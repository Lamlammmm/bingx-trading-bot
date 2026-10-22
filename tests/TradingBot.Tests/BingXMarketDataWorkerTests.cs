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
    [Fact]
    public async Task ReconnectLoadsNative15m30m1hAndReplaysMissedEntryAndProtectionBarsWithoutNewEntries()
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

        Assert.Equal(new[] { TimeFrame.FiveMinutes, TimeFrame.FifteenMinutes, TimeFrame.ThirtyMinutes, TimeFrame.OneHour },
            market.RequestedFrames.Take(4));
        foreach (var frame in market.RequestedFrames.Distinct())
            Assert.Equal(2, candles.Get("BTC-USDT", frame).Count);
        Assert.Empty(candles.Get("BTC-USDT", TimeFrame.FourHours));
        var replay = new List<MarketUpdate>();
        while (updates.Reader.TryRead(out var update)) replay.Add(update);
        Assert.Equal(new[] { TimeFrame.FiveMinutes, TimeFrame.FifteenMinutes }, replay.Select(u => u.TimeFrame));
        Assert.All(replay, update => { Assert.True(update.IsReconciliation); Assert.True(update.Candle.IsClosed); });
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
            var minutes = frame switch { TimeFrame.FiveMinutes => 5, TimeFrame.FifteenMinutes => 15,
                TimeFrame.ThirtyMinutes => 30, TimeFrame.OneHour => 60, _ => throw new InvalidOperationException() };
            var first = new Candle(_initialClose.AddMinutes(-minutes), _initialClose, 100, 102, 99, 101, 10, true);
            var next = first with { OpenTime = _initialClose, CloseTime = _initialClose.AddMinutes(minutes) };
            return Task.FromResult<IReadOnlyList<Candle>>(_loads == 1 ? new[] { first } : new[] { first, next });
        }

        public async Task StreamAsync(IReadOnlyList<string> symbols, Func<MarketUpdate, Task> onUpdate, CancellationToken cancellationToken)
        {
            if (++_connections == 1) return;
            Reconnected.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
