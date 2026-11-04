using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.BingX;

namespace TradingBot.Tests;

public sealed class BingXMarketDataWorkerTests
{
    [Theory]
    [InlineData("rate-limit", 2)]
    [InlineData("timeout", 1)]
    [InlineData("network", 1)]
    [InlineData("partial-history", 1)]
    public async Task FailedSymbolRecoversOnHealthyStreamWithoutReloadingActiveSymbols(string failure, int failures)
    {
        var market = new RecoveringMarket(failure, failures);
        var candles = new InMemoryCandleStore();
        var updates = Channel.CreateUnbounded<MarketUpdate>();
        using var worker = new BingXMarketDataWorker(market, market, candles, new InMemoryContractStore(), updates,
            Options.Create(new BingXOptions { Symbols = ["BTC-USDT", "ETH-USDT"], SymbolRetrySeconds = 1 }),
            NullLogger<BingXMarketDataWorker>.Instance);
        try
        {
            await worker.StartAsync(default);
            await market.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "ETH-USDT" }, market.InitialSymbols);
            Assert.True(updates.Reader.TryRead(out var healthyUpdate));
            Assert.Equal("ETH-USDT", healthyUpdate.Symbol);
            Assert.False(healthyUpdate.IsReconciliation);

            await market.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Task.Delay(1100);
        }
        finally
        {
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.Equal(1, market.Connections);
        Assert.Equal(1, market.Loads["ETH-USDT"]);
        Assert.Equal(failures + 1, market.Loads["BTC-USDT"]);
        Assert.Equal(new[] { "BTC-USDT" }, market.AddedSymbols);
        foreach (var frame in new[] { TimeFrame.FiveMinutes, TimeFrame.ThirtyMinutes, TimeFrame.OneHour })
            Assert.Single(candles.Get("BTC-USDT", frame));
        Assert.Empty(candles.Get("BTC-USDT", TimeFrame.FifteenMinutes));
        // Initial history for a newly recovered symbol must not trigger retrospective entries.
        Assert.False(updates.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RetriesWhenAllSymbolsInitiallyFail()
    {
        var market = new RecoveringMarket("rate-limit", 1);
        using var worker = new BingXMarketDataWorker(market, market, new InMemoryCandleStore(),
            new InMemoryContractStore(), Channel.CreateUnbounded<MarketUpdate>(),
            Options.Create(new BingXOptions { Symbols = ["BTC-USDT"], SymbolRetrySeconds = 1 }),
            NullLogger<BingXMarketDataWorker>.Instance);
        try
        {
            await worker.StartAsync(default);
            await market.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.Equal(2, market.Loads["BTC-USDT"]);
        Assert.Equal(new[] { "BTC-USDT" }, market.InitialSymbols);
        Assert.Equal(1, market.Connections);
    }

    [Fact]
    public async Task StopCancelsPendingRetryWithoutWaitingForRetryInterval()
    {
        var market = new RecoveringMarket("rate-limit", int.MaxValue);
        using var worker = new BingXMarketDataWorker(market, market, new InMemoryCandleStore(),
            new InMemoryContractStore(), Channel.CreateUnbounded<MarketUpdate>(),
            Options.Create(new BingXOptions { Symbols = ["BTC-USDT", "ETH-USDT"], SymbolRetrySeconds = 60 }),
            NullLogger<BingXMarketDataWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await market.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(1, market.Loads["BTC-USDT"]);
    }

    [Fact]
    public async Task ReconnectLoadsNative5m30m1hAndReplaysMissed5mBarsWithoutNewEntries()
    {
        var market = new ReconnectingMarket();
        var candles = new InMemoryCandleStore();
        var updates = Channel.CreateUnbounded<MarketUpdate>();
        using var worker = new BingXMarketDataWorker(market, market, candles, new InMemoryContractStore(), updates,
            Options.Create(new BingXOptions { Symbols = ["BTC-USDT"], ReconnectDelaySeconds = 0 }),
            NullLogger<BingXMarketDataWorker>.Instance);
        await worker.StartAsync(default);
        await market.Reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);

        Assert.Equal(new[] { TimeFrame.FiveMinutes, TimeFrame.ThirtyMinutes, TimeFrame.OneHour,
            TimeFrame.FiveMinutes, TimeFrame.ThirtyMinutes, TimeFrame.OneHour }, market.RequestedFrames);
        foreach (var frame in market.RequestedFrames.Distinct())
            Assert.Equal(2, candles.Get("BTC-USDT", frame).Count);
        Assert.Empty(candles.Get("BTC-USDT", TimeFrame.FourHours));
        Assert.Empty(candles.Get("BTC-USDT", TimeFrame.FifteenMinutes));
        var replay = new List<MarketUpdate>();
        while (updates.Reader.TryRead(out var update)) replay.Add(update);
        Assert.Equal(TimeFrame.FiveMinutes, Assert.Single(replay).TimeFrame);
        Assert.All(replay, update => { Assert.True(update.IsReconciliation); Assert.True(update.Candle.IsClosed); });
    }

    private sealed class RecoveringMarket(string failure, int failures) : IBingXMarketClient, IBingXMarketStream
    {
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, int> Loads { get; } = new();
        public IReadOnlyList<string> InitialSymbols { get; private set; } = Array.Empty<string>();
        public List<string> AddedSymbols { get; } = new();
        public int Connections { get; private set; }
        private readonly DateTimeOffset _initialClose = DateTimeOffset.UtcNow.AddDays(-1);

        public Task<ContractInfo?> GetContractAsync(string symbol, CancellationToken cancellationToken)
        {
            Loads[symbol] = Loads.GetValueOrDefault(symbol) + 1;
            return Task.FromResult<ContractInfo?>(new ContractInfo(symbol, 4, 2, 0.0001m, 2m, true));
        }

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, TimeFrame frame, int limit, CancellationToken cancellationToken)
        {
            var failedFrame = failure == "partial-history" ? TimeFrame.ThirtyMinutes : TimeFrame.FiveMinutes;
            if (symbol == "BTC-USDT" && Loads[symbol] <= failures && frame == failedFrame)
                throw failure switch
                {
                    "timeout" => new TaskCanceledException("HTTP request timed out"),
                    "network" => new HttpRequestException("Connection reset"),
                    _ => new InvalidOperationException("BingX REST error 100410: 请稍后重试")
                };
            var minutes = frame switch
            {
                TimeFrame.FiveMinutes => 5,
                TimeFrame.FifteenMinutes => 15,
                TimeFrame.ThirtyMinutes => 30,
                TimeFrame.OneHour => 60,
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult<IReadOnlyList<Candle>>(
                [new Candle(_initialClose.AddMinutes(-minutes), _initialClose, 100, 102, 99, 101, 10, true)]);
        }

        public async Task StreamAsync(IReadOnlyList<string> symbols, ChannelReader<string> additionalSymbols,
            Func<MarketUpdate, Task> onUpdate, CancellationToken cancellationToken)
        {
            Connections++;
            InitialSymbols = symbols;
            if (symbols.Contains("ETH-USDT"))
            {
                var candle = new Candle(_initialClose, _initialClose.AddMinutes(5), 100, 102, 99, 101, 10, false);
                await onUpdate(new MarketUpdate("ETH-USDT", TimeFrame.FiveMinutes, candle));
                await onUpdate(new MarketUpdate("ETH-USDT", TimeFrame.FiveMinutes,
                    candle with { OpenTime = candle.CloseTime, CloseTime = candle.CloseTime.AddMinutes(5) }));
            }
            Connected.TrySetResult();
            await foreach (var symbol in additionalSymbols.ReadAllAsync(cancellationToken))
            {
                AddedSymbols.Add(symbol);
                Recovered.TrySetResult();
            }
        }
    }

    private sealed class ReconnectingMarket : IBingXMarketClient, IBingXMarketStream
    {
        public TaskCompletionSource Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<TimeFrame> RequestedFrames { get; } = new();
        private int _loads;
        private int _connections;
        private readonly DateTimeOffset _initialClose = DateTimeOffset.UtcNow.AddDays(-1);

        public Task<ContractInfo?> GetContractAsync(string symbol, CancellationToken cancellationToken)
        {
            _loads++;
            return Task.FromResult<ContractInfo?>(new ContractInfo(symbol, 4, 2, 0.0001m, 2m, true));
        }

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, TimeFrame frame, int limit, CancellationToken cancellationToken)
        {
            RequestedFrames.Add(frame);
            var minutes = frame switch
            {
                TimeFrame.FiveMinutes => 5,
                TimeFrame.FifteenMinutes => 15,
                TimeFrame.ThirtyMinutes => 30,
                TimeFrame.OneHour => 60,
                _ => throw new InvalidOperationException()
            };
            var first = new Candle(_initialClose.AddMinutes(-minutes), _initialClose, 100, 102, 99, 101, 10, true);
            var next = first with { OpenTime = _initialClose, CloseTime = _initialClose.AddMinutes(minutes) };
            return Task.FromResult<IReadOnlyList<Candle>>(_loads == 1 ? new[] { first } : new[] { first, next });
        }

        public async Task StreamAsync(IReadOnlyList<string> symbols, ChannelReader<string> additionalSymbols,
            Func<MarketUpdate, Task> onUpdate, CancellationToken cancellationToken)
        {
            if (++_connections == 1) return;
            Reconnected.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
