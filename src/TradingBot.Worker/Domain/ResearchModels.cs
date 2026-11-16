using System.Text.Json.Serialization;
using TradingBot.Worker.Configuration;

namespace TradingBot.Worker.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<TradingMode>))]
public enum TradingMode { PAPER, LIVE }

public sealed record TradingConfiguration(StrategyOptions Strategy, RiskOptions Risk, PaperTradingOptions Costs, AdaptiveOptions Adaptive);

public sealed record StrategySnapshot(TradingMode Mode, string Version, string ConfigurationHash,
    DateTimeOffset EffectiveAt, string ApprovalStatus, string ConfigurationJson);

public sealed record StrategyRegistration(StrategySnapshot Snapshot, string Status, string? ApprovedBy = null,
    string? PreviousVersion = null, ValidationEvidence? Evidence = null);

public sealed record ValidationEvidence(string ConfigurationHash, string ArtifactHash,
    DateTimeOffset TrainEnd, DateTimeOffset ValidationStart, DateTimeOffset ValidationEnd,
    DateTimeOffset TestStart, DateTimeOffset TestEnd, DateTimeOffset ShadowStart, DateTimeOffset ShadowEnd,
    int WalkForwardFolds, int OutOfSampleTrades, int ShadowTrades, decimal OutOfSampleExpectancy,
    decimal OutOfSampleProfitFactor, decimal MaximumDrawdownPercent, decimal ActiveExpectancy,
    bool BacktestPassed, bool ShadowPassed);

public sealed record TradingJournalEvent(TradingMode Mode, string EventId, string TradeId, string? SignalId,
    string StrategyVersion, string ConfigurationHash, string Symbol, string Kind,
    DateTimeOffset OccurredAt, string PayloadJson);

public sealed record TradingOutcome(TradingMode Mode, string TradeId, string SignalId, StrategySnapshot Strategy,
    string Symbol, TimeFrame TimeFrame, MarketRegime Regime, TradeDirection Direction, decimal Quantity,
    decimal EntryPrice, decimal ExitPrice, decimal InitialStop, decimal TakeProfit, decimal Leverage,
    decimal TradingFees, decimal FundingFees, decimal NetPnl, decimal? RealizedR, decimal Mfe, decimal Mae,
    DateTimeOffset OpenedAt, DateTimeOffset ClosedAt, string EntryReason, string ExitReason,
    decimal? SlippageBasisPoints, decimal? SignalScore, bool Reconciled, string ExcursionBasis, decimal EquityAtEntry = 0m,
    decimal InitialRiskAmount = 0m, decimal? EntryLatencyMilliseconds = null, decimal? ExitSlippageBasisPoints = null);

public sealed record PerformanceSummary(TradingMode Mode, string Group, int TradeCount, int Wins, int Losses,
    decimal? WinRate, decimal? ProfitFactor, decimal? NetExpectancy, decimal? AverageR,
    decimal NetPnl, decimal MaximumDrawdownAmount, decimal? MaximumDrawdownPercent,
    int MaximumConsecutiveLosses, decimal Fees, decimal Funding, decimal AverageHoldingMinutes,
    decimal? AverageSlippageBasisPoints, decimal? AverageMfe, decimal? AverageMae, decimal? ProfitCaptureRatio,
    decimal? AverageEntryLatencyMilliseconds = null, decimal? AverageExitSlippageBasisPoints = null);

public sealed record AdaptiveDecision(bool AllowEntry, decimal RiskMultiplier, string Reason);

public sealed record TradingContext(StrategySnapshot Strategy, string SignalId, string TradeId, decimal Leverage,
    decimal EquityAtEntry, string EntryReason);
