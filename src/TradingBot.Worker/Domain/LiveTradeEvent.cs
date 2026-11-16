namespace TradingBot.Worker.Domain;

public sealed record LiveTradeEvent(string EventId, string TradeId, string Symbol,
    string Kind, DateTimeOffset OccurredAt, string PayloadJson);

public sealed record LiveOrderReceipt(decimal? AveragePrice, string ResponseJson);

public sealed record LiveAccountSnapshot(decimal WalletBalance, decimal Equity, decimal AvailableMargin,
    decimal DayStartBalance, decimal NetTradingIncomeToday, decimal TakerFeeRate = 0.0005m, decimal ExternalIncomeToday = 0m);

public sealed record LiveIncomeSnapshot(decimal NetPnl, decimal FundingIncome, decimal TradingFees, int Records,
    bool HasRealizedPnl = false, bool HasTradingFee = false, decimal RealizedPnl = 0m);

public sealed record LiveManagedPosition(string PositionId, string Symbol, TradeDirection Direction,
    decimal Quantity, decimal EntryPrice, string StopOrderId, string TakeOrderId, bool StopClosesPosition = false);

public sealed record LivePositionSnapshot(string PositionId, string Symbol, TradeDirection Direction, decimal Quantity, decimal EntryPrice);

public sealed class LiveProtectionReplacementException(string message) : InvalidOperationException(message);

public sealed record LiveOrderFill(string OrderId, string ClientOrderId, string Symbol, string Side,
    string PositionSide, string Status, decimal ExecutedQuantity, decimal AveragePrice);

public sealed record LivePositionManagement(string PositionId, decimal EntryPrice, decimal InitialQuantity,
    decimal InitialStopLoss, decimal InitialRiskAmount, string StopOrderId, string TakeOrderId,
    PositionMetrics Metrics, decimal FeeRate = 0m);
