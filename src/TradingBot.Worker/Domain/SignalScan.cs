namespace TradingBot.Worker.Domain;

public sealed class SignalScan(MarketUpdate update, string mode, string strategy, string version)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CandleClose { get; } = update.Candle.CloseTime;
    public string Symbol { get; } = update.Symbol;
    public string Timeframe { get; } = "5m";
    public string Mode { get; } = mode;
    public string Strategy { get; set; } = strategy;
    public string Version { get; set; } = version;
    public string? ConfigurationHash { get; set; }
    public decimal CurrentPrice { get; } = update.Candle.Close;
    public decimal Volume { get; } = update.Candle.Volume;
    public string Decision { get; private set; } = "REJECTED";
    public string Stage { get; set; } = "ENTRY_GUARDS";
    public string? PrimaryReason { get; private set; }
    public IReadOnlyList<string> SecondaryReasons { get; private set; } = [];
    public string ExecutionStatus { get; set; } = "NOT_ATTEMPTED";
    public string? OrderId { get; set; }
    public DateTimeOffset? FilledAt { get; set; }
    public bool? ProtectionConfirmed { get; set; }
    public string? ErrorDetails { get; private set; }
    public string? RejectionDetails { get; set; }
    public SignalEvaluation? Evaluation { get; private set; }
    public MarketRegime Regime => Evaluation?.Regime ?? MarketRegime.Unknown;
    public string Trend => Regime switch { MarketRegime.TrendingUp => "UP", MarketRegime.TrendingDown => "DOWN", _ => "UNKNOWN" };
    public string Direction { get; private set; } = "NONE";
    public decimal? Score { get; private set; }
    public decimal? Atr { get; private set; }
    public decimal? EntryPrice { get; private set; }
    public decimal? StopLoss { get; private set; }
    public decimal? TakeProfit { get; private set; }
    public decimal? NetRewardRisk { get; private set; }
    public MacdEntryDecision? Macd { get; set; }

    public void Observe(StrategySignal? signal, SignalEvaluation? evaluation)
    {
        Stage = "STRATEGY";
        Evaluation = evaluation;
        if (signal is null)
        {
            Reject(evaluation?.ReasonCode ?? "NO_VALID_SETUP", evaluation?.SecondaryReasons ?? []);
            return;
        }
        Direction = signal.Direction == TradeDirection.Long ? "LONG" : "SHORT";
        Score = signal.Source == "openai" ? signal.Confidence : null;
        EntryPrice = signal.EntryPrice;
        Atr = signal.Atr;
    }

    public void Reject(string reason, params string[] secondary)
    {
        PrimaryReason = reason;
        SecondaryReasons = secondary;
    }

    public void Accept(decimal entry, decimal stop, decimal take, decimal netRewardRisk)
    {
        Decision = "ACCEPTED";
        PrimaryReason = null;
        SecondaryReasons = [];
        EntryPrice = entry;
        StopLoss = stop;
        TakeProfit = take;
        NetRewardRisk = netRewardRisk;
    }

    public void Fail(Exception exception) => Fail(exception is BingXOrderRejected rejection
        ? $"{nameof(BingXOrderRejected)}:{rejection.Code}" : exception.GetType().Name,
        exception is BingXOrderRejected);

    public void Fail(string errorType, bool exchangeRejected = false)
    {
        // Exception messages/responses may contain credentials. Keep only safe types and exchange codes.
        ErrorDetails = errorType;
        if (Decision != "ACCEPTED")
        {
            Decision = "ERROR";
            PrimaryReason = null;
            SecondaryReasons = [];
        }
        if (ExecutionStatus == "ATTEMPTED")
            ExecutionStatus = exchangeRejected ? "REJECTED" : "UNKNOWN";
    }
}
