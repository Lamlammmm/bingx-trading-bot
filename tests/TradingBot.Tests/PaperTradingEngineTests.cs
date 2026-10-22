using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.PaperTrading;

namespace TradingBot.Tests;

public sealed class PaperTradingEngineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EntriesUse15mWith30mAnd1hTrendsWhile5mOnlyMonitorsProtection(bool protectionArrivesFirst)
    {
        var now = DateTimeOffset.UtcNow;
        var strategy = new CapturingStrategy();
        var store = new CapturingTradeStore();
        var risk = Options.Create(new RiskOptions { StartingBalance = 100m, FeeRate = 0m,
            TargetProfitUsdt = 0m, UsePartialTakeProfit = false });
        var contracts = new InMemoryContractStore();
        contracts.Upsert("BTC-USDT", new ContractInfo("BTC-USDT", 4, 2, 0.0001m, 2m, true));
        var engine = new PaperTradingEngine(new InMemoryCandleStore(), strategy, new RiskManager(risk),
            new DisabledAnalyzer(), store, contracts, risk, Options.Create(new PaperTradingOptions { SlippageBasisPoints = 0m }),
            Options.Create(new OpenAIOptions()), Options.Create(new StrategyOptions()), NullLogger<PaperTradingEngine>.Instance);
        await engine.InitializeAsync(default);
        var oldProtection = new MarketUpdate("BTC-USDT", TimeFrame.FiveMinutes,
            new Candle(now.AddMinutes(-5), now, 100m, 120m, 80m, 100m, 10m, true));
        if (protectionArrivesFirst) await engine.ProcessAsync(oldProtection, default);
        await engine.ProcessAsync(new("BTC-USDT", TimeFrame.ThirtyMinutes,
            new Candle(now.AddMinutes(-30), now, 104m, 106m, 103m, 105m, 10m, true)), default);
        await engine.ProcessAsync(new("BTC-USDT", TimeFrame.OneHour,
            new Candle(now.AddHours(-1), now, 105m, 107m, 104m, 106m, 10m, true)), default);
        Assert.Equal(0, strategy.Calls);
        Assert.Empty(store.Positions);

        var entry = new MarketUpdate("BTC-USDT", TimeFrame.FifteenMinutes,
            new Candle(now.AddMinutes(-15), now, 99m, 101m, 98m, 100m, 10m, true));
        await engine.ProcessAsync(entry with { Candle = entry.Candle with { IsClosed = false } }, default);
        Assert.Equal(0, strategy.Calls);
        await engine.ProcessAsync(entry, default);
        await engine.ProcessAsync(entry, default);
        if (!protectionArrivesFirst) await engine.ProcessAsync(oldProtection, default);
        Assert.Equal(1, strategy.Calls);
        Assert.Equal(100m, Assert.Single(strategy.Entry).Close);
        Assert.Equal(105m, Assert.Single(strategy.Medium).Close);
        Assert.Equal(106m, Assert.Single(strategy.Higher).Close);
        Assert.Single(store.Positions);
        Assert.Null(store.ClosedTrade);

        await engine.ProcessAsync(new("BTC-USDT", TimeFrame.FiveMinutes,
            new Candle(now.AddMinutes(10), now.AddMinutes(15), 100m, 101m, 90m, 95m, 10m, true)), default);
        Assert.Equal("SL", store.ClosedTrade!.ExitReason);
        await engine.ProcessAsync(entry with { Candle = entry.Candle with {
            OpenTime = now, CloseTime = now.AddMinutes(15) } }, default);
        Assert.Empty(store.Positions);
        Assert.Equal(1, strategy.Calls); // No new entry on the candle that closed the previous position.
    }

    [Theory]
    [InlineData(TradeDirection.Long, "SL")]
    [InlineData(TradeDirection.Long, "TP")]
    [InlineData(TradeDirection.Short, "SL")]
    [InlineData(TradeDirection.Short, "TP")]
    public async Task OldPositionOnlyClosesWhenProtectionIsHit(TradeDirection direction, string exitReason)
    {
        var now = DateTimeOffset.UtcNow;
        var stop = direction == TradeDirection.Long ? 90m : 110m;
        var take = direction == TradeDirection.Long ? 120m : 80m;
        var store = new CapturingTradeStore(new PaperPosition("BTC-USDT", direction,
            1m, 100m, stop, take, 10m, 0m, now.AddDays(-2)));
        var riskOptions = Options.Create(new RiskOptions { StartingBalance = 100m, FeeRate = 0m, UsePartialTakeProfit = false });
        var engine = new PaperTradingEngine(new InMemoryCandleStore(), new NoSignalStrategy(),
            new RiskManager(riskOptions), new DisabledAnalyzer(), store, new InMemoryContractStore(), riskOptions,
            Options.Create(new PaperTradingOptions { SlippageBasisPoints = 0m }),
            Options.Create(new OpenAIOptions()), Options.Create(new StrategyOptions()), NullLogger<PaperTradingEngine>.Instance);
        await engine.InitializeAsync(default);

        await engine.ProcessAsync(new("BTC-USDT", TimeFrame.FiveMinutes,
            new Candle(now.AddMinutes(-10), now.AddMinutes(-5), 100m, 101m, 99m, 100m, 10m, true)), default);

        Assert.Null(store.ClosedTrade);

        var exit = exitReason == "SL" ? stop : take;
        await engine.ProcessAsync(new("BTC-USDT", TimeFrame.FiveMinutes,
            new Candle(now.AddMinutes(-5), now, 100m, Math.Max(100m, exit), Math.Min(100m, exit), exit, 10m, true)), default);

        Assert.NotNull(store.ClosedTrade);
        Assert.Equal(exitReason, store.ClosedTrade.ExitReason);
    }

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
            Options.Create(new PaperTradingOptions { SlippageBasisPoints = 0m }),
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

    private sealed class CapturingStrategy : ITradingStrategy
    {
        public int Calls;
        public IReadOnlyList<Candle> Entry = Array.Empty<Candle>();
        public IReadOnlyList<Candle> Medium = Array.Empty<Candle>();
        public IReadOnlyList<Candle> Higher = Array.Empty<Candle>();
        public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
            IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles)
        {
            Calls++;
            Entry = entryCandles;
            Medium = mediumTrendCandles;
            Higher = higherTrendCandles;
            return new StrategySignal(symbol, TradeDirection.Long, entryCandles[^1].CloseTime, 100m, 2m, "test");
        }
    }

    private sealed class DisabledAnalyzer : IOpenAiAnalyzer
    {
        public bool Enabled => false;
        public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiAnalysisResult(null, "disabled"));
    }

    private sealed class CapturingTradeStore(PaperPosition? position = null) : ITradeStore
    {
        public AccountState? AccountState { get; private set; }
        public ClosedTrade? ClosedTrade { get; private set; }
        public IReadOnlyCollection<PaperPosition> Positions { get; private set; } = Array.Empty<PaperPosition>();

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
            Task.FromResult<IReadOnlyList<PaperPosition>>(position is null ? Array.Empty<PaperPosition>() : new[] { position });
        public Task SaveOpenPositionsAsync(IReadOnlyCollection<PaperPosition> positions, CancellationToken cancellationToken)
        {
            Positions = positions;
            return Task.CompletedTask;
        }
        public Task RecordClosedTradeAsync(ClosedTrade trade, CancellationToken cancellationToken)
        {
            ClosedTrade = trade;
            return Task.CompletedTask;
        }
    }
}
