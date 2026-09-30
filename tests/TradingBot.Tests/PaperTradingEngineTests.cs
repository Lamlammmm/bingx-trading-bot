using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.PaperTrading;

namespace TradingBot.Tests;

public sealed class PaperTradingEngineTests
{
    [Fact]
    public async Task PartialProfitAndBreakevenRemainder_AreRecordedAsOneWinningTrade()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var store = new CapturingTradeStore(new PaperPosition("BTC-USDT", TradeDirection.Long,
            5m, 100m, 100m, 106m, 0m, 0.5m, openedAt,
            InitialStopLoss: 97m, PartialTaken: true, InitialQuantity: 10m, RealizedPartialPnl: 2m));
        var engine = new PaperTradingEngine(new InMemoryCandleStore(), new NoSignalStrategy(),
            new RiskManager(Options.Create(new RiskOptions())), new DisabledAnalyzer(), store,
            new InMemoryContractStore(), Options.Create(new RiskOptions { StartingBalance = 100m, FeeRate = 0m }),
            Options.Create(new PaperTradingOptions { SlippageBasisPoints = 0m, MaxHoldingMinutes = 0 }),
            Options.Create(new OpenAIOptions()), Options.Create(new StrategyOptions()),
            NullLogger<PaperTradingEngine>.Instance);
        await engine.InitializeAsync(CancellationToken.None);

        var candle = new Candle(openedAt.AddMinutes(15), openedAt.AddMinutes(30),
            101m, 102m, 100m, 101m, 10m, true);
        await engine.ProcessAsync(new MarketUpdate("BTC-USDT", TimeFrame.FiveMinutes, candle), CancellationToken.None);

        Assert.NotNull(store.ClosedTrade);
        Assert.Equal(1.5m, store.ClosedTrade.NetPnl);
        Assert.Equal(10m, store.ClosedTrade.Quantity);
        Assert.Equal(1, store.AccountState!.TotalWins);
        Assert.Equal(0, store.AccountState.TotalLosses);
    }

    private sealed class NoSignalStrategy : ITradingStrategy
    {
        public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
            IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles) => null;
    }

    private sealed class DisabledAnalyzer : IOpenAiAnalyzer
    {
        public bool Enabled => false;
        public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiAnalysisResult(null, "disabled"));
    }

    private sealed class CapturingTradeStore(PaperPosition position) : ITradeStore
    {
        public AccountState? AccountState { get; private set; }
        public ClosedTrade? ClosedTrade { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<AccountState?> LoadAccountStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<AccountState?>(new AccountState(100m, DateOnly.FromDateTime(DateTime.UtcNow), 0m));
        public Task<TradePerformance?> LoadTradePerformanceAsync(CancellationToken cancellationToken) =>
            Task.FromResult<TradePerformance?>(null);
        public async IAsyncEnumerable<StoredPaperTrade> ReadClosedTradesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        { await Task.CompletedTask; yield break; }
        public Task SaveAccountStateAsync(AccountState state, CancellationToken cancellationToken)
        {
            AccountState = state;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PaperPosition>> LoadOpenPositionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaperPosition>>(new[] { position });
        public Task SaveOpenPositionsAsync(IReadOnlyCollection<PaperPosition> positions, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task RecordClosedTradeAsync(ClosedTrade trade, CancellationToken cancellationToken)
        {
            ClosedTrade = trade;
            return Task.CompletedTask;
        }
    }
}
