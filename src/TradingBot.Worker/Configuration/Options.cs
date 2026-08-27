namespace TradingBot.Worker.Configuration;

public sealed class BingXOptions
{
    public const string SectionName = "BingX";
    public string RestBaseUrl { get; set; } = "https://open-api.bingx.com";
    public string WebSocketUrl { get; set; } = "wss://open-api-swap.bingx.com/swap-market";
    public List<string> Symbols { get; set; } = new() { "BTC-USDT" };
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public int HistoryLimit { get; set; } = 250;
    public int RequestTimeoutSeconds { get; set; } = 20;
    public int ReconnectDelaySeconds { get; set; } = 5;
}

public sealed class StrategyOptions
{
    public const string SectionName = "Strategy";
    public int FastEmaPeriod { get; set; } = 20;
    public int SlowEmaPeriod { get; set; } = 50;
    public int BreakoutLookback { get; set; } = 20;
    public int VolumeLookback { get; set; } = 20;
    public decimal VolumeMultiplier { get; set; } = 1.2m;
    public int AtrPeriod { get; set; } = 14;
    public int RsiPeriod { get; set; } = 14;
    public decimal RsiOverbought { get; set; } = 75m;
    public decimal RsiOversold { get; set; } = 25m;
    public decimal MinimumBodyRatio { get; set; } = 0.5m;
    public int SignalCooldownBars { get; set; } = 4;
}

public sealed class RiskOptions
{
    public const string SectionName = "Risk";
    public decimal StartingBalance { get; set; } = 10_000m;
    public decimal RiskPerTradePercent { get; set; } = 0.5m;
    public decimal MaxDailyLossPercent { get; set; } = 2m;
    public decimal MaxLeverage { get; set; } = 3m;
    public decimal StopAtrMultiplier { get; set; } = 1.5m;
    public decimal TakeProfitRiskMultiple { get; set; } = 2m;
    public decimal FeeRate { get; set; } = 0.0005m;
    public decimal MinimumQuantity { get; set; } = 0.0001m;
    public decimal MaximumStopAtrMultiplier { get; set; } = 3m;
    public decimal MinimumTakeProfitRiskMultiple { get; set; } = 1.5m;
}

public sealed class OpenAIOptions
{
    public const string SectionName = "OpenAI";
    public bool Enabled { get; set; }
    public string Model { get; set; } = "gpt-5.2";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public int RequestTimeoutSeconds { get; set; } = 45;
    public int CandleCountPerTimeFrame { get; set; } = 60;
    public decimal MaximumEntryDeviationPercent { get; set; } = 0.25m;
    public decimal MinimumConfidence { get; set; } = 0.6m;
    public string? ApiKey { get; set; }
}
