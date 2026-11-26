using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.PaperTrading;
using TradingBot.Worker.Infrastructure.Persistence;

namespace TradingBot.Tests;

public sealed class PaperRecoveryTests
{
    [Fact]
    public async Task LossCircuitAndExitCooldownSurviveRestartAndBlockBothDirections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"paper-recovery-{Guid.NewGuid():N}.db");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var store = new SqliteTradeStore(Options.Create(new PersistenceOptions { DatabasePath = path }));
            await store.InitializeAsync(default);
            var risk = Options.Create(new RiskOptions { MaximumConsecutiveLosses = 1, CircuitBreakerMinutes = 60,
                ExitCooldownBars = 6, UsePartialTakeProfit = false, TargetProfitUsdt = 0m });
            var position = new PaperPosition("BTC-USDT", TradeDirection.Long, 1m, 100m, 90m, 120m, 10m, 0.05m,
                now.AddMinutes(-10), InitialStopLoss: 90m, InitialQuantity: 1m);
            await store.CommitPaperStateAsync(new AccountState(999.95m, DateOnly.FromDateTime(now.UtcDateTime), -0.05m), [position], null, default);
            var strategy = new CountingStrategy();
            var first = Engine(store, risk, strategy);
            await first.InitializeAsync(default);
            var update = new MarketUpdate("BTC-USDT", TimeFrame.FiveMinutes,
                new Candle(now.AddMinutes(-5), now, 100m, 101m, 89m, 90m, 100m, true));
            await first.ProcessAsync(update, default);
            var saved = (await store.LoadAccountStateAsync(default))!;
            Assert.Equal(1, saved.RiskControls!.ConsecutiveLosses);
            Assert.Equal(now.AddMinutes(60), saved.RiskControls.CircuitOpenUntil);
            var restarted = Engine(store, risk, strategy);
            await restarted.InitializeAsync(default);
            await restarted.ProcessAsync(update, default);
            await restarted.ProcessAsync(update with { Candle = update.Candle with { OpenTime = now, CloseTime = now.AddMinutes(5) } }, default);
            Assert.Empty(await store.LoadOpenPositionsAsync(default));
            Assert.Equal(0, strategy.Calls);
            await restarted.ProcessAsync(update with { Candle = new(now.AddMinutes(65), now.AddMinutes(70), 100m, 101m, 99m, 100m, 100m, true) }, default);
            Assert.Equal(1, strategy.Calls);
            Assert.Equal(TradeDirection.Short, Assert.Single(await store.LoadOpenPositionsAsync(default)).Direction);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseCommitRetriesWithoutDuplicateSettlementOrFalseSuccess(bool persistentFailure)
    {
        var path = Path.Combine(Path.GetTempPath(), $"paper-close-retry-{Guid.NewGuid():N}.db");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var store = new SqliteTradeStore(Options.Create(new PersistenceOptions { DatabasePath = path }));
            await store.InitializeAsync(default);
            var risk = Options.Create(new RiskOptions { UsePartialTakeProfit = false, FeeRate = 0m });
            var position = new PaperPosition("BTC-USDT", TradeDirection.Long, 1m, 100m, 90m, 120m, 10m, 0m,
                now.AddMinutes(-5), InitialStopLoss: 90m, InitialQuantity: 1m);
            await store.CommitPaperStateAsync(new AccountState(100m, DateOnly.FromDateTime(now.UtcDateTime), 0m), [position], null, default);
            var interrupted = new FaultingCloseStore(store, persistentFailure);
            var logger = new PositionEventLogger();
            var engine = Engine(interrupted, risk, new CountingStrategy(), logger);
            await engine.InitializeAsync(default);
            var update = new MarketUpdate("BTC-USDT", TimeFrame.FiveMinutes,
                new Candle(position.OpenedAt, now, 100m, 121m, 99m, 101m, 100m, true));
            if (persistentFailure)
            {
                await Assert.ThrowsAsync<CloseCommitException>(() => engine.ProcessAsync(update, default));
                var newBar = update with { Candle = update.Candle with { OpenTime = now, CloseTime = now.AddMinutes(5) } };
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ProcessAsync(newBar, default));
                Assert.Equal(["TP_TRIGGERED", "POSITION_CLOSE_REQUESTED", "POSITION_CLOSE_FAILED"], logger.Events);
                Assert.Equal(100m, (await store.LoadAccountStateAsync(default))!.Balance);
                Assert.Single(await store.LoadOpenPositionsAsync(default));
                Assert.Null(await store.LoadTradePerformanceAsync(default));
            }
            else
            {
                await engine.ProcessAsync(update, default);
                await engine.ProcessAsync(update, default);
                Assert.Equal(["TP_TRIGGERED", "POSITION_CLOSE_REQUESTED", "POSITION_CLOSED"], logger.Events);
                Assert.Equal(120m, (await store.LoadAccountStateAsync(default))!.Balance);
                Assert.Empty(await store.LoadOpenPositionsAsync(default));
                Assert.Equal(new TradePerformance(1, 0, 20m), await store.LoadTradePerformanceAsync(default));
            }

            Assert.Equal(2, interrupted.CloseAttempts);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentClosesForDifferentSymbolsPreserveBothSettlementsAndCash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"paper-close-concurrent-{Guid.NewGuid():N}.db");
        try
        {
            var closeTime = DateTimeOffset.UtcNow;
            var store = new SqliteTradeStore(Options.Create(new PersistenceOptions { DatabasePath = path }));
            await store.InitializeAsync(default);
            var position = new PaperPosition("BTC-USDT", TradeDirection.Long, 1m, 100m, 90m, 120m, 10m, 0m,
                closeTime.AddMinutes(-5));
            await store.CommitPaperStateAsync(new AccountState(100m, DateOnly.FromDateTime(closeTime.UtcDateTime), 0m),
                [position, position with { Symbol = "ETH-USDT" }], null, default);
            var risk = Options.Create(new RiskOptions { UsePartialTakeProfit = false, FeeRate = 0m });
            var engine = Engine(store, risk, new CountingStrategy());
            await engine.InitializeAsync(default);
            var update = new MarketUpdate(position.Symbol, TimeFrame.FiveMinutes,
                new Candle(position.OpenedAt, closeTime, 100m, 120m, 99m, 100m, 10m, true));

            await Task.WhenAll(Task.Run(() => engine.ProcessAsync(update, default)),
                Task.Run(() => engine.ProcessAsync(update with { Symbol = "ETH-USDT" }, default)));

            Assert.Empty(await store.LoadOpenPositionsAsync(default));
            Assert.Equal(140m, (await store.LoadAccountStateAsync(default))!.Balance);
            Assert.Equal(new TradePerformance(2, 0, 40m), await store.LoadTradePerformanceAsync(default));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static PaperTradingEngine Engine(ITradeStore store, IOptions<RiskOptions> risk, ITradingStrategy strategy,
        ILogger<PaperTradingEngine>? logger = null)
    {
        var contracts = new InMemoryContractStore();
        contracts.Upsert("BTC-USDT", new("BTC-USDT", 4, 2, 0.0001m, 1m, true));
        return new(new InMemoryCandleStore(), strategy, new RiskManager(risk), new NoAi(), store, contracts,
            risk, Options.Create(new PaperTradingOptions { SlippageBasisPoints = 0m, MaximumEntryAgeSeconds = 0 }),
            Options.Create(new OpenAIOptions()), Options.Create(new StrategyOptions()), logger ?? NullLogger<PaperTradingEngine>.Instance);
    }

    private sealed class CountingStrategy : ITradingStrategy
    {
        public int Calls;
        public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entry, IReadOnlyList<Candle> medium, IReadOnlyList<Candle> higher)
        {
            Calls++;
            return new(symbol, TradeDirection.Short, entry[^1].CloseTime, entry[^1].Close, 2m, "test after expiry");
        }
    }

    private sealed class NoAi : IOpenAiAnalyzer
    {
        public bool Enabled => false;
        public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisContext context, CancellationToken token) => throw new InvalidOperationException();
    }

    private sealed class FaultingCloseStore(SqliteTradeStore store, bool persistentFailure) : ITradeStore
    {
        public int CloseAttempts { get; private set; }

        public async Task CommitPaperStateAsync(AccountState account, IReadOnlyCollection<PaperPosition> positions,
            ClosedTrade? closedTrade, CancellationToken token)
        {
            if (closedTrade is not null)
            {
                CloseAttempts++;
                if (persistentFailure) throw new CloseCommitException();
            }
            await store.CommitPaperStateAsync(account, positions, closedTrade, token);
            if (closedTrade is not null && CloseAttempts == 1) throw new CloseCommitException();
        }

        public Task InitializeAsync(CancellationToken token) => store.InitializeAsync(token);
        public Task<AccountState?> LoadAccountStateAsync(CancellationToken token) => store.LoadAccountStateAsync(token);
        public Task<TradePerformance?> LoadTradePerformanceAsync(CancellationToken token) => store.LoadTradePerformanceAsync(token);
        public Task SaveAccountStateAsync(AccountState state, CancellationToken token) => store.SaveAccountStateAsync(state, token);
        public Task<IReadOnlyList<PaperPosition>> LoadOpenPositionsAsync(CancellationToken token) => store.LoadOpenPositionsAsync(token);
        public Task SaveOpenPositionsAsync(IReadOnlyCollection<PaperPosition> positions, CancellationToken token) => store.SaveOpenPositionsAsync(positions, token);
        public Task RecordClosedTradeAsync(ClosedTrade trade, CancellationToken token) => store.RecordClosedTradeAsync(trade, token);
        public IAsyncEnumerable<StoredPaperTrade> ReadClosedTradesAsync(CancellationToken token) => store.ReadClosedTradesAsync(token);
    }

    private sealed class CloseCommitException : DbException
    {
        public override bool IsTransient => true;
    }

    private sealed class PositionEventLogger : ILogger<PaperTradingEngine>
    {
        public List<string> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> fields &&
                fields.FirstOrDefault(field => field.Key == "PositionEvent").Value is string positionEvent &&
                positionEvent != "POSITION_MONITORING")
                Events.Add(positionEvent);
        }
    }
}
