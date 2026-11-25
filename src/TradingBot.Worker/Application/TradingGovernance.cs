using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class TradingGovernance(ITradingResearchStore store, IOptions<StrategyOptions> strategy,
    IOptions<RiskOptions> risk, IOptions<PaperTradingOptions> costs, IOptions<AdaptiveOptions> adaptive,
    IOptions<LiveOptions> live, ILogger<TradingGovernance> logger)
{
    private readonly ConcurrentDictionary<(TradingMode, string), TradingPolicyRuntime> _policies = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ITradingResearchStore Store => store;

    public async Task InitializeAsync(TradingMode mode, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await store.InitializeAsync(token);
            if (await store.GetActiveAsync(mode, token) is not null) return;
            var r = JsonSerializer.Deserialize<RiskOptions>(JsonSerializer.Serialize(risk.Value))!;
            if (mode == TradingMode.LIVE)
            {
                r.RiskPerTradePercent = live.Value.RiskPerTradePercent ?? r.RiskPerTradePercent;
                r.MaxLeverage = Math.Min(r.MaxLeverage, live.Value.MaxLeverage);
            }
            var entryOptions = strategy.Value;
            if (mode == TradingMode.LIVE && entryOptions.UseAdaptiveCurrentEntries)
            {
                // Bootstrap is not evidence or operator approval of the adaptive entry policy.
                entryOptions = JsonSerializer.Deserialize<StrategyOptions>(JsonSerializer.Serialize(entryOptions))!;
                entryOptions.UseAdaptiveCurrentEntries = false;
                logger.LogWarning("Bootstrapping legacy Current for Live. Adaptive Current requires a validated and approved policy.");
            }
            var configuration = new TradingConfiguration(entryOptions, r, costs.Value, adaptive.Value);
            ValidateConfiguration(configuration);
            var json = JsonSerializer.Serialize(configuration);
            var snapshot = new StrategySnapshot(mode, adaptive.Value.BootstrapVersion, TradingPolicyRuntime.Hash(json), DateTimeOffset.UtcNow,
                "APPROVED", json);
            // This imports the operator's existing configuration exactly once. It never approves a generated candidate.
            await store.RegisterAsync(new(snapshot, "APPROVED", "operator-controlled initial configuration"), true, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<TradingPolicyRuntime> ActiveAsync(TradingMode mode, CancellationToken token)
    {
        var registration = await store.GetActiveAsync(mode, token) ?? throw new InvalidOperationException("No approved active strategy.");
        if (registration.Status != "APPROVED" || registration.Snapshot.ApprovalStatus != "APPROVED")
            throw new InvalidOperationException("An unapproved strategy cannot generate orders.");
        var key = (mode, registration.Snapshot.Version + registration.Snapshot.EffectiveAt.UtcTicks + registration.Snapshot.ConfigurationHash);
        return _policies.GetOrAdd(key, _ =>
        {
            var policy = new TradingPolicyRuntime(registration.Snapshot);
            ValidateConfiguration(policy.Configuration);
            if (mode == TradingMode.LIVE && policy.Configuration.Strategy.UseAdaptiveCurrentEntries)
            {
                if (string.IsNullOrWhiteSpace(registration.ApprovedBy))
                    throw new InvalidOperationException("Live adaptive Current requires explicit operator approval.");
                TradingPolicyRuntime.ValidateEvidence(registration);
            }
            return policy;
        });
    }

    public TradingPolicyRuntime Pinned(StrategySnapshot snapshot) => new(snapshot);

    public async Task<AdaptiveDecision> DecideAsync(TradingPolicyRuntime policy, MarketRegime regime, decimal? spread, CancellationToken token)
    {
        var outcomes = await OutcomesAsync(policy.Snapshot.Mode, DateTimeOffset.UtcNow.AddDays(-policy.Configuration.Adaptive.LookbackDays), token);
        var relevant = outcomes.Where(t => t.Strategy.ConfigurationHash == policy.Snapshot.ConfigurationHash && t.Regime == regime).ToArray();
        var capital = outcomes.FirstOrDefault()?.EquityAtEntry ?? policy.Configuration.Risk.StartingBalance;
        var summary = PerformanceAnalyzer.Analyze(policy.Snapshot.Mode, relevant, capital)[0];
        var all = PerformanceAnalyzer.Analyze(policy.Snapshot.Mode, outcomes, capital)[0];
        if (all.MaximumDrawdownPercent >= policy.Configuration.Risk.MaxDrawdownPercent)
            return new(false, 0m, "realized drawdown limit reached");
        return PerformanceAnalyzer.Decide(policy.Configuration, summary, spread, await store.IsHaltedAsync(policy.Snapshot.Mode, token));
    }

    public Task SignalAsync(StrategySnapshot snapshot, string signalId, StrategySignal signal, AdaptiveDecision decision, CancellationToken token) =>
        store.AppendAsync(new(snapshot.Mode, signalId, signalId, signalId, snapshot.Version, snapshot.ConfigurationHash,
            signal.Symbol, "signal", signal.Time, JsonSerializer.Serialize(new { Signal = signal, Decision = decision,
                TimeFrame = "5m", SignalScore = (decimal?)null, ScoreInterpretation = "No uncalibrated probability score is invented." })), token);

    public Task MirrorLiveAsync(LiveTradeEvent entry, TradingContext? context, CancellationToken token) =>
        store.AppendAsync(new(TradingMode.LIVE, entry.EventId, entry.TradeId, context?.SignalId,
            context?.Strategy.Version ?? "legacy-unversioned", context?.Strategy.ConfigurationHash ?? "", entry.Symbol,
            entry.Kind, entry.OccurredAt, entry.PayloadJson), token);

    public static TradingJournalEvent OutcomeEvent(TradingOutcome outcome)
    {
        var json = JsonSerializer.Serialize(outcome);
        return new(outcome.Mode, outcome.TradeId + ":closed:" + TradingPolicyRuntime.Hash(json)[..16], outcome.TradeId,
            outcome.SignalId, outcome.Strategy.Version, outcome.Strategy.ConfigurationHash, outcome.Symbol, "trade_closed",
            DateTimeOffset.UtcNow, json);
    }

    public async Task<IReadOnlyList<TradingOutcome>> OutcomesAsync(TradingMode mode, DateTimeOffset since, CancellationToken token)
    {
        var outcomes = new Dictionary<string, TradingOutcome>();
        await foreach (var entry in store.ReadAsync(mode, since, token, "trade_closed"))
        {
            if (entry.Kind != "trade_closed") continue;
            var outcome = JsonSerializer.Deserialize<TradingOutcome>(entry.PayloadJson)!;
            if (outcome.Mode != mode) throw new InvalidOperationException("Journal mode and payload mode do not match.");
            outcomes[outcome.TradeId] = outcome;
        }
        return outcomes.Values.Where(t => t.ClosedAt >= since).OrderBy(t => t.ClosedAt).ToArray();
    }

    public async Task<IReadOnlyList<PerformanceSummary>> AnalyzeAsync(TradingMode mode, CancellationToken token)
    {
        var policy = await ActiveAsync(mode, token);
        var outcomes = await OutcomesAsync(mode, DateTimeOffset.UtcNow.AddDays(-policy.Configuration.Adaptive.LookbackDays), token);
        var capital = outcomes.FirstOrDefault()?.Strategy is null ? policy.Configuration.Risk.StartingBalance
            : outcomes[0].EquityAtEntry;
        var summaries = PerformanceAnalyzer.Analyze(mode, outcomes, capital);
        var reportId = "analytics:" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (policy.Configuration.Adaptive.AnalysisIntervalMinutes * 60);
        await store.AppendAsync(new(mode, reportId, reportId, null, policy.Snapshot.Version, policy.Snapshot.ConfigurationHash,
            "", "performance", DateTimeOffset.UtcNow, JsonSerializer.Serialize(new { Summaries = summaries,
                DrawdownBasis = "closed-trade equity curve; excludes intratrade equity changes", PendingReconciliation = outcomes.Count(t => !t.Reconciled) })), token);
        var degraded = summaries.FirstOrDefault(s => s.Group == "strategy:" + policy.Snapshot.Version);
        if (degraded is { } s && s.TradeCount >= policy.Configuration.Adaptive.MinimumTrades && s.NetExpectancy <= 0m)
        {
            var candidate = JsonSerializer.Deserialize<TradingConfiguration>(policy.Snapshot.ConfigurationJson)!;
            candidate.Strategy.SignalCooldownBars += 3;
            var json = JsonSerializer.Serialize(candidate); var hash = TradingPolicyRuntime.Hash(json);
            var snapshot = new StrategySnapshot(mode, "candidate-" + hash[..16], hash, DateTimeOffset.UtcNow, "CANDIDATE", json);
            await store.RegisterAsync(new(snapshot, "CANDIDATE", PreviousVersion: policy.Snapshot.Version), false, token);
            logger.LogWarning("{Mode} degradation detected for {Version}; candidate {Candidate} requires backtest/OOS/shadow evidence and human approval", mode, policy.Snapshot.Version, snapshot.Version);
        }
        return summaries;
    }

    public async Task<IReadOnlyList<PerformanceSummary>> PerformanceAsync(TradingMode mode, CancellationToken token)
    {
        var policy = await ActiveAsync(mode, token);
        var outcomes = await OutcomesAsync(mode, DateTimeOffset.UtcNow.AddDays(-policy.Configuration.Adaptive.LookbackDays), token);
        return PerformanceAnalyzer.Analyze(mode, outcomes, outcomes.FirstOrDefault()?.EquityAtEntry ?? policy.Configuration.Risk.StartingBalance);
    }

    public async Task ValidateCandidateAsync(TradingMode mode, string version, ValidationEvidence evidence, CancellationToken token)
    {
        var registration = await store.GetVersionAsync(mode, version, token) ?? throw new InvalidOperationException("Unknown candidate.");
        if (registration.Status != "CANDIDATE") throw new InvalidOperationException("Only candidates can be validated.");
        await store.RegisterAsync(registration with { Status = "VALIDATED", Evidence = evidence }, false, token);
    }

    private static void ValidateConfiguration(TradingConfiguration configuration)
    {
        if (configuration.Adaptive.MinimumTrades < 10 || configuration.Adaptive.LookbackDays < 1 ||
            configuration.Adaptive.AnalysisIntervalMinutes < 1 || configuration.Adaptive.MaximumSpreadBasisPoints <= 0m ||
            configuration.Adaptive.DegradedRiskMultiplier <= 0m || configuration.Adaptive.DegradedRiskMultiplier > 1m ||
            configuration.Risk.MaxDrawdownPercent <= 0m || configuration.Risk.MaxAggregateExposurePercent <= 0m)
            throw new InvalidOperationException("Invalid approved adaptive/risk configuration.");
    }
}
