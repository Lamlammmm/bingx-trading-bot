using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class MultiTimeframeTests
{
    [Fact]
    public void AlignedTrendAndRetestProduceSignalWithSetupTimeframeRisk()
    {
        var options = new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 2m,
            MaximumSetupDistanceAtr = 10m };
        var strategy = new MultiTimeframeStrategy(Options.Create(options));
        var entry = Bars(900, 5, 0.01m).ToList();
        var medium = Bars(250, 30, 0.036m);
        var higher = Bars(250, 60, 0.036m);
        var last = entry[^1];
        entry[^1] = last with { Open = 109m, High = 109.7m, Low = 108.99m, Close = 109.6m, Volume = 200m };
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
        entry.Add(new(last.CloseTime, last.CloseTime.AddMinutes(5), 109.1m, 109.45m, 109.05m, 109.4m, 100m, true));
        var signal = strategy.Evaluate("BTC-USDT", entry, medium, higher);
        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Long, signal.Direction);
        Assert.Equal(MarketRegime.TrendingUp, signal.Evaluation!.Regime);
        Assert.Equal("HH/HL", signal.Evaluation.Structure);
        Assert.Equal(TechnicalIndicators.Atr(TechnicalIndicators.AggregateClosed(entry, 15), options.AtrPeriod), signal.Atr);
        Assert.True(signal.InvalidationPrice < entry[^1].Low);
    }

    [Fact]
    public void FlatRsiIsNeutralAndDirectionalAdxIsOneHundred()
    {
        var flat = Bars(100, 5, 0m);
        Assert.Equal(50m, TechnicalIndicators.Rsi(flat, 14));
        Assert.Equal(0m, TechnicalIndicators.Adx(flat, 14));
        Assert.Equal(100m, TechnicalIndicators.Adx(Bars(100, 5, 1m), 14));
        Assert.Equal(100m, TechnicalIndicators.Adx(Bars(100, 5, -1m), 14));
    }

    [Fact]
    public void SetupAggregationRejectsIncompleteDuplicateAndFormingBuckets()
    {
        var bars = Bars(9, 5, 1m);
        Assert.Equal(3, TechnicalIndicators.AggregateClosed(bars, 15).Count);
        Assert.Equal(2, TechnicalIndicators.AggregateClosed(bars.Take(8).ToArray(), 15).Count);
        Assert.Equal(2, TechnicalIndicators.AggregateClosed(bars.Where((_, i) => i != 4).ToArray(), 15).Count);
        Assert.Equal(2, TechnicalIndicators.AggregateClosed(bars.Append(bars[4]).ToArray(), 15).Count);
        var forming = bars.Select((c, i) => c with { IsClosed = i != 8 }).ToArray();
        Assert.Equal(2, TechnicalIndicators.AggregateClosed(forming, 15).Count);
        var aggregate = TechnicalIndicators.AggregateClosed(bars, 15)[0];
        Assert.Equal(bars[0].Open, aggregate.Open);
        Assert.Equal(bars[2].Close, aggregate.Close);
        Assert.Equal(300m, aggregate.Volume);
    }

    [Theory]
    [InlineData(0, MarketRegime.LowVolatility)]
    [InlineData(1, MarketRegime.HighVolatility)]
    [InlineData(2, MarketRegime.Sideways)]
    public void UntradableRegimesHaveExplicitNoTradeReason(int scenario, MarketRegime expected)
    {
        var options = new StrategyOptions { MinimumAtrPercent = scenario == 0 ? 10m : 0m,
            MaximumAtrPercent = scenario == 1 ? 0.01m : 100m, MinimumAdx = scenario == 2 ? 101m : 20m };
        var strategy = new MultiTimeframeStrategy(Options.Create(options));
        Assert.Null(strategy.Evaluate("BTC-USDT", Bars(900, 5, 0.01m), Bars(250, 30, 0.1m), Bars(250, 60, 0.1m)));
        Assert.Equal(expected, strategy.LastEvaluation!.Regime);
        Assert.Equal("market regime does not permit a trend entry", strategy.LastEvaluation.Reason);
    }

    [Fact]
    public void FutureAndUnclosedHigherBarsCannotAffectDecision()
    {
        var options = Options.Create(new StrategyOptions());
        var higher = Bars(250, 60, 0.1m);
        var entry = Bars(900, 5, 0.01m);
        var medium = Bars(250, 30, 0.1m);
        var baseline = new MultiTimeframeStrategy(options);
        baseline.Evaluate("BTC-USDT", entry, medium, higher);
        var changed = new MultiTimeframeStrategy(options);
        var future = higher[^1] with { OpenTime = higher[^1].CloseTime, CloseTime = higher[^1].CloseTime.AddHours(1), Close = 1m };
        changed.Evaluate("BTC-USDT", entry, medium, higher.Append(future).Append(future with { IsClosed = false }).ToArray());
        Assert.Equal(baseline.LastEvaluation, changed.LastEvaluation);
    }

    private static Candle[] Bars(int count, int minutes, decimal step)
    {
        var end = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        return Enumerable.Range(0, count).Select(i => new Candle(end.AddMinutes((i - count) * minutes),
            end.AddMinutes((i - count + 1) * minutes), 100m + i * step,
            100m + i * step + Math.Abs(step) + 0.1m, 100m + i * step - Math.Abs(step) - 0.1m,
            100m + (i + 1) * step, 100m, true)).ToArray();
    }
}
