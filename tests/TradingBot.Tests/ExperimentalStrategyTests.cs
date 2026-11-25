using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class ExperimentalStrategyTests
{
    [Theory]
    [InlineData(false, TradeDirection.Long, MarketRegime.TrendingUp)]
    [InlineData(true, TradeDirection.Short, MarketRegime.TrendingDown)]
    public void ConfirmedPullbackIsSymmetricAndIgnoresFutureTrendBars(bool shortTrend, TradeDirection direction, MarketRegime regime)
    {
        var options = Options.Create(new StrategyOptions());
        var strategy = new ExperimentalStrategy(options, Options.Create(new ExperimentalStrategyOptions()));
        var (entry, medium, higher) = Pullback(shortTrend);
        var expectedAtr = TechnicalIndicators.Atr(TechnicalIndicators.AggregateClosed(entry[..^1], 15), options.Value.AtrPeriod);

        var signal = strategy.Evaluate("BTC-USDT", entry, medium, higher);
        Assert.NotNull(signal);
        Assert.Equal(direction, signal.Direction);
        Assert.Equal(regime, signal.Evaluation!.Regime);
        Assert.Equal(expectedAtr, signal.Atr);
        Assert.Equal(shortTrend ? entry[^2].High : entry[^2].Low, signal.InvalidationPrice);

        var future = higher[^1] with
        {
            OpenTime = higher[^1].CloseTime, CloseTime = higher[^1].CloseTime.AddHours(1), Close = 1m
        };
        Assert.Equal(signal, strategy.Evaluate("BTC-USDT", entry, medium, higher.Append(future).ToArray()));
    }

    [Fact]
    public void IncompleteRiskContextAndHighVolatilityBlockOtherwiseValidSetup()
    {
        var options = new ExperimentalStrategyOptions();
        var strategy = new ExperimentalStrategy(Options.Create(new StrategyOptions()), Options.Create(options));
        var (entry, medium, higher) = Pullback();

        Assert.Null(strategy.Evaluate("BTC-USDT", entry.Where((_, index) => index != 50).ToArray(), medium, higher));
        Assert.Equal("STALE_MARKET_DATA", strategy.LastEvaluation?.ReasonCode);

        options.MaximumAtrPercent = 0.2m;
        Assert.Null(strategy.Evaluate("BTC-USDT", entry, medium, higher));
        Assert.Equal(MarketRegime.HighVolatility, strategy.LastEvaluation?.Regime);
        Assert.Equal("MARKET_REGIME_UNSUITABLE", strategy.LastEvaluation?.ReasonCode);
    }

    internal static (Candle[] Entry, Candle[] Medium, Candle[] Higher) Pullback(bool shortTrend = false, DateTimeOffset? closeTime = null)
    {
        var end = closeTime ?? DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        Candle[] Bars(int count, int minutes) => Enumerable.Range(0, count).Select(i =>
        {
            var price = 100m + i * 0.1m;
            return new Candle(end.AddMinutes((i - count) * minutes), end.AddMinutes((i + 1 - count) * minutes),
                price, price + 0.45m, price - 0.4m, price + 0.1m, 100m, true);
        }).ToArray();
        var entry = Bars(60, 5);
        entry[^2] = entry[^2] with { Open = 105.9m, High = 105.95m, Low = 104.4m, Close = 104.5m };
        entry[^1] = entry[^1] with { Open = 104.7m, High = 106.1m, Low = 104.6m, Close = 106m };
        var medium = Bars(100, 30);
        var higher = Bars(220, 60);
        Candle[] Mirror(Candle[] candles) => candles.Select(c => c with
        {
            Open = 240m - c.Open, Close = 240m - c.Close, Low = 240m - c.High, High = 240m - c.Low
        }).ToArray();
        return shortTrend ? (Mirror(entry), Mirror(medium), Mirror(higher)) : (entry, medium, higher);
    }
}
