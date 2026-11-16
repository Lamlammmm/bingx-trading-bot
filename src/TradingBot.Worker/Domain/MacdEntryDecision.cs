namespace TradingBot.Worker.Domain;

public sealed record MacdEntryDecision(bool Allowed, string Reason, TimeFrame TimeFrame, DateTimeOffset CandleCloseTime,
    int FastPeriod, int SlowPeriod, int SignalPeriod, decimal? Macd = null, decimal? Signal = null,
    decimal? PreviousHistogram = null, decimal? Histogram = null);
