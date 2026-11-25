using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Persistence;

namespace TradingBot.Tests;

public sealed class TradingGovernanceTests
{
    [Fact]
    public void AnalyticsSeparatesModesAndExcludesUnreconciledResults()
    {
        var snapshot = Snapshot(TradingMode.PAPER);
        var rows = new[] { Outcome(snapshot, "a", -10m), Outcome(snapshot, "b", 20m), Outcome(snapshot, "c", -5m),
            Outcome(Snapshot(TradingMode.LIVE), "a", 1000m), Outcome(snapshot, "d", 5000m) with { Reconciled = false } };
        var report = PerformanceAnalyzer.Analyze(TradingMode.PAPER, rows, 100m)[0];
        Assert.Equal(3, report.TradeCount); Assert.Equal(5m, report.NetPnl);
        Assert.Equal(1m / 3m, report.WinRate); Assert.Equal(20m / 15m, report.ProfitFactor);
        Assert.Equal(5m / 3m, report.NetExpectancy); Assert.Equal(10m, report.MaximumDrawdownAmount);
        Assert.Equal(10m, report.MaximumDrawdownPercent); Assert.Equal(3m, report.Fees);
        Assert.Equal(1, report.MaximumConsecutiveLosses);
    }

    [Theory]
    [InlineData(40, -1, 1, false, 0.5, true)]
    [InlineData(10, -1, 1, false, 1, true)]
    [InlineData(40, 1, 1, false, 1, true)]
    [InlineData(40, 1, 30, false, 0, false)]
    [InlineData(40, 1, 1, true, 0, false)]
    public void DeterministicAdaptationOnlyReducesRisk(int count, int pnl, int spread, bool halted, double multiplier, bool allowed)
    {
        var configuration = JsonSerializer.Deserialize<TradingConfiguration>(Snapshot(TradingMode.LIVE).ConfigurationJson)!;
        var rows = Enumerable.Range(0, count).Select(i => Outcome(Snapshot(TradingMode.LIVE), i.ToString(), pnl));
        var summary = PerformanceAnalyzer.Analyze(TradingMode.LIVE, rows, 10_000m)[0];
        var decision = PerformanceAnalyzer.Decide(configuration, summary, spread, halted);
        Assert.Equal(allowed, decision.AllowEntry); Assert.Equal((decimal)multiplier, decision.RiskMultiplier);
        Assert.InRange(decision.RiskMultiplier, 0m, 1m);
    }

