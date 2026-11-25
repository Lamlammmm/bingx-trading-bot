using System.Text.Json.Serialization;

namespace TradingBot.Worker.Domain;

// Append new values so persisted enum numbers retain their original meaning.
public enum TimeFrame { OneMinute, FiveMinutes, FifteenMinutes, OneHour, FourHours, ThirtyMinutes }
public enum TradeDirection { Long, Short }

public sealed record Candle(
    DateTimeOffset OpenTime,
    DateTimeOffset CloseTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    bool IsClosed);

public sealed record MarketUpdate(string Symbol, TimeFrame TimeFrame, Candle Candle, bool IsReconciliation = false);

public sealed record StrategySignal(
    string Symbol,
    TradeDirection Direction,
    DateTimeOffset Time,
    decimal EntryPrice,
    decimal Atr,
    string Reason,
    decimal? SuggestedStopLoss = null,
    decimal? SuggestedTakeProfit = null,
    decimal Confidence = 0m,
    string Source = "technical",
    decimal? InvalidationPrice = null,
    SignalEvaluation? Evaluation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MacdEntryDecision? Macd = null);

public sealed record AiAnalysisContext(
    string Symbol,
    DateTimeOffset CandleTime,
    IReadOnlyList<Candle> EntryCandles,
    IReadOnlyList<Candle> MediumTrendCandles,
    IReadOnlyList<Candle> HigherTrendCandles,
    StrategySignal? TechnicalSignal);

public sealed record AiDecision(
    string Action,
    decimal Confidence,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    string Reason,
    string Invalidation);

public sealed record AiAnalysisResult(
    AiDecision? Decision,
    string? ErrorCode = null,
    int? HttpStatusCode = null,
    string? RequestId = null);

public sealed record TradePlan(
    string Symbol,
    TradeDirection Direction,
    decimal Quantity,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskAmount,
    decimal EntryFee,
    string Reason,
    decimal NetRewardRiskRatio = 0m);

public sealed record TradeFeatures(
    decimal Rsi5m,
    decimal Rsi15m,
    decimal Rsi1h,
    decimal Atr5m,
    decimal EmaFast5m,
    decimal EmaSlow5m,
    decimal VolumeRatio5m,
    decimal BodyRatio5m,
    decimal Confidence,
    int HourOfDayUtc,
    int DayOfWeekUtc);

public sealed record PaperPosition(
    string Symbol,
    TradeDirection Direction,
    decimal Quantity,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskAmount,
    decimal EntryFee,
    DateTimeOffset OpenedAt,
    TradeFeatures? EntryFeatures = null,
    decimal InitialStopLoss = 0m,
    bool PartialTaken = false,
    decimal InitialQuantity = 0m,
    decimal RealizedPartialPnl = 0m,
    PositionMetrics? Metrics = null);

public sealed record ContractInfo(
    string Symbol,
    decimal QuantityPrecision,
    decimal PricePrecision,
    decimal MinimumQuantity,
    decimal MinimumOrderValue,
    bool IsActive);

public sealed record AccountState(
    decimal Balance,
    DateOnly BalanceDate,
    decimal RealizedPnlToday,
    int TotalWins = 0,
    int TotalLosses = 0,
    decimal TotalNetPnl = 0m,
    RiskControlState? RiskControls = null);

public sealed record TradePerformance(int Wins, int Losses, decimal TotalNetPnl);

public sealed record ClosedTrade(
    string Symbol,
    TradeDirection Direction,
    decimal Quantity,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal NetPnl,
    string ExitReason,
    string Source,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    TradeFeatures? EntryFeatures = null,
    TradeMetrics? Metrics = null);

public enum MarketRegime { TrendingUp, TrendingDown, Sideways, HighVolatility, LowVolatility, Unknown }

public sealed record SignalEvaluation(DateTimeOffset Time, MarketRegime Regime, string Reason,
    decimal Adx = 0m, decimal AtrPercent = 0m, decimal Ema20 = 0m, decimal Ema50 = 0m,
    decimal Ema200 = 0m, decimal Rsi15m = 50m, string Structure = "unconfirmed",
    string? ReasonCode = null, string[]? SecondaryReasons = null, decimal? AtrValue = null,
    decimal? RsiEntry = null, decimal? VolumeRatio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Setup = null);

public sealed record PositionMetrics(
    int TakeProfitStage = 0, decimal Mfe = 0m, decimal Mae = 0m,
    decimal FundingPaid = 0m, decimal ExitFees = 0m, decimal ExitNotional = 0m,
    DateTimeOffset? LastEvaluatedAt = null, DateTimeOffset? LastStopAdjustedAt = null,
    decimal RequestedEntryPrice = 0m, MarketRegime Regime = MarketRegime.Unknown,
    TimeFrame RiskTimeFrame = TimeFrame.FiveMinutes, TradingContext? Context = null, decimal? EntryLatencyMilliseconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MacdEntryDecision? Macd = null);

public sealed record TradeMetrics(decimal InitialRiskAmount, decimal Mfe, decimal Mae,
    decimal FundingPaid, decimal TotalFees, decimal EntrySlippageBasisPoints,
    decimal FinalExitSlippageBasisPoints, decimal AverageExitPrice, decimal HoldingMinutes,
    MarketRegime Regime, TradingContext? Context = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MacdEntryDecision? Macd = null);

public sealed record RiskControlState(decimal DayStartBalance, int ConsecutiveLosses = 0,
    DateTimeOffset? CircuitOpenUntil = null, Dictionary<string, DateTimeOffset>? LastExits = null,
    DateTimeOffset? TradingDateTime = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? PeakClosedEquity = null);

public sealed record StoredPaperTrade(long Id, ClosedTrade Trade, StoredTradeFeatures Features);

public sealed record StoredTradeFeatures(
    decimal? Rsi5m, decimal? Rsi15m, decimal? Rsi1h, decimal? Atr5m,
    decimal? EmaFast5m, decimal? EmaSlow5m, decimal? VolumeRatio5m,
    decimal? BodyRatio5m, decimal? Confidence, int? HourOfDayUtc, int? DayOfWeekUtc);
