using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Live;

namespace TradingBot.Tests;

public sealed class LiveTradingEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "live-test-" + Guid.NewGuid());
    private readonly FakeExecutor _executor = new();
    private readonly InMemoryCandleStore _candles = new();
    private readonly InMemoryContractStore _contracts = new();
    private readonly CapturingJournal _journal = new();
    private bool _autoLeverage;

    private LiveTradingEngine Create()
    {
        Directory.CreateDirectory(_directory);
        return new LiveTradingEngine(_candles, Options.Create(new StrategyOptions { RsiOverbought = 101m }),
            Options.Create(new RiskOptions { RiskPerTradePercent = 10m }),
            Options.Create(new LiveOptions { Enabled = true, AutoLeverage = _autoLeverage, MaxLeverage = 10,
                StatePath = Path.Combine(_directory, "state.json") }),
            _executor, _contracts, NullLogger<LiveTradingEngine>.Instance, _journal);
    }

    [Theory]
    [InlineData("10", false, 1, false, "0.01", 20, 5, "0.15")]
    [InlineData("0.1", false, 0, false, "0.01", 20, 0, "0")]
    [InlineData("10", true, 1, false, "0.01", 20, 5, "0.15")]
    [InlineData("10", false, 1, true, "0.01", 20, 3, "0.15")]
    [InlineData("10", false, 1, true, "0.06", 20, 3, "0.15")]
    [InlineData("10", false, 0, true, "0.20", 20, 0, "0")]
    [InlineData("10", false, 0, true, "0.01", 2, 0, "0")]
    [InlineData("100", false, 1, true, "0.01", 20, 3, "1.57")]
    [InlineData("0.1", false, 0, true, "0.01", 20, 0, "0")]
    public async Task SmallBalanceEntryAndTimeoutAreHandled(string balance, bool timeout, int expectedOrders,
        bool adaptive, string minimumQuantity, int exchangeMaximum, int expectedLeverage, string expectedQuantity)
    {
        _executor.Balance = decimal.Parse(balance, System.Globalization.CultureInfo.InvariantCulture);
        _executor.TimeoutOnOpen = timeout;
        _autoLeverage = adaptive;
        _executor.MaximumLeverage = exchangeMaximum;
        _contracts.Upsert("SOL-USDT", new ContractInfo("SOL-USDT", 2, 2,
            decimal.Parse(minimumQuantity, System.Globalization.CultureInfo.InvariantCulture), 2m, true));
        var end = DateTimeOffset.UtcNow.AddMinutes(-1);
        var close = 100m;
        for (var i = 0; i < 80; i++)
        {
            var open = close;
            close += i % 3 == 2 ? -0.6m : 0.5m;
            var time = end.AddMinutes(-5 * (80 - i));
            var candle = i == 79
                ? new Candle(end.AddMinutes(-1), end, 129m, 131m, 129m, 130m, 200m, true)
                : new Candle(time, time.AddMinutes(5), open, Math.Max(open, close) + 0.5m,
                    Math.Min(open, close) - 0.5m, close, 100m, true);
            _candles.Upsert("SOL-USDT", TimeFrame.FiveMinutes, candle);
            foreach (var frame in new[] { TimeFrame.OneHour, TimeFrame.FourHours })
            {
                var trend = 100m + i * 2m;
                var interval = frame == TimeFrame.OneHour ? 1 : 4;
                var trendTime = end.AddHours(-interval * (80 - i));
                _candles.Upsert("SOL-USDT", frame,
                    new Candle(trendTime, trendTime.AddHours(interval), trend - 2m, trend + 0.5m, trend - 0.5m, trend, 100m, true));
            }
        }
        if (expectedOrders == 1)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "state.json"),
                "{\"Version\":1,\"Date\":\"2026-09-22\",\"StartingBalance\":10,\"DailyLossTripped\":true}");
        }
        using (var engine = Create())
        {
            await engine.InitializeAsync(default);
            await engine.ProcessAsync(new MarketUpdate("SOL-USDT", TimeFrame.FiveMinutes,
                _candles.Get("SOL-USDT", TimeFrame.FiveMinutes)[^1]), default);
            var retest = new Candle(end, DateTimeOffset.UtcNow, 112m, 132m, 110m, 131m, 100m, true);
            _candles.Upsert("SOL-USDT", TimeFrame.FiveMinutes, retest);
            var update = new MarketUpdate("SOL-USDT", TimeFrame.FiveMinutes, retest);
            if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => engine.ProcessAsync(update, default));
            else await engine.ProcessAsync(update, default);
            await engine.ProcessAsync(update, default);
            Assert.Equal(expectedOrders, _executor.OpenOrdersSent);
            Assert.Equal(expectedLeverage, _executor.LastLeverage);
            Assert.Equal(decimal.Parse(expectedQuantity, System.Globalization.CultureInfo.InvariantCulture), _executor.LastQuantity);
            if (expectedOrders == 1)
            {
                var request = Assert.Single(_journal.Events.Values.Where(e => e.Kind == "entry_requested"));
                using var context = JsonDocument.Parse(request.PayloadJson);
                Assert.Equal(expectedLeverage, context.RootElement.GetProperty("Leverage").GetDecimal());
                Assert.Equal(81, context.RootElement.GetProperty("EntryCandles").GetArrayLength());
                Assert.Equal(timeout ? 1 : 3, _journal.Events.Count);
            }
        }
        if (timeout)
        {
            using var restarted = Create();
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.InitializeAsync(default));
            Assert.Equal(1, _executor.OpenOrdersSent);
        }
    }

    [Fact]
    public async Task StartupAcceptsOldDailyLossStateWithoutKeepingTheGuard()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "state.json"),
            "{\"Version\":1,\"Date\":\"2026-09-22\",\"StartingBalance\":1.0145,\"DailyLossTripped\":true}");
        _executor.Balance = 0.95m;
        using (var engine = Create()) await engine.InitializeAsync(default);
        var state = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(Path.Combine(_directory, "state.json")))!;
        Assert.Equal(1, state.Version);
        Assert.DoesNotContain("DailyLossTripped", File.ReadAllText(Path.Combine(_directory, "state.json")));
        Assert.Equal(0, _executor.Writes);
    }

    private sealed class CapturingJournal : ILiveTradeJournal
    {
        public Dictionary<string, LiveTradeEvent> Events { get; } = new();
        public bool FailWrites;
        public Task InitializeJournalAsync(CancellationToken token) => Task.CompletedTask;
        public Task RecordLiveEventAsync(LiveTradeEvent entry, CancellationToken token)
        {
            if (FailWrites) throw new IOException("Database unavailable");
            Events.TryAdd(entry.EventId, entry); return Task.CompletedTask;
        }
        public async IAsyncEnumerable<LiveTradeEvent> ReadLiveEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            foreach (var entry in Events.Values) yield return entry;
        }
    }

    [Fact]
    public async Task ManualPositionPausesBotWithoutFailingStartup()
    {
        _executor.Positions = 1;
        using var engine = Create();
        await engine.InitializeAsync(default);
        var now = DateTimeOffset.UtcNow;
        await engine.ProcessAsync(new("SOL-USDT", TimeFrame.FiveMinutes,
            new Candle(now.AddMinutes(-5), now, 100m, 101m, 99m, 100m, 1m, true)), default);
        Assert.Equal(0, _executor.Writes);
    }

    [Fact]
    public async Task OutboxIsReplayedEvenWhenPendingOrderBlocksStartup()
    {
        using (var engine = Create())
        {
            var entry = new LiveTradeEvent("id:entry", "id", "SOL-USDT", "entry_requested", DateTimeOffset.UtcNow, "{}");
            File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(new LiveTradingEngine.LiveState
                { PendingOrder = "id", JournalOutbox = new() { entry } }));
            _journal.FailWrites = true;
            await Assert.ThrowsAsync<IOException>(() => engine.InitializeAsync(default));
        }
        _journal.FailWrites = false;
        using var restarted = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.InitializeAsync(default));
        Assert.Single(_journal.Events);
        Assert.Equal(0, _executor.Writes);
    }

    [Fact]
    public async Task OutstandingOrdersBlockStartup()
    {
        _executor.Orders = 1;
        using var engine = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.InitializeAsync(default));
        Assert.Equal(0, _executor.Writes);
    }

    [Fact]
    public async Task PendingOrderSurvivesRestartAndBlocksTrading()
    {
        using var engine = Create();
        File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(new LiveTradingEngine.LiveState
            { PendingOrder = "bot-unknown" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.InitializeAsync(default));
        Assert.Equal(0, _executor.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoredPositionRequiresExchangeProtection(bool protectedPosition)
    {
        using var engine = Create();
        File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(new LiveTradingEngine.LiveState
        {
            Position = new("SOL-USDT", TradeDirection.Long, 0.02m, 90m, 110m, DateTimeOffset.UtcNow)
        }));
        _executor.Positions = 1;
        _executor.Protected = protectedPosition;
        if (protectedPosition) await engine.InitializeAsync(default);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => engine.InitializeAsync(default));
        Assert.Equal(1, _executor.ProtectionChecks);
        Assert.Equal(0, _executor.Writes);
    }

    [Fact]
    public async Task SecondInstanceCannotShareState()
    {
        using var first = Create();
        await first.InitializeAsync(default);
        using var second = Create();
        await Assert.ThrowsAsync<IOException>(() => second.InitializeAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExchangeClosedPositionIsRemovedWithoutSendingCloseAgain(bool emptyHistory)
    {
        using var engine = Create();
        _executor.EmptyHistory = emptyHistory;
        File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(new LiveTradingEngine.LiveState
        {
            Position = new("SOL-USDT", TradeDirection.Long, 0.02m, 90m, 110m, DateTimeOffset.UtcNow)
        }));
        await engine.InitializeAsync(default);
        var state = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(Path.Combine(_directory, "state.json")))!;
        Assert.Null(state.Position);
        Assert.Contains(_journal.Events.Values, e => e.Kind == "closed_observed");
        Assert.Contains(_journal.Events.Values, e => e.Kind == "exchange_position_history");
        if (emptyHistory)
            Assert.Contains(_journal.Events.Values, e => e.Kind == "exchange_position_history" &&
                e.PayloadJson.Contains("\"HistoryAvailable\":false"));
        Assert.Equal(0, _executor.Writes);
    }

    [Fact]
    public async Task TimeExitKeepsPositionForHistoryRetryWithoutSendingAnotherClose()
    {
        using var engine = Create();
        File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(new LiveTradingEngine.LiveState
        {
            Position = new("SOL-USDT", TradeDirection.Long, 0.02m, 90m, 110m, DateTimeOffset.UtcNow.AddHours(-2), "trade")
        }));
        _executor.Positions = 1;
        await engine.InitializeAsync(default);
        _executor.HistoryUnavailable = true;
        var now = DateTimeOffset.UtcNow;
        var candle = new Candle(now.AddMinutes(-5), now, 100, 101, 99, 100, 1, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ProcessAsync(new("SOL-USDT", TimeFrame.FiveMinutes, candle), default));
        var state = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(Path.Combine(_directory, "state.json")))!;
        Assert.Null(state.PendingOrder);
        Assert.NotNull(state.Position);
        _executor.HistoryUnavailable = false;
        await engine.ProcessAsync(new("SOL-USDT", TimeFrame.FiveMinutes, candle with { OpenTime = candle.OpenTime.AddSeconds(1) }), default);
        Assert.Equal(1, _executor.Writes);
        Assert.Single(_journal.Events.Values.Where(e => e.Kind == "closed_observed"));
        Assert.Single(_journal.Events.Values.Where(e => e.Kind == "exchange_position_history"));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class FakeExecutor : ILiveExecutor
    {
        public bool HistoryUnavailable;
        public bool EmptyHistory;
        public Task<string> GetPositionHistoryAsync(string symbol, DateTimeOffset since, CancellationToken token)
        {
            if (HistoryUnavailable) throw new InvalidOperationException("History not ready");
            if (EmptyHistory) return Task.FromResult("{\"HistoryAvailable\":false,\"Positions\":[]}");
            return Task.FromResult("{\"Positions\":[{\"positionId\":\"test-position\",\"netProfit\":\"0.1\"}]}");
        }
        public decimal Balance = 1.0145m;
        public int Positions;
        public int Orders;
        public int Writes;
        public int ProtectionChecks;
        public bool Protected = true;
        public bool TimeoutOnOpen;
        public int OpenOrdersSent;
        public decimal LastQuantity;
        public decimal LastLeverage;
        public decimal MaximumLeverage = 20m;
        public Task<decimal> GetMaxLeverageAsync(string symbol, TradeDirection direction, CancellationToken token)
            => Task.FromResult(MaximumLeverage);
        public Task ValidatePositionModeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<int> GetOpenOrderCountAsync(CancellationToken token) => Task.FromResult(Orders);
        public Task<decimal> GetAvailableBalanceUsdtAsync(CancellationToken token) => Task.FromResult(Balance);
        public Task<int> GetOpenPositionCountAsync(CancellationToken token) => Task.FromResult(Positions);
        public Task<bool> HasOpenPositionAsync(string symbol, CancellationToken token) => Task.FromResult(Positions > 0);
        public Task ValidateProtectionAsync(string symbol, TradeDirection direction, decimal quantity, CancellationToken token)
        {
            ProtectionChecks++;
            if (!Protected) throw new InvalidOperationException("No SL/TP");
            return Task.CompletedTask;
        }
        public Task SetLeverageAsync(string symbol, TradeDirection direction, decimal leverage, CancellationToken token)
        { Writes++; LastLeverage = leverage; return Task.CompletedTask; }
        public Task<LiveOrderReceipt> OpenMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity,
            decimal stopLoss, decimal takeProfit, string id, CancellationToken token)
        {
            Writes++;
            OpenOrdersSent++;
            LastQuantity = quantity;
            if (TimeoutOnOpen) throw new TimeoutException();
            Positions = 1;
            return Task.FromResult(new LiveOrderReceipt(100m, "{\"orderId\":\"test\"}"));
        }
        public Task<LiveOrderReceipt> CloseMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity, CancellationToken token)
        { Writes++; Positions = 0; return Task.FromResult(new LiveOrderReceipt(100m, "{\"orderId\":\"test-close\"}")); }
    }
}
