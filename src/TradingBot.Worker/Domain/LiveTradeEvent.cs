namespace TradingBot.Worker.Domain;

public sealed record LiveTradeEvent(string EventId, string TradeId, string Symbol,
    string Kind, DateTimeOffset OccurredAt, string PayloadJson);

public sealed record LiveOrderReceipt(decimal? AveragePrice, string ResponseJson);
