using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public interface ICandleStore
{
    void Upsert(string symbol, TimeFrame timeFrame, Candle candle);
    IReadOnlyList<Candle> Get(string symbol, TimeFrame timeFrame);
}

public interface IContractStore
{
    void Upsert(string symbol, ContractInfo contract);
    ContractInfo? Get(string symbol);
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
    StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles);
}

public interface IRiskManager
{
    TradePlan? CreatePlan(StrategySignal signal, decimal accountBalance, PaperPosition? openPosition,
        decimal realizedPnlToday, decimal aggregateOpenRisk, ContractInfo? contract);
}

public interface IOpenAiAnalyzer
{
    bool Enabled { get; }
    Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisContext context, CancellationToken cancellationToken);
}

public interface ITradeStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<AccountState?> LoadAccountStateAsync(CancellationToken cancellationToken);
    Task SaveAccountStateAsync(AccountState state, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaperPosition>> LoadOpenPositionsAsync(CancellationToken cancellationToken);
    Task SaveOpenPositionsAsync(IReadOnlyCollection<PaperPosition> positions, CancellationToken cancellationToken);
    Task RecordClosedTradeAsync(ClosedTrade trade, CancellationToken cancellationToken);
}
