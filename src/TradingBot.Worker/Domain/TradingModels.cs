namespace TradingBot.Worker.Domain;

public enum TimeFrame { FifteenMinutes, OneHour, FourHours }
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

public sealed record MarketUpdate(TimeFrame TimeFrame, Candle Candle);

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
    string Source = "technical");

public sealed record AiAnalysisContext(
    string Symbol,
    DateTimeOffset CandleTime,
    IReadOnlyList<Candle> FifteenMinuteCandles,
    IReadOnlyList<Candle> OneHourCandles,
    IReadOnlyList<Candle> FourHourCandles,
    StrategySignal? TechnicalSignal);

public sealed record AiDecision(
    string Action,
    decimal Confidence,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    string Reason,
    string Invalidation);

public sealed record TradePlan(
    string Symbol,
    TradeDirection Direction,
    decimal Quantity,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskAmount,
    decimal EntryFee,
    string Reason);

public sealed record PaperPosition(
    string Symbol,
    TradeDirection Direction,
    decimal Quantity,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskAmount,
    decimal EntryFee,
    DateTimeOffset OpenedAt);

public sealed record ContractInfo(
    string Symbol,
    decimal QuantityPrecision,
    decimal PricePrecision,
    decimal MinimumQuantity,
    decimal MinimumOrderValue,
    bool IsActive);
