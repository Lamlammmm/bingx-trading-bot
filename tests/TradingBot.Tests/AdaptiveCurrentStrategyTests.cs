using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class AdaptiveCurrentStrategyTests
{
    [Theory]
    [InlineData(false, TradeDirection.Long)]
    [InlineData(true, TradeDirection.Short)]
    public void AdaptiveCurrentOnlyTakesConfirmedPullbackOncePerBar(bool shortTrend, TradeDirection direction)
    {
        var (entry, medium, higher) = ExperimentalStrategyTests.Pullback(shortTrend);
        var early = Enumerable.Range(1, 6).Reverse().Select(bars => entry[0] with
        {
            OpenTime = entry[0].OpenTime.AddMinutes(-5 * bars),
            CloseTime = entry[0].CloseTime.AddMinutes(-5 * bars)
        });
        entry = early.Concat(entry).ToArray();
        var options = new StrategyOptions { UseMultiTimeframeAnalysis = true, RsiOverbought = 70m, RsiOversold = 30m, SignalCooldownBars = 0 };
        Assert.Null(new MultiTimeframeStrategy(Options.Create(options)).Evaluate("BTC-USDT", entry, medium, higher));

        options.UseAdaptiveCurrentEntries = true;
        var strategy = new MultiTimeframeStrategy(Options.Create(options));
        var futureTrend = higher[^1] with
        {
            OpenTime = higher[^1].CloseTime, CloseTime = higher[^1].CloseTime.AddHours(1), Close = 1m
        };
        Assert.Null(strategy.Evaluate("BTC-USDT", entry[..^1], medium, higher));
        var signal = strategy.Evaluate("BTC-USDT", entry, medium, higher.Append(futureTrend).ToArray());
        Assert.NotNull(signal);
        Assert.Equal(direction, signal.Direction);
        Assert.Equal("pullback-reclaim", signal.Evaluation?.Setup);
        Assert.Equal(shortTrend ? entry[^2].High : entry[^2].Low, signal.InvalidationPrice);
        Assert.Equal(TechnicalIndicators.Atr(TechnicalIndicators.AggregateClosed(entry[..^1], 15), options.AtrPeriod), signal.Atr);
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
        Assert.Equal("SIGNAL_COOLDOWN_ACTIVE", strategy.LastEvaluation?.ReasonCode);
    }

    [Theory]
    [InlineData(false, "breakout-retest", "mixed")]
    [InlineData(true, "emerging-breakout-retest", "HH/HL")]
    public void AdaptiveTrendRequiresBreakoutRetest(bool emerging, string setup, string structure)
    {
        var (_, medium, higher) = ExperimentalStrategyTests.Pullback();
        var options = new StrategyOptions { UseMultiTimeframeAnalysis = true, UseAdaptiveCurrentEntries = true, RsiOverbought = 90m };
        if (emerging)
        {
            higher = higher.Select((bar, i) =>
            {
                var price = 100m + 0.03m * i + (i % 2 == 1 ? 0.22m : 0m) + 0.3m * Math.Max(0, i - 212);
                return bar with { Open = price - 0.1m, Close = price, Low = price - 0.4m, High = price + 0.45m };
            }).ToArray();
            options.MinimumAdx = TechnicalIndicators.Adx(higher, options.AdxPeriod) + 1m;
        }
        else
        {
            medium[^1] = medium[^1] with { Low = medium[^13].Low };
            medium[^2] = medium[^2] with { Low = medium[^13].Low };
        }
        var time = higher[^1].CloseTime;
        var entry = Enumerable.Range(0, 66).Select(i =>
        {
            var open = time.AddMinutes(5 * (i - 66));
            return new Candle(open, open.AddMinutes(5), 100.3m, 102m, 98m, i % 2 == 0 ? 100.4m : 100.2m, 100m, true);
        }).ToArray();
        entry[^2] = entry[^2] with { Close = 102.25m, High = 102.4m, Low = 100.2m, Volume = 120m };
        entry[^1] = entry[^1] with { Open = 101.95m, Close = 102.2m, High = 102.25m, Low = 101.9m };
        var strategy = new MultiTimeframeStrategy(Options.Create(options));

        Assert.Null(strategy.Evaluate("BTC-USDT", entry[..^1], medium, higher));
        Assert.Equal("RETEST_PENDING", strategy.LastEvaluation?.ReasonCode);
        var signal = strategy.Evaluate("BTC-USDT", entry, medium, higher);
        Assert.NotNull(signal);
        Assert.Equal(setup, signal.Evaluation?.Setup);
        Assert.Equal(structure, signal.Evaluation?.Structure);
        Assert.Equal(101.9m, signal.InvalidationPrice);

        var blocked = new MultiTimeframeStrategy(Options.Create(options));
        Assert.Null(blocked.Evaluate("BTC-USDT", entry[..^1], medium, higher));
        Assert.Null(blocked.Evaluate("BTC-USDT", entry.Where((_, i) => i != 30).ToArray(), medium, higher));
        Assert.Equal("STALE_MARKET_DATA", blocked.LastEvaluation?.ReasonCode);
        Assert.Null(blocked.Evaluate("BTC-USDT", entry, medium, higher));
        options.MaximumAtrPercent = 0.2m;
        Assert.Null(blocked.Evaluate("BTC-USDT", entry, medium, higher));
        Assert.Equal(MarketRegime.HighVolatility, blocked.LastEvaluation?.Regime);
    }
}
