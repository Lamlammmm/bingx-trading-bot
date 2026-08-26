using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class TradingLogicTests
{
    [Fact]
    public void Strategy_ReturnsLongSignalForConfirmedBreakout()
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions
        {
            FastEmaPeriod = 20,
            SlowEmaPeriod = 50,
            BreakoutLookback = 20,
            VolumeLookback = 20,
            VolumeMultiplier = 1.2m,
            AtrPeriod = 14
        }));
        var fifteenMinute = BuildOscillatingCandles(80, TimeSpan.FromMinutes(15), 100m, 100m, 130m, 200m);
        var oneHour = BuildCandles(80, TimeSpan.FromHours(1), 100m, 1m, 100m, 179m, 100m);
        var fourHour = BuildCandles(80, TimeSpan.FromHours(4), 100m, 2m, 100m, 258m, 100m);

        var signal = strategy.Evaluate("BTC-USDT", fifteenMinute, oneHour, fourHour);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Long, signal.Direction);
        Assert.Equal(130m, signal.EntryPrice);
    }

    [Fact]
    public void RiskManager_UsesConfiguredRiskAndReward()
    {
        var riskManager = new RiskManager(Options.Create(new RiskOptions
        {
            RiskPerTradePercent = 0.5m,
            MaxDailyLossPercent = 2m,
            MaxLeverage = 3m,
            StopAtrMultiplier = 1.5m,
            TakeProfitRiskMultiple = 2m,
            FeeRate = 0.0005m,
            MinimumQuantity = 0.0001m
        }));
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 2m, "test");

        var plan = riskManager.CreatePlan(signal, 10_000m, null, 0m);

        Assert.NotNull(plan);
        Assert.Equal(50m, plan.RiskAmount);
        Assert.Equal(97m, plan.StopLoss);
        Assert.Equal(106m, plan.TakeProfit);
        Assert.Equal(50m / 3m, plan.Quantity, 8);
    }

    private static IReadOnlyList<Candle> BuildCandles(int count, TimeSpan interval, decimal start, decimal step,
        decimal previousVolume, decimal finalClose, decimal finalVolume)
    {
        var candles = new List<Candle>(count);
        var openTime = DateTimeOffset.UtcNow.Add(-interval * count);
        for (var index = 0; index < count - 1; index++)
        {
            var close = start + (step * index);
            candles.Add(new Candle(openTime, openTime.Add(interval), close - step, close + 0.5m, close - 0.5m,
                close, previousVolume, true));
            openTime = openTime.Add(interval);
        }

        candles.Add(new Candle(openTime, openTime.Add(interval), finalClose - 1m, finalClose + 1m, finalClose - 1m,
            finalClose, finalVolume, true));
        return candles;
    }

    private static IReadOnlyList<Candle> BuildOscillatingCandles(int count, TimeSpan interval, decimal start,
        decimal previousVolume, decimal finalClose, decimal finalVolume)
    {
        var candles = new List<Candle>(count);
        var openTime = DateTimeOffset.UtcNow.Add(-interval * count);
        var close = start;
        for (var index = 0; index < count - 1; index++)
        {
            var step = index % 3 == 2 ? -0.6m : 0.5m;
            var open = close;
            close += step;
            var high = Math.Max(open, close) + 0.5m;
            var low = Math.Min(open, close) - 0.5m;
            candles.Add(new Candle(openTime, openTime.Add(interval), open, high, low, close, previousVolume, true));
            openTime = openTime.Add(interval);
        }

        candles.Add(new Candle(openTime, openTime.Add(interval), finalClose - 1m, finalClose + 1m, finalClose - 1m,
            finalClose, finalVolume, true));
        return candles;
    }
}
