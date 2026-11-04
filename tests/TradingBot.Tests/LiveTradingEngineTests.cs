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
    private int _positionLimit;
    private decimal _marginUsage = 100m;

    private LiveTradingEngine Create()
    {
        Directory.CreateDirectory(_directory);
        // Keep these synthetic, distant retests focused on execution/sizing; entry filters have their own tests.
        return new LiveTradingEngine(_candles, Options.Create(new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m }),
            Options.Create(new RiskOptions { RiskPerTradePercent = 10m }),
            Options.Create(new LiveOptions { Enabled = true, AutoLeverage = _autoLeverage, MaxLeverage = 10,
                MaxOpenPositions = _positionLimit, MaxMarginUsagePercent = _marginUsage,
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
    [InlineData("10", false, 1, true, "0.01", 2, 2, "0.15")]
    [InlineData("100", false, 1, true, "0.01", 20, 3, "1.54")]
    [InlineData("0.1", false, 0, true, "0.01", 20, 0, "0")]
    [InlineData("10", false, 1, false, "0.01", 20, 5, "0.15", "BTC-USDT", 2)]
    [InlineData("0", false, 0, false, "0.01", 20, 0, "0", "BTC-USDT", 2)]
    [InlineData("10", false, 0, false, "0.01", 20, 0, "0", "SOL-USDT", 2)]
    [InlineData("10", false, 0, false, "0.01", 20, 0, "0", null, 2)]
    public async Task SmallBalanceEntryAndTimeoutAreHandled(string balance, bool timeout, int expectedOrders,
        bool adaptive, string minimumQuantity, int exchangeMaximum, int expectedLeverage, string expectedQuantity, string? manualSymbol = null, int manualOrders = 0)
    {
        _executor.ManualSymbol = manualSymbol;
        _executor.ManualOrders = manualOrders;
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
            foreach (var frame in new[] { TimeFrame.ThirtyMinutes, TimeFrame.OneHour })
            {
                var trend = 100m + i * 2m;
                var interval = frame == TimeFrame.ThirtyMinutes ? 30 : 60;
                var trendTime = end.AddMinutes(-interval * (80 - i));
                _candles.Upsert("SOL-USDT", frame,
                    new Candle(trendTime, trendTime.AddMinutes(interval), trend - 2m, trend + 0.5m, trend - 0.5m, trend, 100m, true));
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
            var breakout = _candles.Get("SOL-USDT", TimeFrame.FiveMinutes)[^1];
            // A newer queued candle must not replace the breakout currently being evaluated.
            _candles.Upsert("SOL-USDT", TimeFrame.FiveMinutes,
                new Candle(end, end.AddMinutes(5), 130m, 131m, 1m, 2m, 200m, true));
            await engine.ProcessAsync(new MarketUpdate("SOL-USDT", TimeFrame.FiveMinutes,
                breakout), default);
            var retest = new Candle(end, DateTimeOffset.UtcNow, 112m, 132m, 110m, 131m, 100m, true);
            _candles.Upsert("SOL-USDT", TimeFrame.FiveMinutes, retest);
            var update = new MarketUpdate("SOL-USDT", TimeFrame.FiveMinutes, retest);
            foreach (var frame in new[] { TimeFrame.FifteenMinutes, TimeFrame.ThirtyMinutes, TimeFrame.OneHour })
                await engine.ProcessAsync(update with { TimeFrame = frame }, default);
            await engine.ProcessAsync(update with { Candle = retest with { IsClosed = false } }, default);
            await engine.ProcessAsync(update with { IsReconciliation = true }, default);
            await engine.ProcessAsync(update with { Candle = retest with { CloseTime = DateTimeOffset.UtcNow.AddMinutes(-3) } }, default);
            await engine.ProcessAsync(update with { Candle = retest with { CloseTime = DateTimeOffset.UtcNow.AddMinutes(5) } }, default);
            Assert.Equal(0, _executor.OpenOrdersSent);
            if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => engine.ProcessAsync(update, default));
            else await engine.ProcessAsync(update, default);
            await engine.ProcessAsync(update, default);
            Assert.Equal(!timeout, engine.IsReady);
            Assert.Equal(expectedOrders, _executor.OpenOrdersSent);
            Assert.Equal(expectedLeverage, _executor.LastLeverage);
            Assert.Equal(decimal.Parse(expectedQuantity, System.Globalization.CultureInfo.InvariantCulture), _executor.LastQuantity);
            if (expectedOrders == 1)
            {
                var request = Assert.Single(_journal.Events.Values.Where(e => e.Kind == "entry_requested"));
                using var context = JsonDocument.Parse(request.PayloadJson);
                Assert.Equal(expectedLeverage, context.RootElement.GetProperty("Leverage").GetDecimal());
                Assert.Equal(81, context.RootElement.GetProperty("EntryCandles").GetArrayLength());
                Assert.Equal("5m", context.RootElement.GetProperty("EntryTimeFrame").GetString());
                Assert.Equal("30m", context.RootElement.GetProperty("MediumTrendTimeFrame").GetString());
                Assert.Equal("1h", context.RootElement.GetProperty("HigherTrendTimeFrame").GetString());
                var signalTime = context.RootElement.GetProperty("Signal").GetProperty("Time").GetDateTimeOffset();
                foreach (var property in new[] { "EntryCandles", "MediumTrendCandles", "HigherTrendCandles" })
                    Assert.All(context.RootElement.GetProperty(property).EnumerateArray(), candle =>
                        Assert.True(candle.GetProperty("CloseTime").GetDateTimeOffset() <= signalTime));
                var payload = context.RootElement;
                var estimatedLoss = payload.GetProperty("Quantity").GetDecimal() *
                    (payload.GetProperty("StopDistance").GetDecimal() +
                     (payload.GetProperty("Signal").GetProperty("EntryPrice").GetDecimal() +
                      payload.GetProperty("StopLoss").GetDecimal()) * 0.0005m);
                Assert.True(estimatedLoss <= payload.GetProperty("RiskBudget").GetDecimal());
                Assert.Equal(estimatedLoss, payload.GetProperty("EstimatedLossAtStop").GetDecimal());
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
        Assert.Equal(2, state.Version);
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
    public async Task ManualPositionDoesNotCauseWritesWithoutSignal()
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
    public async Task OutstandingOrdersOnUntrackedSymbolsDoNotBlockStartup()
    {
        _executor.Orders = 1;
        using var engine = Create();
        await engine.InitializeAsync(default);
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
    [InlineData(true, "BTC-USDT")]
    [InlineData(false, "BTC-USDT")]
    [InlineData(false)]
    public async Task RestoredPositionRequiresExchangeProtection(bool protectedPosition, string? manualSymbol = null)
    {
        using var engine = Create();
        File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(new LiveTradingEngine.LiveState
        {
            Version = 1, Position = new("SOL-USDT", TradeDirection.Long, 0.02m, 90m, 110m, DateTimeOffset.UtcNow)
        }));
        _executor.Positions = 1;
        _executor.ManualSymbol = manualSymbol;
        _executor.Protected = protectedPosition;
        if (protectedPosition) await engine.InitializeAsync(default);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => engine.InitializeAsync(default));
        if (protectedPosition)
        {
            var snapshot = File.ReadAllText(Path.Combine(_directory, "state.json"));
            var migrated = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(snapshot)!;
            Assert.Equal(2, migrated.Version);
            var saved = Assert.Single(migrated.Positions);
            Assert.Equal("SOL-USDT", saved.Symbol);
            Assert.Equal(0.02m, saved.Quantity);
            Assert.Equal(90m, saved.StopLoss);
            Assert.Equal(110m, saved.TakeProfit);
            Assert.DoesNotContain("\"Position\":", snapshot);
        }
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
    [InlineData(false, "BTC-USDT")]
    public async Task ExchangeClosedPositionIsRemovedWithoutSendingCloseAgain(bool emptyHistory, string? manualSymbol = null)
    {
        using var engine = Create();
        _executor.EmptyHistory = emptyHistory;
        _executor.ManualSymbol = manualSymbol;
        _executor.ManualOrders = manualSymbol is null ? 0 : 2;
        File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(new LiveTradingEngine.LiveState
        {
            Version = 1, Position = new("SOL-USDT", TradeDirection.Long, 0.02m, 90m, 110m, DateTimeOffset.UtcNow)
        }));
        await engine.InitializeAsync(default);
        var state = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(Path.Combine(_directory, "state.json")))!;
        Assert.Empty(state.Positions);
        Assert.Contains(_journal.Events.Values, e => e.Kind == "closed_observed");
        Assert.Contains(_journal.Events.Values, e => e.Kind == "exchange_position_history");
        if (emptyHistory)
            Assert.Contains(_journal.Events.Values, e => e.Kind == "exchange_position_history" &&
                e.PayloadJson.Contains("\"HistoryAvailable\":false"));
        Assert.Equal(0, _executor.Writes);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public async Task OldProtectedPositionStaysOpenUntilExchangeClosesIt(TradeDirection direction)
    {
        using var engine = Create();
        File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(new LiveTradingEngine.LiveState
        {
            Positions = new()
            {
                new("SOL-USDT", direction, 0.02m,
                    direction == TradeDirection.Long ? 90m : 110m,
                    direction == TradeDirection.Long ? 110m : 90m,
                    DateTimeOffset.UtcNow.AddDays(-2), "sol")
            }
        }));
        _executor.BotSymbols.Add("SOL-USDT");
        await engine.InitializeAsync(default);
        var now = DateTimeOffset.UtcNow;
        var candle = new Candle(now.AddMinutes(-5), now, 100m, 101m, 99m, 100m, 1m, true);

        await engine.ProcessAsync(new("SOL-USDT", TimeFrame.FiveMinutes, candle), default);

        var state = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(Path.Combine(_directory, "state.json")))!;
        Assert.Single(state.Positions);
        Assert.Null(state.PendingOrder);
        Assert.Equal(0, _executor.Writes);
        Assert.DoesNotContain(_journal.Events.Values, entry => entry.Kind == "close_requested");

        _executor.BotSymbols.Remove("SOL-USDT");
        await engine.ProcessAsync(new("SOL-USDT", TimeFrame.FiveMinutes,
            candle with { OpenTime = candle.OpenTime.AddSeconds(1) }), default);

        state = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(Path.Combine(_directory, "state.json")))!;
        Assert.Empty(state.Positions);
        Assert.Equal(0, _executor.Writes);
        Assert.Single(_journal.Events.Values.Where(entry => entry.Kind == "closed_observed"));
    }

    [Fact]
    public async Task ExchangeClosureRetriesHistoryWithoutSendingCloseOrder()
    {
        using var engine = Create();
        File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(new LiveTradingEngine.LiveState
        {
            Version = 1, Position = new("SOL-USDT", TradeDirection.Long, 0.02m, 90m, 110m, DateTimeOffset.UtcNow.AddHours(-2), "trade")
        }));
        _executor.Positions = 1;
        await engine.InitializeAsync(default);
        _executor.Positions = 0; // Exchange SL/TP closed the position.
        _executor.HistoryUnavailable = true;
        var now = DateTimeOffset.UtcNow;
        var candle = new Candle(now.AddMinutes(-5), now, 100, 101, 99, 100, 1, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ProcessAsync(new("SOL-USDT", TimeFrame.FiveMinutes, candle), default));
        var state = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(Path.Combine(_directory, "state.json")))!;
        Assert.Null(state.PendingOrder);
        Assert.Single(state.Positions);
        _executor.HistoryUnavailable = false;
        await engine.ProcessAsync(new("SOL-USDT", TimeFrame.FiveMinutes, candle with { OpenTime = candle.OpenTime.AddSeconds(1) }), default);
        Assert.Equal(0, _executor.Writes);
        Assert.Single(_journal.Events.Values.Where(e => e.Kind == "closed_observed"));
        Assert.Single(_journal.Events.Values.Where(e => e.Kind == "exchange_position_history"));
    }

    [Theory]
    [InlineData(0, false, null, 2)]
    [InlineData(1, false, null, 1)]
    [InlineData(2, false, null, 2)]
    [InlineData(0, false, "ETH-USDT", 1)]
    [InlineData(0, true, null, 2)]
    public async Task MultipleSignalsUseRemainingMarginAndSurviveRestart(int limit, bool timeoutSecond,
        string? manualSymbol, int expectedEntries)
    {
        _positionLimit = limit;
        _marginUsage = 20m;
        _executor.Balance = 100m;
        _executor.DeductMargin = true;
        _executor.ManualSymbol = manualSymbol ?? "BTC-USDT";
        _executor.ManualOrders = 2;
        var end = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var symbol in new[] { "SOL-USDT", "ETH-USDT" })
        {
            _contracts.Upsert(symbol, new ContractInfo(symbol, 2, 2, 0.01m, 2m, true));
            var close = 100m;
            for (var i = 0; i < 80; i++)
            {
                var open = close;
                close += i % 3 == 2 ? -0.6m : 0.5m;
                var time = end.AddMinutes(-5 * (80 - i));
                _candles.Upsert(symbol, TimeFrame.FiveMinutes, i == 79
                    ? new Candle(end.AddMinutes(-1), end, 129m, 131m, 129m, 130m, 200m, true)
                    : new Candle(time, time.AddMinutes(5), open, Math.Max(open, close) + 0.5m,
                        Math.Min(open, close) - 0.5m, close, 100m, true));
                foreach (var frame in new[] { TimeFrame.ThirtyMinutes, TimeFrame.OneHour })
                {
                    var interval = frame == TimeFrame.ThirtyMinutes ? 30 : 60;
                    var trend = 100m + i * 2m;
                    var trendTime = end.AddMinutes(-interval * (80 - i));
                    _candles.Upsert(symbol, frame, new Candle(trendTime, trendTime.AddMinutes(interval),
                        trend - 2m, trend + 0.5m, trend - 0.5m, trend, 100m, true));
                }
            }
        }
        using (var engine = Create())
        {
            await engine.InitializeAsync(default);
            foreach (var symbol in new[] { "SOL-USDT", "ETH-USDT" })
                await engine.ProcessAsync(new(symbol, TimeFrame.FiveMinutes, _candles.Get(symbol, TimeFrame.FiveMinutes)[^1]), default);
            foreach (var symbol in new[] { "SOL-USDT", "ETH-USDT" })
            {
                _executor.TimeoutOnOpen = timeoutSecond && symbol == "ETH-USDT";
                var retest = new Candle(end, DateTimeOffset.UtcNow, 112m, 132m, 110m, 131m, 100m, true);
                _candles.Upsert(symbol, TimeFrame.FiveMinutes, retest);
                var update = new MarketUpdate(symbol, TimeFrame.FiveMinutes, retest);
                if (_executor.TimeoutOnOpen) await Assert.ThrowsAsync<TimeoutException>(() => engine.ProcessAsync(update, default));
                else await engine.ProcessAsync(update, default);
                await engine.ProcessAsync(update, default);
            }
        }
        Assert.Equal(expectedEntries, _executor.OpenOrdersSent);
        Assert.DoesNotContain(_executor.ManualSymbol!, _executor.BotSymbols);
        var path = Path.Combine(_directory, "state.json");
        var state = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(path))!;
        Assert.Equal(2, state.Version);
        Assert.Equal(timeoutSecond ? 1 : expectedEntries, state.Positions.Count);
        var entries = _journal.Events.Values.Where(e => e.Kind == "entry_requested").ToArray();
        foreach (var entry in entries)
        {
            using var json = JsonDocument.Parse(entry.PayloadJson);
            var payload = json.RootElement;
            var balance = payload.GetProperty("AvailableBalance").GetDecimal();
            var quantity = payload.GetProperty("Quantity").GetDecimal();
            var price = payload.GetProperty("Signal").GetProperty("EntryPrice").GetDecimal();
            var leverage = payload.GetProperty("Leverage").GetDecimal();
            Assert.True(quantity * price * (1m / leverage + 0.001m) <= balance * 0.2m);
        }
        if (entries.Length == 2)
        {
            using var first = JsonDocument.Parse(entries[0].PayloadJson);
            using var second = JsonDocument.Parse(entries[1].PayloadJson);
            Assert.True(second.RootElement.GetProperty("Quantity").GetDecimal() < first.RootElement.GetProperty("Quantity").GetDecimal());
        }
        using var restarted = Create();
        var writes = _executor.Writes;
        if (timeoutSecond)
        {
            Assert.NotNull(state.PendingOrder);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.InitializeAsync(default));
        }
        else
        {
            await restarted.InitializeAsync(default);
            foreach (var held in state.Positions)
            {
                _executor.BotSymbols.Remove(held.Symbol);
                var now = DateTimeOffset.UtcNow;
                await restarted.ProcessAsync(new(held.Symbol, TimeFrame.FiveMinutes,
                    new Candle(now.AddMinutes(-5), now, 100, 101, 99, 100, 1, true)), default);
                var remaining = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(path))!;
                Assert.DoesNotContain(remaining.Positions, p => p.Symbol == held.Symbol);
                Assert.Equal(_executor.BotSymbols.Count, remaining.Positions.Count);
            }
        }
        Assert.Equal(writes, _executor.Writes);
    }

    [Fact]
    public async Task ExchangeClosureRemovesOnlyTheMatchingBotPosition()
    {
        using var engine = Create();
        File.WriteAllText(Path.Combine(_directory, "state.json"), JsonSerializer.Serialize(new LiveTradingEngine.LiveState
        {
            Positions = new()
            {
                new("SOL-USDT", TradeDirection.Long, 0.02m, 90m, 110m, DateTimeOffset.UtcNow.AddHours(-2), "sol"),
                new("ETH-USDT", TradeDirection.Long, 0.03m, 90m, 110m, DateTimeOffset.UtcNow.AddHours(-2), "eth")
            }
        }));
        _executor.BotSymbols.UnionWith(new[] { "SOL-USDT", "ETH-USDT" });
        _executor.ManualSymbol = "BTC-USDT";
        await engine.InitializeAsync(default);
        _executor.BotSymbols.Remove("SOL-USDT"); // Exchange SL/TP closure.
        var now = DateTimeOffset.UtcNow;
        await engine.ProcessAsync(new("SOL-USDT", TimeFrame.FiveMinutes,
            new Candle(now.AddMinutes(-5), now, 100, 101, 99, 100, 1, true)), default);
        var state = JsonSerializer.Deserialize<LiveTradingEngine.LiveState>(File.ReadAllText(Path.Combine(_directory, "state.json")))!;
        Assert.Equal("ETH-USDT", Assert.Single(state.Positions).Symbol);
        Assert.Equal("ETH-USDT", Assert.Single(_executor.BotSymbols));
        Assert.Equal(0, _executor.Writes);
        Assert.Equal("BTC-USDT", _executor.ManualSymbol);
    }

    [Theory]
    [InlineData("{\"Version\":3}")]
    [InlineData("{\"Version\":2,\"Positions\":null}")]
    [InlineData("{\"Version\":2,\"Positions\":[{\"Symbol\":\"SOL-USDT\",\"Quantity\":1},{\"Symbol\":\"sol-usdt\",\"Quantity\":1}]}")]
    [InlineData("{\"Version\":1,\"Position\":{\"Symbol\":\"SOL-USDT\",\"Quantity\":1},\"Positions\":[{\"Symbol\":\"ETH-USDT\",\"Quantity\":1}]}")]
    public async Task InvalidMultiPositionStateIsRejectedBeforeExchangeWrites(string json)
    {
        using var engine = Create();
        var path = Path.Combine(_directory, "state.json");
        File.WriteAllText(path, json);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.InitializeAsync(default));
        Assert.Equal(json, File.ReadAllText(path));
        Assert.Equal(0, _executor.Writes);
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
        public bool DeductMargin;
        public HashSet<string> BotSymbols = new(StringComparer.OrdinalIgnoreCase);
        public string? ManualSymbol;
        public int ManualOrders;
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
        public Task<int> GetOpenOrderCountAsync(CancellationToken token, string? symbol = null)
            => Task.FromResult(Orders + (symbol is null || ManualSymbol is null || symbol == ManualSymbol ? ManualOrders : 0));
        public Task<decimal> GetAvailableBalanceUsdtAsync(CancellationToken token) => Task.FromResult(Balance);
        public Task<int> GetOpenPositionCountAsync(CancellationToken token) => Task.FromResult(Positions + BotSymbols.Count + (ManualSymbol is null ? 0 : 1));
        public Task<bool> HasOpenPositionAsync(string symbol, CancellationToken token) => Task.FromResult((Positions > 0 && symbol == "SOL-USDT") || BotSymbols.Contains(symbol) || symbol == ManualSymbol);
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
            BotSymbols.Add(symbol);
            if (DeductMargin) Balance -= quantity * 131m * (1m / LastLeverage + 0.001m);
            return Task.FromResult(new LiveOrderReceipt(100m, "{\"orderId\":\"test\"}"));
        }
        public Task<LiveOrderReceipt> CloseMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity, CancellationToken token)
        { Writes++; Positions = 0; BotSymbols.Remove(symbol); return Task.FromResult(new LiveOrderReceipt(100m, "{\"orderId\":\"test-close\"}")); }
    }
}
