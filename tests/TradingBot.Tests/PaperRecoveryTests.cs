using Microsoft.Data.Sqlite;
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

    private static PaperTradingEngine Engine(ITradeStore store, IOptions<RiskOptions> risk, ITradingStrategy strategy)
    {
        var contracts = new InMemoryContractStore();
        contracts.Upsert("BTC-USDT", new("BTC-USDT", 4, 2, 0.0001m, 1m, true));
        return new(new InMemoryCandleStore(), strategy, new RiskManager(risk), new NoAi(), store, contracts,
            risk, Options.Create(new PaperTradingOptions { SlippageBasisPoints = 0m, MaximumEntryAgeSeconds = 0 }),
            Options.Create(new OpenAIOptions()), Options.Create(new StrategyOptions()), NullLogger<PaperTradingEngine>.Instance);
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
}
