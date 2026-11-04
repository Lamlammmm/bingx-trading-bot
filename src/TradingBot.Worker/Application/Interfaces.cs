using System.Threading.Channels;
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
    Task StreamAsync(IReadOnlyList<string> symbols, ChannelReader<string> additionalSymbols,
        Func<MarketUpdate, Task> onUpdate, CancellationToken cancellationToken);
}

public interface ITradingStrategy
{
    StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles);
}

public interface ILiveExecutor
{
    Task<string> GetPositionHistoryAsync(string symbol, DateTimeOffset since, CancellationToken cancellationToken);
    Task ValidatePositionModeAsync(CancellationToken cancellationToken);
    Task<int> GetOpenOrderCountAsync(CancellationToken cancellationToken, string? symbol = null);
    Task ValidateProtectionAsync(string symbol, TradeDirection direction, decimal quantity, CancellationToken cancellationToken);
    Task<decimal> GetAvailableBalanceUsdtAsync(CancellationToken cancellationToken);
    Task<int> GetOpenPositionCountAsync(CancellationToken cancellationToken);
    Task<bool> HasOpenPositionAsync(string symbol, CancellationToken cancellationToken);
    Task SetLeverageAsync(string symbol, TradeDirection direction, decimal leverage, CancellationToken cancellationToken);
    Task<decimal> GetMaxLeverageAsync(string symbol, TradeDirection direction, CancellationToken cancellationToken);
    Task<LiveOrderReceipt> OpenMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity,
        decimal stopLoss, decimal takeProfit, string clientOrderId, CancellationToken cancellationToken);
    Task<LiveOrderReceipt> CloseMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity, CancellationToken cancellationToken);
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
    Task<TradePerformance?> LoadTradePerformanceAsync(CancellationToken cancellationToken);
    Task SaveAccountStateAsync(AccountState state, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaperPosition>> LoadOpenPositionsAsync(CancellationToken cancellationToken);
    Task SaveOpenPositionsAsync(IReadOnlyCollection<PaperPosition> positions, CancellationToken cancellationToken);
    Task RecordClosedTradeAsync(ClosedTrade trade, CancellationToken cancellationToken);
    IAsyncEnumerable<StoredPaperTrade> ReadClosedTradesAsync(CancellationToken cancellationToken);
}

public interface ILiveTradeJournal
{
    Task InitializeJournalAsync(CancellationToken cancellationToken);
    Task RecordLiveEventAsync(LiveTradeEvent entry, CancellationToken cancellationToken);
    IAsyncEnumerable<LiveTradeEvent> ReadLiveEventsAsync(CancellationToken cancellationToken);
}