    [Fact]
    public async Task CandidateCannotPromoteWithoutValidationAndExplicitApprovalAndCannotRaiseRisk()
    {
        using var fixture = new ResearchFixture();
        await fixture.Governance.InitializeAsync(TradingMode.LIVE, default);
        var active = await fixture.Governance.ActiveAsync(TradingMode.LIVE, default);
        var configuration = JsonSerializer.Deserialize<TradingConfiguration>(active.Snapshot.ConfigurationJson)!;
        configuration.Risk.RiskPerTradePercent *= 2m;
        var json = JsonSerializer.Serialize(configuration);
        var bad = active.Snapshot with { Version = "raised-risk", ConfigurationJson = json, ConfigurationHash = TradingPolicyRuntime.Hash(json), ApprovalStatus = "CANDIDATE" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.RegisterAsync(new(bad, "CANDIDATE"), false, default));
        configuration.Risk.RiskPerTradePercent /= 2m; configuration.Risk.TrailingAtrMultiplier *= 1.2m;
        json = JsonSerializer.Serialize(configuration);
        var candidate = bad with { Version = "candidate-test", ConfigurationJson = json, ConfigurationHash = TradingPolicyRuntime.Hash(json) };
        await fixture.Store.RegisterAsync(new(candidate, "CANDIDATE"), false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.PromoteAsync(TradingMode.LIVE, candidate.Version, active.Snapshot.Version, "reviewer", "APPROVE", default));
        var e = Evidence(candidate.ConfigurationHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Governance.ValidateCandidateAsync(TradingMode.LIVE, candidate.Version, e with { OutOfSampleTrades = 0 }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Governance.ValidateCandidateAsync(TradingMode.LIVE, candidate.Version, e with { TestStart = e.TrainEnd }, default));
        await fixture.Governance.ValidateCandidateAsync(TradingMode.LIVE, candidate.Version, e, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.PromoteAsync(TradingMode.LIVE, candidate.Version, active.Snapshot.Version, "", "APPROVE", default));
        await fixture.Store.PromoteAsync(TradingMode.LIVE, candidate.Version, active.Snapshot.Version, "synthetic unit-test approval", "APPROVE", default);
        var promoted = await fixture.Governance.ActiveAsync(TradingMode.LIVE, default);
        Assert.Equal(2.4m, promoted.Configuration.Risk.TrailingAtrMultiplier);
        Assert.Equal(2m, fixture.Governance.Pinned(active.Snapshot).Configuration.Risk.TrailingAtrMultiplier);
        Assert.Equal(2m, active.Configuration.Risk.TrailingAtrMultiplier);
        await fixture.Store.PromoteAsync(TradingMode.LIVE, active.Snapshot.Version, promoted.Snapshot.Version, "synthetic unit-test approval", "ROLLBACK", default);
        Assert.Equal(active.Snapshot.Version, (await fixture.Governance.ActiveAsync(TradingMode.LIVE, default)).Snapshot.Version);
    }

    [Fact]
    public async Task AdaptiveCurrentLiveRequiresPromotionAndCanRollBackToLegacy()
    {
        var requested = new StrategyOptions { UseMultiTimeframeAnalysis = true, UseAdaptiveCurrentEntries = true };
        using var fixture = new ResearchFixture(requested);
        await fixture.Governance.InitializeAsync(TradingMode.LIVE, default);
        var legacy = await fixture.Governance.ActiveAsync(TradingMode.LIVE, default);
        Assert.False(legacy.Configuration.Strategy.UseAdaptiveCurrentEntries);
        Assert.True(requested.UseAdaptiveCurrentEntries);

        var json = JsonSerializer.Serialize(legacy.Configuration with { Strategy = requested });
        var candidate = legacy.Snapshot with
        {
            Version = "current-adaptive-test",
            ConfigurationJson = json,
            ConfigurationHash = TradingPolicyRuntime.Hash(json),
            ApprovalStatus = "CANDIDATE"
        };
        await fixture.Store.RegisterAsync(new(candidate, "CANDIDATE", PreviousVersion: legacy.Snapshot.Version), false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.PromoteAsync(
            TradingMode.LIVE, candidate.Version, legacy.Snapshot.Version, "synthetic reviewer", "APPROVE", default));
        var evidence = Evidence(candidate.ConfigurationHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Governance.ValidateCandidateAsync(
            TradingMode.LIVE, candidate.Version, evidence with { ShadowPassed = false }, default));
        await fixture.Governance.ValidateCandidateAsync(TradingMode.LIVE, candidate.Version, evidence, default);
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.PromoteAsync(
            TradingMode.LIVE, candidate.Version, "different-baseline", "synthetic reviewer", "APPROVE", default));
        Assert.Contains("active baseline", stale.Message);
        await fixture.Store.PromoteAsync(TradingMode.LIVE, candidate.Version,
            legacy.Snapshot.Version, "synthetic reviewer", "APPROVE", default);
        Assert.True((await fixture.Governance.ActiveAsync(TradingMode.LIVE, default)).Configuration.Strategy.UseAdaptiveCurrentEntries);

        await fixture.Store.PromoteAsync(TradingMode.LIVE, legacy.Snapshot.Version,
            candidate.Version, "synthetic reviewer", "ROLLBACK", default);
        Assert.False((await fixture.Governance.ActiveAsync(TradingMode.LIVE, default)).Configuration.Strategy.UseAdaptiveCurrentEntries);
    }

    [Fact]
    public async Task JournalIsIdempotentAndPaperPerformanceDoesNotAuthorizeLiveRisk()
    {
        using var f = new ResearchFixture();
        foreach (var mode in new[] { TradingMode.PAPER, TradingMode.LIVE })
        {
            await f.Governance.InitializeAsync(mode, default);
            var p = await f.Governance.ActiveAsync(mode, default);
            for (var i = 0; i < 30; i++)
            {
                var row = Outcome(p.Snapshot, i.ToString(), mode == TradingMode.PAPER ? 10m : -1m);
                var e = TradingGovernance.OutcomeEvent(row);
                await f.Store.AppendAsync(e, default); await f.Store.AppendAsync(e, default);
            }
            var decision = await f.Governance.DecideAsync(p, MarketRegime.TrendingUp, 1m, default);
            Assert.Equal(mode == TradingMode.PAPER ? 1m : 0.5m, decision.RiskMultiplier);
            Assert.Equal(30, (await f.Governance.OutcomesAsync(mode, DateTimeOffset.UtcNow.AddDays(-1), default)).Count);
        }
        await f.Governance.AnalyzeAsync(TradingMode.LIVE, default);
        Assert.Equal("mtf-v1", (await f.Store.GetActiveAsync(TradingMode.LIVE, default))!.Snapshot.Version);
        await f.Store.SetHaltAsync(TradingMode.LIVE, true, "test kill switch", default);
        var live = await f.Governance.ActiveAsync(TradingMode.LIVE, default);
        Assert.False((await f.Governance.DecideAsync(live, MarketRegime.TrendingUp, 1m, default)).AllowEntry);
        var paper = await f.Governance.ActiveAsync(TradingMode.PAPER, default);
        Assert.True((await f.Governance.DecideAsync(paper, MarketRegime.TrendingUp, 1m, default)).AllowEntry);
    }

