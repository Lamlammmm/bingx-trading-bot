using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class EntrySignalQualityTests
{
    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void ConfirmedRetestNearBreakoutLevelStillProducesSignal(TradeDirection direction)
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions()));
        var (entry, medium, higher, retest) = BuildContext(direction);
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
        entry.Add(retest);

        var signal = strategy.Evaluate("BTC-USDT", entry, medium, higher);

        Assert.NotNull(signal);
        Assert.Equal(direction, signal.Direction);
        Assert.Equal(retest.Close, signal.EntryPrice);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void OppositeDirectionBreakoutDoesNotArmRetest(TradeDirection direction)
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions { RsiOverbought = 101m, RsiOversold = -1m }));
        var (entry, medium, higher, retest) = BuildContext(direction);
        entry[^1] = direction == TradeDirection.Long
            ? entry[^1] with { Open = 103.2m, High = 103.3m, Low = 101.1m }
            : entry[^1] with { Open = 96.8m, High = 98.9m, Low = 96.7m };
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
        entry.Add(retest);

        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
    }

    [Theory]
    [InlineData(TradeDirection.Long, false)]
    [InlineData(TradeDirection.Short, false)]
    [InlineData(TradeDirection.Long, true)]
    [InlineData(TradeDirection.Short, true)]
    public void RetestCannotChasePriceOrInflateAtrWithBreakout(TradeDirection direction, bool inflatedBreakout)
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions { RsiOverbought = 101m, RsiOversold = -1m }));
        var (entry, medium, higher, retest) = BuildContext(direction);
        if (inflatedBreakout)
            entry[^1] = direction == TradeDirection.Long
                ? entry[^1] with { Close = 120m, High = 120.2m }
                : entry[^1] with { Close = 80m, Low = 79.8m };
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
        retest = direction == TradeDirection.Long
            ? retest with { Open = 100.5m, Close = inflatedBreakout ? 102m : 104m, High = inflatedBreakout ? 102.2m : 104.2m }
            : retest with { Open = 99.5m, Close = inflatedBreakout ? 98m : 96m, Low = inflatedBreakout ? 97.8m : 95.8m };
        entry.Add(retest);

        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
    }

    [Theory]
    [InlineData(TradeDirection.Long, false)]
    [InlineData(TradeDirection.Short, false)]
    [InlineData(TradeDirection.Long, true)]
    [InlineData(TradeDirection.Short, true)]
    public void StaleTrendRejectsAndCancelsPendingBreakout(TradeDirection direction, bool staleHigher)
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions { RsiOverbought = 101m, RsiOversold = -1m }));
        var (entry, medium, higher, retest) = BuildContext(direction);
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
        entry.Add(retest);
        var stale = (staleHigher ? higher : medium).Select(candle => candle with
        {
            OpenTime = candle.OpenTime.AddMinutes(staleHigher ? -60 : -30),
            CloseTime = candle.CloseTime.AddMinutes(staleHigher ? -60 : -30)
        }).ToList();

        Assert.Null(strategy.Evaluate("BTC-USDT", entry, staleHigher ? medium : stale, staleHigher ? stale : higher));
        entry.Add(retest with { OpenTime = retest.OpenTime.AddMinutes(5), CloseTime = retest.CloseTime.AddMinutes(5) });
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void FailedIntermediateCloseInvalidatesPendingEvenIfItsUpdateWasMissed(TradeDirection direction)
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions { RsiOverbought = 101m, RsiOversold = -1m }));
        var (entry, medium, higher, retest) = BuildContext(direction);
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
        entry.Add(direction == TradeDirection.Long
            ? retest with { Open = 100.8m, Close = 100.4m, Low = 100.2m }
            : retest with { Open = 99.2m, Close = 99.6m, High = 99.8m });
        // The store has the missed bar, but Evaluate only sees the latest update.
        entry.Add(retest with { OpenTime = retest.OpenTime.AddMinutes(5), CloseTime = retest.CloseTime.AddMinutes(5) });

        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
    }

    [Theory]
    [InlineData(TradeDirection.Long, true)]
    [InlineData(TradeDirection.Short, true)]
    [InlineData(TradeDirection.Long, false)]
    [InlineData(TradeDirection.Short, false)]
    public void RetestDistanceAcceptsExactLimitAndRejectsBeyondIt(TradeDirection direction, bool atLimit)
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions { RsiOverbought = 101m, RsiOversold = -1m }));
        var (entry, medium, higher, retest) = BuildContext(direction);
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
        // Before breakout, ATR is exactly 1.2 and the broken levels are 100.6 / 99.4.
        retest = direction == TradeDirection.Long
            ? retest with { Close = atLimit ? 101.8m : 101.8001m, High = 102m }
            : retest with { Close = atLimit ? 98.2m : 98.1999m, Low = 98m };
        entry.Add(retest);

        Assert.Equal(atLimit, strategy.Evaluate("BTC-USDT", entry, medium, higher) is not null);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void ZeroDistanceLimitExplicitlyRestoresUncappedRetests(TradeDirection direction)
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions
        {
            RsiOverbought = 101m, RsiOversold = -1m, MaxRetestEntryDistanceAtr = 0m
        }));
        var (entry, medium, higher, retest) = BuildContext(direction);
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
        entry.Add(direction == TradeDirection.Long
            ? retest with { Close = 104m, High = 104.2m }
            : retest with { Close = 96m, Low = 95.8m });

        Assert.NotNull(strategy.Evaluate("BTC-USDT", entry, medium, higher));
    }

    private static (List<Candle> Entry, List<Candle> Medium, List<Candle> Higher, Candle Retest) BuildContext(TradeDirection direction)
    {
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var entry = new List<Candle>();
        var medium = new List<Candle>();
        var higher = new List<Candle>();
        for (var index = 0; index < 60; index++)
        {
            var time = start.AddMinutes((index - 60) * 5);
            var close = index % 2 == 0 ? 100.2m : 100m;
            entry.Add(new Candle(time, time.AddMinutes(5), close == 100m ? 100.2m : 100m, 100.6m, 99.4m, close, 100m, true));
            var mediumEnd = start.AddMinutes(5 + (index - 59) * 30);
            var higherEnd = start.AddMinutes(5 + (index - 59) * 60);
            var trend = 100m + index * 0.2m;
            medium.Add(new Candle(mediumEnd.AddMinutes(-30), mediumEnd, trend - 0.2m, trend + 0.2m, trend - 0.4m, trend, 100m, true));
            higher.Add(new Candle(higherEnd.AddHours(-1), higherEnd, trend - 0.2m, trend + 0.2m, trend - 0.4m, trend, 100m, true));
        }
        entry.Add(new Candle(start, start.AddMinutes(5), 100.2m, 101.4m, 100.1m, 101.2m, 200m, true));
        var retest = new Candle(start.AddMinutes(5), start.AddMinutes(10), 100.55m, 101m, 100.5m, 100.9m, 100m, true);
        if (direction == TradeDirection.Short)
        {
            foreach (var candles in new[] { entry, medium, higher })
                for (var index = 0; index < candles.Count; index++)
                    candles[index] = candles[index] with { Open = 200m - candles[index].Open, High = 200m - candles[index].Low,
                        Low = 200m - candles[index].High, Close = 200m - candles[index].Close };
            retest = retest with { Open = 200m - retest.Open, High = 200m - retest.Low,
                Low = 200m - retest.High, Close = 200m - retest.Close };
        }
        return (entry, medium, higher, retest);
    }
}
