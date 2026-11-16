using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class MacdEntryFilterTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly MacdOptions Enabled = new() { Enabled = true };

    [Theory]
    [InlineData(TradeDirection.Long, 110, 1)]
    [InlineData(TradeDirection.Short, 90, -1)]
    public void ConfirmsCrossingFromZeroAndComputesValues(TradeDirection direction, int close, int sign)
    {
        var candles = Bars(close);
        var result = MacdEntryFilter.Evaluate(candles, Signal(candles[^1], direction), TimeFrame.FiveMinutes, Enabled);
        var expectedMacd = (close - 100) * (2m / 13m - 2m / 27m);

        Assert.True(result.Allowed);
        Assert.Equal(0m, result.PreviousHistogram);
        Assert.Equal(expectedMacd, result.Macd!.Value, 20);
        Assert.Equal(expectedMacd * 0.2m, result.Signal!.Value, 20);
        Assert.Equal(expectedMacd * 0.8m, result.Histogram!.Value, 20);
        Assert.Equal(sign, Math.Sign(result.Histogram.Value));
    }

    [Theory]
    [InlineData(TradeDirection.Long, 90, 106, -1)]
    [InlineData(TradeDirection.Short, 110, 94, 1)]
    public void ConfirmsHistogramCrossingRegardlessOfMacdZeroLine(TradeDirection direction, int priorClose, int close, int macdSign)
    {
        var candles = Bars(close);
        candles[^2] = candles[^2] with { Close = priorClose };
        var result = MacdEntryFilter.Evaluate(candles, Signal(candles[^1], direction), TimeFrame.FiveMinutes, Enabled);

        Assert.True(result.Allowed);
        Assert.Equal(macdSign, Math.Sign(result.Macd!.Value));
        Assert.Equal(macdSign, Math.Sign(result.PreviousHistogram!.Value));
        Assert.Equal(-macdSign, Math.Sign(result.Histogram!.Value));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(90)]
    public void RejectsFlatHistogramOrOppositeCross(int close)
    {
        var candles = Bars(close);
        var result = MacdEntryFilter.Evaluate(candles, Signal(candles[^1]), TimeFrame.FiveMinutes, Enabled);

        Assert.False(result.Allowed);
        Assert.Equal("no matching histogram crossover", result.Reason);
        Assert.NotNull(result.Histogram);
    }

    [Fact]
    public void RejectsWhenHistogramStaysOnTheSameSideOfZero()
    {
        var candles = Bars(110);
        candles[^2] = candles[^2] with { Close = 110m };
        var result = MacdEntryFilter.Evaluate(candles, Signal(candles[^1]), TimeFrame.FiveMinutes, Enabled);

        Assert.True(result.PreviousHistogram > 0m);
        Assert.True(result.Histogram > 0m);
        Assert.False(result.Allowed);
    }

    [Fact]
    public void RejectsWhenSignalCandleIsStillForming()
    {
        var candles = Bars(110);
        candles[^1] = candles[^1] with { IsClosed = false };
        var result = MacdEntryFilter.Evaluate(candles, Signal(candles[^1]), TimeFrame.FiveMinutes, Enabled);

        Assert.False(result.Allowed);
        Assert.Equal("missing closed signal candle", result.Reason);
    }

    [Fact]
    public void SortsDeduplicatesAndIgnoresCandlesAfterSignalTime()
    {
        var candles = Bars(110);
        var signal = Signal(candles[^1]);
        var future = candles[^1] with { OpenTime = signal.Time, CloseTime = signal.Time.AddMinutes(5), Close = 50m };
        var shuffled = candles.Reverse().Concat([future, candles[^2], candles[^1] with { IsClosed = false, Close = 1m }]).ToArray();

        var expected = MacdEntryFilter.Evaluate(candles, signal, TimeFrame.FiveMinutes, Enabled);
        Assert.True(expected.Allowed);
        Assert.Equal(expected, MacdEntryFilter.Evaluate(shuffled, signal, TimeFrame.FiveMinutes, Enabled));
    }

    [Fact]
    public void RequiresEnoughUniqueAndConsecutiveBars()
    {
        var candles = Bars(110);
        var signal = Signal(candles[^1]);
        var shortHistory = candles[1..].Append(candles[1]).ToArray();
        var result = MacdEntryFilter.Evaluate(shortHistory, signal, TimeFrame.FiveMinutes, Enabled);
        Assert.False(result.Allowed);
        Assert.Contains("warm-up (129/130", result.Reason);

        candles[30] = candles[30] with { OpenTime = candles[30].OpenTime.AddMinutes(1), CloseTime = candles[30].CloseTime.AddMinutes(1) };
        result = MacdEntryFilter.Evaluate(candles, signal, TimeFrame.FiveMinutes, Enabled);
        Assert.False(result.Allowed);
        Assert.Equal("gap in MACD candle history", result.Reason);
    }

    [Fact]
    public void RejectsConflictingDuplicatesAndInvalidPrices()
    {
        var candles = Bars(110);
        var signal = Signal(candles[^1]);
        var conflicting = candles.Append(candles[^2] with { Close = 110m }).ToArray();
        var result = MacdEntryFilter.Evaluate(conflicting, signal, TimeFrame.FiveMinutes, Enabled);
        Assert.False(result.Allowed);
        Assert.Equal("conflicting duplicate MACD candles", result.Reason);

        candles[^2] = candles[^2] with { Close = 0m };
        result = MacdEntryFilter.Evaluate(candles, signal, TimeFrame.FiveMinutes, Enabled);
        Assert.False(result.Allowed);
        Assert.Equal("invalid MACD candle price or duration", result.Reason);
    }

    [Fact]
    public void SupportsConfiguredPeriodsAndInclusiveCloseTimestamps()
    {
        var candles = Bars(110).TakeLast(15).Select(c => c with { CloseTime = c.CloseTime.AddMilliseconds(-1) }).ToArray();
        var result = MacdEntryFilter.Evaluate(candles, Signal(candles[^1]), TimeFrame.FiveMinutes,
            new MacdOptions { Enabled = true, Fast = 2, Slow = 3, Signal = 2 });

        Assert.True(result.Allowed);
        Assert.Equal(10m * (2m / 3m - 0.5m), result.Macd!.Value, 20);
    }

    [Theory]
    [InlineData(0, 26, 9)]
    [InlineData(12, 12, 9)]
    [InlineData(12, 11, 9)]
    [InlineData(12, 26, 0)]
    public void RejectsInvalidParameters(int fast, int slow, int signalPeriod)
    {
        var candles = Bars(110);
        var result = MacdEntryFilter.Evaluate(candles, Signal(candles[^1]), TimeFrame.FiveMinutes,
            new MacdOptions { Enabled = true, Fast = fast, Slow = slow, Signal = signalPeriod });

        Assert.False(result.Allowed);
        Assert.Equal("invalid MACD periods", result.Reason);
    }

    [Fact]
    public void DisabledFilterPassesWithoutHistory()
    {
        var result = MacdEntryFilter.Evaluate([], Signal(Bars(100)[^1]), TimeFrame.FiveMinutes, new MacdOptions());

        Assert.True(result.Allowed);
        Assert.Null(result.Macd);
    }

    private static Candle[] Bars(decimal finalClose) => Enumerable.Range(0, 130).Select(i =>
        new Candle(Start.AddMinutes(i * 5), Start.AddMinutes((i + 1) * 5), 100m, 120m, 80m,
            i == 129 ? finalClose : 100m, 1m, true)).ToArray();

    private static StrategySignal Signal(Candle candle, TradeDirection direction = TradeDirection.Long) =>
        new("BTC-USDT", direction, candle.CloseTime, candle.Close, 1m, "test signal");
}
