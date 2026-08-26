using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public interface ICandleStore
{
    void Upsert(TimeFrame timeFrame, Candle candle);
    IReadOnlyList<Candle> Get(TimeFrame timeFrame);
}

public interface IBingXMarketClient
{
    Task<IReadOnlyList<Candle>> GetCandlesAsync(TimeFrame timeFrame, int limit, CancellationToken cancellationToken);
    Task<ContractInfo?> GetContractAsync(CancellationToken cancellationToken);
}

public interface IBingXMarketStream
{
    Task StreamAsync(Func<MarketUpdate, Task> onUpdate, CancellationToken cancellationToken);
}

public interface ITradingStrategy
{
    StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> fifteenMinuteCandles,
        IReadOnlyList<Candle> oneHourCandles, IReadOnlyList<Candle> fourHourCandles);
}

public interface IRiskManager
{
    TradePlan? CreatePlan(StrategySignal signal, decimal accountBalance, PaperPosition? openPosition, decimal realizedPnlToday);
}

public interface IOpenAiAnalyzer
{
    bool Enabled { get; }
    Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisContext context, CancellationToken cancellationToken);
}
