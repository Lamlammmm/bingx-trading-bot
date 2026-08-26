using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.BingX;

namespace TradingBot.Tests;

public sealed class BingXWebSocketClientTests
{
    [Fact]
    public void ParseUpdates_ParsesLiveFlatArrayPayload()
    {
        const string payload = """
            {"code":0,"dataType":"BTC-USDT@kline_15m","s":"BTC-USDT","data":[{"c":"78005.4","o":"78090.9","h":"78141.4","l":"77955.6","v":"164.5371","T":1787750100000}]}
            """;

        var update = Assert.Single(BingXWebSocketClient.ParseUpdates(payload));

        Assert.Equal(TimeFrame.FifteenMinutes, update.TimeFrame);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787750100000), update.Candle.OpenTime);
        Assert.Equal(update.Candle.OpenTime.AddMinutes(15), update.Candle.CloseTime);
        Assert.Equal(78090.9m, update.Candle.Open);
        Assert.Equal(78141.4m, update.Candle.High);
        Assert.Equal(77955.6m, update.Candle.Low);
        Assert.Equal(78005.4m, update.Candle.Close);
        Assert.Equal(164.5371m, update.Candle.Volume);
        Assert.False(update.Candle.IsClosed);
    }

    [Fact]
    public void ParseUpdates_PreservesDocumentedNestedPayload()
    {
        const string payload = """
            {"dataType":"BTC-USDT@kline_15m","data":{"K":{"t":1787750100000,"T":1787750999999,"o":"100","h":"110","l":"90","c":"105","v":"12"}}}
            """;

        var update = Assert.Single(BingXWebSocketClient.ParseUpdates(payload));

        Assert.Equal(TimeFrame.FifteenMinutes, update.TimeFrame);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787750100000), update.Candle.OpenTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787750999999), update.Candle.CloseTime);
        Assert.Equal(105m, update.Candle.Close);
        Assert.False(update.Candle.IsClosed);
    }

    [Fact]
    public void ClosedCandleTracker_EmitsLatestSnapshotOnceOnRollover()
    {
        var tracker = new ClosedCandleTracker();
        var firstOpen = DateTimeOffset.FromUnixTimeMilliseconds(1787750100000);
        var first = CreateUpdate(firstOpen, 100m, 10m);
        var latest = CreateUpdate(firstOpen, 105m, 12m);
        var next = CreateUpdate(firstOpen.AddMinutes(15), 106m, 1m);

        Assert.Null(tracker.Observe(first));
        Assert.Null(tracker.Observe(latest));

        var closed = tracker.Observe(next);

        Assert.NotNull(closed);
        Assert.True(closed.Candle.IsClosed);
        Assert.Equal(firstOpen, closed.Candle.OpenTime);
        Assert.Equal(105m, closed.Candle.Close);
        Assert.Equal(12m, closed.Candle.Volume);
        Assert.Null(tracker.Observe(next));
    }

    [Fact]
    public void ClosedCandleTracker_DiscardsAStaleSnapshotAfterRestReconciliation()
    {
        var tracker = new ClosedCandleTracker();
        var firstOpen = DateTimeOffset.FromUnixTimeMilliseconds(1787750100000);
        Assert.Null(tracker.Observe(CreateUpdate(firstOpen, 100m, 10m)));

        tracker.DiscardThrough(TimeFrame.FifteenMinutes, firstOpen.AddMinutes(15));

        var current = CreateUpdate(firstOpen.AddMinutes(30), 103m, 2m);
        Assert.Null(tracker.Observe(current));
        var closed = tracker.Observe(CreateUpdate(firstOpen.AddMinutes(45), 104m, 1m));
        Assert.NotNull(closed);
        Assert.Equal(current.Candle.OpenTime, closed.Candle.OpenTime);
    }

    private static MarketUpdate CreateUpdate(DateTimeOffset openTime, decimal close, decimal volume) =>
        new(TimeFrame.FifteenMinutes,
            new Candle(openTime, openTime.AddMinutes(15), close - 1m, close + 1m, close - 2m, close, volume, false));
}
