using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public interface ICandleStore
{
    void Upsert(string symbol, TimeFrame timeFrame, Candle candle);
    IReadOnlyList<Candle> Get(string symbol, TimeFrame timeFrame);
}

public interface IBingXMarketClient
{
    Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int limit, CancellationToken cancellationToken);
    Task<ContractInfo?> GetContractAsync(string symbol, CancellationToken cancellationToken);
}

public interface IBingXMarketStream
{
    Task StreamAsync(IReadOnlyList<string> symbols, Func<MarketUpdate, Task> onUpdate, CancellationToken cancellationToken);
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