    [Fact]
    public async Task PaperAccountPositionAndUnifiedJournalRollbackTogether()
    {
        using var f = new ResearchFixture();
        await f.Trades.InitializeAsync(default); await f.Governance.InitializeAsync(TradingMode.PAPER, default);
        var account = new AccountState(1000m, DateOnly.FromDateTime(DateTime.UtcNow), 0m);
        await f.Trades.CommitPaperStateAsync(account, [], null, default);
        await using (var c = new SqliteConnection(f.Connection))
        {
            await c.OpenAsync(); await using var q = c.CreateCommand();
            q.CommandText = "CREATE TRIGGER fail_unified BEFORE INSERT ON trading_journal BEGIN SELECT RAISE(ABORT,'injected journal failure'); END";
            await q.ExecuteNonQueryAsync();
        }
        var snapshot = (await f.Governance.ActiveAsync(TradingMode.PAPER, default)).Snapshot;
        var e = TradingGovernance.OutcomeEvent(Outcome(snapshot, "rollback", 10m));
        var position = new PaperPosition("BTC-USDT", TradeDirection.Long, 1m, 100m, 90m, 120m, 10m, 0m, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<SqliteException>(() => f.Trades.CommitPaperStateWithJournalAsync(account with { Balance = 999m }, [position], null, [e], default));
        Assert.Equal(1000m, (await f.Trades.LoadAccountStateAsync(default))!.Balance);
        Assert.Empty(await f.Trades.LoadOpenPositionsAsync(default));
        Assert.Empty(await f.Governance.OutcomesAsync(TradingMode.PAPER, DateTimeOffset.MinValue, default));
    }

    [Fact]
    public void PaperAndLivePolicyUseIdenticalPlanningFormulasForIdenticalInputs()
    {
        var paper = new TradingPolicyRuntime(Snapshot(TradingMode.PAPER));
        var live = new TradingPolicyRuntime(Snapshot(TradingMode.LIVE));
        var contract = new ContractInfo("BTC-USDT", 4, 2, 0.0001m, 1m, true);
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 2m, "same inputs");
        var a = paper.Plan(signal, 1000m, 1000m, 0m, 0m, contract, 5m, 1m);
        var b = live.Plan(signal, 1000m, 1000m, 0m, 0m, contract, 5m, 1m);
        Assert.NotNull(a); Assert.Equal(a, b);
        var reduced = live.Plan(signal, 1000m, 1000m, 0m, 0m, contract, 5m, 0.5m);
        Assert.True(reduced!.Quantity < a.Quantity); Assert.Equal(a.StopLoss, reduced.StopLoss); Assert.Equal(a.TakeProfit, reduced.TakeProfit);
        Assert.Null(live.Plan(signal, 1000m, 1000m, 0m, 0m, contract, 5m, 2m));
        var capped = live.Plan(signal, 1000m, 1000m, 0m, 2999m, contract, 5m, 1m);
        Assert.NotNull(capped); Assert.True(2999m + capped.EntryPrice * capped.Quantity <= 3000m);
        Assert.Null(live.Plan(signal, 1000m, 1000m, 0m, 3000m, contract, 5m, 1m));
    }

    internal static StrategySnapshot Snapshot(TradingMode mode)
    {
        var json = JsonSerializer.Serialize(new TradingConfiguration(new StrategyOptions(), new RiskOptions { StartingBalance = 10000m, TargetProfitUsdt = 0m },
            new PaperTradingOptions(), new AdaptiveOptions()));
        return new(mode, "test-v1", TradingPolicyRuntime.Hash(json), DateTimeOffset.UtcNow, "APPROVED", json);
    }
    internal static TradingOutcome Outcome(StrategySnapshot snapshot, string id, decimal pnl) => new(snapshot.Mode, id, "signal-" + id,
        snapshot, "BTC-USDT", TimeFrame.FiveMinutes, MarketRegime.TrendingUp, TradeDirection.Long, 1m, 100m, 100m + pnl, 90m, 120m,
        5m, 1m, 0.1m, pnl, pnl / 10m, 12m, 3m, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1),
        "synthetic fixture", "SL", 1m, null, true, "synthetic fixture", 10000m, 10m);
    internal static ValidationEvidence Evidence(string hash)
    {
        var now = DateTimeOffset.UtcNow;
        return new(hash, new string('a', 64), now.AddDays(-50), now.AddDays(-49), now.AddDays(-40), now.AddDays(-39), now.AddDays(-30),
            now.AddDays(-29), now.AddDays(-1), 3, 30, 30, 1m, 1.5m, 2m, 0m, true, true);
    }
}

internal sealed class ResearchFixture : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "research-" + Guid.NewGuid().ToString("N") + ".db");
    public string Connection => "Data Source=" + _path;
    public SqliteTradeStore Trades { get; }
    public ITradingResearchStore Store { get; }
    public TradingGovernance Governance { get; }
    public ResearchFixture(StrategyOptions? strategy = null, RiskOptions? risk = null)
    {
        Trades = new SqliteTradeStore(Options.Create(new PersistenceOptions { DatabasePath = _path }));
        Store = Trades.Research;
        Governance = new(Store, Options.Create(strategy ?? new StrategyOptions()), Options.Create(risk ?? new RiskOptions()),
            Options.Create(new PaperTradingOptions()), Options.Create(new AdaptiveOptions()), Options.Create(new LiveOptions()), NullLogger<TradingGovernance>.Instance);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (File.Exists(_path)) File.Delete(_path); }
}
