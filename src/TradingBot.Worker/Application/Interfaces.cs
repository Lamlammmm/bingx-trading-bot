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
    Task<decimal?> GetSpreadBasisPointsAsync(string symbol, CancellationToken cancellationToken) => Task.FromResult<decimal?>(null);
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
    SignalEvaluation? LastEvaluation => null;
    StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles);
}

public interface ILiveExecutor
{
    Task<LiveAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<LiveIncomeSnapshot> GetIncomeAsync(string symbol, DateTimeOffset since, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<LiveIncomeSnapshot> GetIncomeAsync(string symbol, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<decimal> GetMarkPriceAsync(string symbol, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<LivePositionSnapshot> GetPositionAsync(string symbol, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<LiveManagedPosition> GetManagedPositionAsync(string symbol, TradeDirection direction, decimal quantity,
        decimal stopLoss, decimal takeProfit, string? stopOrderId, string? takeOrderId, CancellationToken cancellationToken,
        bool allowOversizedProtection = false) => throw new NotSupportedException();
    Task<LiveOrderFill> GetOrderFillAsync(string symbol, string clientOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<LiveOrderReceipt> CloseMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity,
        string clientOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<string> ReplaceProtectionAsync(string symbol, TradeDirection direction, string orderId, string orderType,
        decimal quantity, decimal triggerPrice, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task CancelKnownProtectionAsync(string symbol, IReadOnlyCollection<string> orderIds, CancellationToken cancellationToken) => throw new NotSupportedException();
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
    string? LastRejectionReason => null;
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
    Task CommitPaperStateWithJournalAsync(AccountState account, IReadOnlyCollection<PaperPosition> positions,
        ClosedTrade? closedTrade, IReadOnlyCollection<TradingJournalEvent> events, CancellationToken token) =>
        throw new NotSupportedException("Atomic unified journal persistence is required.");
    async Task CommitPaperStateAsync(AccountState account, IReadOnlyCollection<PaperPosition> positions,
        ClosedTrade? closedTrade, CancellationToken cancellationToken)
    {
        // Test/third-party stores can keep the small interface; production stores override atomically.
        await SaveAccountStateAsync(account, cancellationToken);
        await SaveOpenPositionsAsync(positions, cancellationToken);
        if (closedTrade is not null) await RecordClosedTradeAsync(closedTrade, cancellationToken);
    }
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

public interface ITradingResearchStore
{
    Task InitializeAsync(CancellationToken token);
    Task AppendAsync(TradingJournalEvent entry, CancellationToken token);
    IAsyncEnumerable<TradingJournalEvent> ReadAsync(TradingMode mode, DateTimeOffset since, CancellationToken token, string? kind = null);
    Task<StrategyRegistration?> GetActiveAsync(TradingMode mode, CancellationToken token);
    Task<StrategyRegistration?> GetVersionAsync(TradingMode mode, string version, CancellationToken token);
    Task RegisterAsync(StrategyRegistration registration, bool bootstrap, CancellationToken token);
    Task PromoteAsync(TradingMode mode, string version, string expectedActiveVersion, string approvedBy,
        string action, CancellationToken token);
    Task<bool> IsHaltedAsync(TradingMode mode, CancellationToken token);
    Task SetHaltAsync(TradingMode mode, bool halted, string reason, CancellationToken token);
}
