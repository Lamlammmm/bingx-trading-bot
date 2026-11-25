using System.Text.Json.Serialization;

namespace TradingBot.Worker.Configuration;

public sealed class BotOptions
{
    public const string SectionName = "Bot";
    public bool Enabled { get; set; } = true;
}

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
    public int SymbolRetrySeconds { get; set; } = 60;
    public bool LogRawPriceUpdates { get; set; }
}

public sealed class StrategyOptions
{
    public const string SectionName = "Strategy";
    public int FastEmaPeriod { get; set; } = 20;
    public int SlowEmaPeriod { get; set; } = 50;
    public int BreakoutLookback { get; set; } = 20;
    public int VolumeLookback { get; set; } = 20;
    public decimal VolumeMultiplier { get; set; } = 1.2m;
    public decimal MaximumVolumeMultiplier { get; set; } = 3m;
    public int AtrPeriod { get; set; } = 14;
    public int RsiPeriod { get; set; } = 14;
    public decimal RsiOverbought { get; set; } = 65m;
    public decimal RsiOversold { get; set; } = 35m;
    public decimal MinimumBodyRatio { get; set; } = 0.5m;
    public int SignalCooldownBars { get; set; } = 12;
    public int RetestConfirmationBars { get; set; } = 3;
    // Distance from the broken level to the retest close, using ATR before breakout. Zero disables this filter.
    public decimal MaxRetestEntryDistanceAtr { get; set; } = 1m;
    public List<string> ExcludedSymbols { get; set; } = new();
    // Select the same multi-timeframe signal policy for paper and live.
    public bool UseMultiTimeframeAnalysis { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseAdaptiveCurrentEntries { get; set; }
    public int PrimaryEmaPeriod { get; set; } = 200;
    public int AdxPeriod { get; set; } = 14;
    public decimal MinimumAdx { get; set; } = 20m;
    public decimal MinimumAtrPercent { get; set; } = 0.1m;
    public decimal MaximumAtrPercent { get; set; } = 3m;
    public int StructureLookback { get; set; } = 6;
    public decimal MaximumSetupDistanceAtr { get; set; } = 2m;
}

public sealed class RiskOptions
{
    public const string SectionName = "Risk";
    public decimal StartingBalance { get; set; } = 10_000m;
    public decimal RiskPerTradePercent { get; set; } = 0.5m;
    public decimal MaxDailyLossPercent { get; set; } = 2m;
    public decimal MaxLeverage { get; set; } = 20m;
    public decimal StopAtrMultiplier { get; set; } = 1.5m;
    public decimal TakeProfitRiskMultiple { get; set; } = 2m;
    public decimal FeeRate { get; set; } = 0.0005m;
    public decimal MinimumQuantity { get; set; } = 0.0001m;
    public decimal MaximumStopAtrMultiplier { get; set; } = 3m;
    public decimal MinimumTakeProfitRiskMultiple { get; set; } = 1.5m;
    // Full-position payoff at final TP, after estimated entry/exit fees; not a win-rate estimate.
    public decimal MinimumNetRewardRiskRatio { get; set; } = 1.5m;
    public decimal MaxAggregateOpenRiskPercent { get; set; } = 2m;
    public decimal TargetProfitUsdt { get; set; } = 1m;
    public bool UseFixedPercentSlTp { get; set; }
    public decimal TakeProfitMarginPercent { get; set; } = 50m;
    public decimal StopLossMarginPercent { get; set; } = 25m;
    public bool UsePartialTakeProfit { get; set; } = true;
    public decimal PartialTakeProfitRMultiple { get; set; } = 1m;
    public decimal PartialTakeProfitPercent { get; set; } = 50m;
    public decimal StructureBufferAtr { get; set; }
    public decimal ExecutionCostBasisPoints { get; set; }
    public int MaxOpenPositions { get; set; }
    public int ExitCooldownBars { get; set; }
    public int MaximumConsecutiveLosses { get; set; }
    public int CircuitBreakerMinutes { get; set; } = 60;
    public bool UseDynamicPositionManagement { get; set; }
    public decimal SecondTakeProfitRMultiple { get; set; } = 2m;
    public decimal SecondTakeProfitPercent { get; set; } = 25m;
    public decimal BreakEvenTriggerR { get; set; } = 1.5m;
    public decimal TrailingStartR { get; set; } = 2m;
    public decimal TrailingAtrMultiplier { get; set; } = 2m;
    public decimal MinimumTrailingStepAtr { get; set; } = 0.25m;
    public int StopAdjustmentCooldownBars { get; set; } = 3;
    public decimal MaxDrawdownPercent { get; set; } = 5m;
    public decimal MaxAggregateExposurePercent { get; set; } = 300m;
}

public sealed class ExperimentalStrategyOptions
{
    public const string SectionName = "ExperimentalStrategy";
    public bool Enabled { get; set; }
    public decimal MinimumAdx { get; set; } = 20m;
    public decimal MinimumAtrPercent { get; set; } = 0.1m;
    public decimal MaximumAtrPercent { get; set; } = 3m;
    public int PullbackBars { get; set; } = 3;
    public decimal MaximumEntryDistanceAtr { get; set; } = 1.5m;
}

public sealed class AdaptiveOptions
{
    public const string SectionName = "Adaptive";
    public bool Enabled { get; set; } = true;
    public int MinimumTrades { get; set; } = 30;
    public int LookbackDays { get; set; } = 30;
    public int AnalysisIntervalMinutes { get; set; } = 5;
    public decimal MaximumSpreadBasisPoints { get; set; } = 20m;
    public decimal DegradedRiskMultiplier { get; set; } = 0.5m;
    public decimal MinimumProfitFactor { get; set; } = 1m;
    public string BootstrapVersion { get; set; } = "mtf-v1";
}

public sealed class LiveOptions
{
    public const string SectionName = "Live";
    // Off by default. This places REAL orders with REAL money when true - never flip without
    // understanding the risks documented alongside this feature.
    public bool Enabled { get; set; }
    public bool AllowAdaptiveCurrentEntries { get; set; }
    // Unset keeps the shared Risk setting for existing configurations.
    public decimal? RiskPerTradePercent { get; set; }
    public string StatePath { get; set; } = "data/live-state.json";
    public decimal Leverage { get; set; } = 5m;
    public bool AutoLeverage { get; set; }
    public decimal MaxLeverage { get; set; } = 20m;
    // Zero permits one bot position per configured symbol, subject to available margin.
    public int MaxOpenPositions { get; set; }
    public decimal MaxMarginUsagePercent { get; set; } = 20m;
    public decimal StopAtrMultiplier { get; set; } = 1.5m;
    public decimal TakeProfitRiskMultiple { get; set; } = 2m;
    public int MaximumConsecutiveApiFailures { get; set; } = 3;
    public int ApiCircuitBreakerMinutes { get; set; } = 5;
    public decimal MaximumEntryDeviationBasisPoints { get; set; } = 10m;
    public MacdOptions Macd { get; set; } = new();
}

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";
    public string Provider { get; set; } = "Sqlite";
    public string DatabasePath { get; set; } = "data/tradingbot.db";
    public string? ConnectionString { get; set; }
}

public sealed class PaperTradingOptions
{
    public const string SectionName = "PaperTrading";
    public decimal SlippageBasisPoints { get; set; } = 2m;
    public decimal FundingRatePerEightHours { get; set; }
    public decimal SpreadBasisPoints { get; set; }
    // Zero is reserved for explicit offline replay.
    public int MaximumEntryAgeSeconds { get; set; } = 120;
    // MACD is a per-mode runtime gate outside the versioned execution-cost snapshot.
    [JsonIgnore]
    public MacdOptions Macd { get; set; } = new();
}

public sealed class MacdOptions
{
    public bool Enabled { get; set; }
    public int Fast { get; set; } = 12;
    public int Slow { get; set; } = 26;
    public int Signal { get; set; } = 9;

    [JsonIgnore]
    public bool IsValid => Fast > 0 && Slow > Fast && Signal > 0;
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

public sealed class TradingFrequencyOptions
{
    public const string SectionName = "TradingFrequency";
    public bool Enabled { get; set; } = true;
    public int TargetEntriesPerDay { get; set; } = 1;
    public string TimeZone { get; set; } = "SE Asia Standard Time";
    public bool CountOnlyFilledEntries { get; set; } = true;
    public bool ForceEntryToMeetTarget { get; set; }
}
