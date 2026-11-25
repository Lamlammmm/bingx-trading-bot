using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class TradingPolicyRuntime
{
    public StrategySnapshot Snapshot { get; }
    public TradingConfiguration Configuration { get; }
    public ITradingStrategy Strategy { get; }
    public TradingPolicyRuntime(StrategySnapshot snapshot)
    {
        if (Hash(snapshot.ConfigurationJson) != snapshot.ConfigurationHash) throw new InvalidOperationException("Strategy configuration hash mismatch.");
        Snapshot = snapshot;
        Configuration = JsonSerializer.Deserialize<TradingConfiguration>(snapshot.ConfigurationJson)!;
        if (Configuration.Strategy.UseAdaptiveCurrentEntries && !Configuration.Strategy.UseMultiTimeframeAnalysis)
            throw new InvalidOperationException("Adaptive Current entries require multi-timeframe analysis.");
        Strategy = Configuration.Strategy.UseMultiTimeframeAnalysis
            ? new MultiTimeframeStrategy(Options.Create(Configuration.Strategy)) : new BreakoutStrategy(Options.Create(Configuration.Strategy));
    }

    public static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

    public TradePlan? Plan(StrategySignal signal, decimal capital, decimal availableMargin, decimal aggregateRisk,
        decimal aggregateNotional, ContractInfo contract, decimal leverage, decimal riskMultiplier, decimal feeRate = 0m)
    {
        if (riskMultiplier <= 0m || riskMultiplier > 1m || leverage < 1m || leverage > Configuration.Risk.MaxLeverage) return null;
        var risk = JsonSerializer.Deserialize<RiskOptions>(JsonSerializer.Serialize(Configuration.Risk))!;
        risk.RiskPerTradePercent *= riskMultiplier;
        risk.MaxLeverage = leverage;
        risk.FeeRate = Math.Max(risk.FeeRate, feeRate);
        var plan = new RiskManager(Options.Create(risk)).CreatePlan(signal, capital, null, 0m, aggregateRisk, contract);
        if (plan is null) return null;
        var notionalHeadroom = capital * risk.MaxAggregateExposurePercent / 100m - aggregateNotional;
        var quantity = decimal.Round(Math.Min(plan.Quantity, Math.Min(
            availableMargin / (plan.EntryPrice / leverage + plan.EntryPrice * risk.FeeRate * 2m),
            notionalHeadroom / plan.EntryPrice)), (int)contract.QuantityPrecision, MidpointRounding.ToZero);
        if (quantity <= 0m || quantity < contract.MinimumQuantity || quantity * plan.EntryPrice < contract.MinimumOrderValue) return null;
        return plan with { Quantity = quantity, RiskAmount = plan.RiskAmount * quantity / plan.Quantity, EntryFee = plan.EntryFee * quantity / plan.Quantity };
    }

    public static void ValidateEvidence(StrategyRegistration registration, string? expectedActiveVersion = null)
    {
        var e = registration.Evidence ?? throw new InvalidOperationException("Validation evidence is required.");
        var cfg = JsonSerializer.Deserialize<TradingConfiguration>(registration.Snapshot.ConfigurationJson)!;
        if (cfg.Strategy.UseAdaptiveCurrentEntries && !cfg.Strategy.UseMultiTimeframeAnalysis)
            throw new InvalidOperationException("Adaptive Current evidence requires multi-timeframe analysis.");
        if (cfg.Strategy.UseAdaptiveCurrentEntries && expectedActiveVersion is not null && registration.PreviousVersion != expectedActiveVersion)
            throw new InvalidOperationException("Adaptive Current evidence must compare against the active baseline.");
        if (e.ConfigurationHash != registration.Snapshot.ConfigurationHash || e.ArtifactHash.Length != 64 || !e.ArtifactHash.All(Uri.IsHexDigit) ||
            !e.BacktestPassed || !e.ShadowPassed || e.WalkForwardFolds < 3 || e.OutOfSampleTrades < cfg.Adaptive.MinimumTrades ||
            e.ShadowTrades < cfg.Adaptive.MinimumTrades || e.OutOfSampleExpectancy <= 0m || e.OutOfSampleExpectancy <= e.ActiveExpectancy ||
            e.OutOfSampleProfitFactor <= 1m || e.MaximumDrawdownPercent < 0m || e.MaximumDrawdownPercent > cfg.Risk.MaxDrawdownPercent ||
            e.TrainEnd >= e.ValidationStart || e.ValidationStart >= e.ValidationEnd || e.ValidationEnd >= e.TestStart ||
            e.TestStart >= e.TestEnd || e.TestEnd >= e.ShadowStart || e.ShadowStart >= e.ShadowEnd || e.ShadowEnd > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Candidate has not passed chronological backtest/walk-forward/OOS/shadow and active-strategy comparison gates.");
    }

    public static void ValidateCandidate(StrategySnapshot active, StrategySnapshot candidate)
    {
        if (active.Mode != candidate.Mode) throw new InvalidOperationException("A strategy cannot change trading mode.");
        var a = JsonNode.Parse(active.ConfigurationJson)!; var b = JsonNode.Parse(candidate.ConfigurationJson)!;
        foreach (var (section, names) in new[]
        {
            ("Strategy", new[] { "MinimumAdx", "MinimumAtrPercent", "MaximumAtrPercent", "MaximumSetupDistanceAtr", "SignalCooldownBars", "MinimumBodyRatio", "VolumeMultiplier", "RetestConfirmationBars", "MaxRetestEntryDistanceAtr", "UseAdaptiveCurrentEntries" }),
            ("Risk", new[] { "StopAtrMultiplier", "TrailingAtrMultiplier", "BreakEvenTriggerR", "TrailingStartR", "TakeProfitRiskMultiple", "PartialTakeProfitRMultiple", "SecondTakeProfitRMultiple", "ExitCooldownBars" })
        })
            foreach (var name in names) { a[section]!.AsObject().Remove(name); b[section]!.AsObject().Remove(name); }
        if (!JsonNode.DeepEquals(a, b)) throw new InvalidOperationException("Candidate changes a non-optimizable risk, execution-cost or operating setting.");
    }
}
