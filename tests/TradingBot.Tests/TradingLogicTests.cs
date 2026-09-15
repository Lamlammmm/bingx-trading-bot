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
        var fiveMinute = BuildOscillatingCandles(80, TimeSpan.FromMinutes(5), 100m, 100m, 130m, 200m).ToList();
        var fifteenMinute = BuildCandles(80, TimeSpan.FromMinutes(15), 100m, 1m, 100m, 179m, 100m);
        var oneHour = BuildCandles(80, TimeSpan.FromHours(1), 100m, 2m, 100m, 258m, 100m);

        var breakoutSignal = strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour);
        Assert.Null(breakoutSignal); // breakout candle only arms a pending signal, awaiting retest confirmation

        var lastOpenTime = fiveMinute[^1].CloseTime;
        fiveMinute.Add(new Candle(lastOpenTime, lastOpenTime.AddMinutes(5), 130m, 132m, 129m, 131m, 100m, true));
        var signal = strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour);

        Assert.NotNull(signal);
        Assert.Equal(TradeDirection.Long, signal.Direction);
        Assert.Equal(131m, signal.EntryPrice);
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
            MinimumQuantity = 0.0001m,
            MaxAggregateOpenRiskPercent = 100m,
            TargetProfitUsdt = 0m
        }));
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 2m, "test");

        var plan = riskManager.CreatePlan(signal, 10_000m, null, 0m, 0m, null);

        Assert.NotNull(plan);
        Assert.Equal(50m, plan.RiskAmount, 8);
        Assert.Equal(97m, plan.StopLoss);
        Assert.Equal(106m, plan.TakeProfit);
        Assert.Equal(50m / 3m, plan.Quantity, 8);
    }

    [Fact]
    public void RiskManager_RejectsWhenAggregateOpenRiskExceedsCap()
    {
        var riskManager = new RiskManager(Options.Create(new RiskOptions
        {
            RiskPerTradePercent = 0.5m,
            MaxDailyLossPercent = 2m,
            MaxLeverage = 3m,
            StopAtrMultiplier = 1.5m,
            TakeProfitRiskMultiple = 2m,
            FeeRate = 0.0005m,
            MinimumQuantity = 0.0001m,
            MaxAggregateOpenRiskPercent = 1m,
            TargetProfitUsdt = 0m
        }));
        var signal = new StrategySignal("ETH-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 2m, "test");

        // 0.5% risk on this trade would push aggregate open risk (0.6%) past the 1% cap.
        var plan = riskManager.CreatePlan(signal, 10_000m, null, 0m, 60m, null);

        Assert.Null(plan);
    }

    [Fact]
    public void RiskManager_AppliesContractPrecisionAndMinimums()
    {
        var riskManager = new RiskManager(Options.Create(new RiskOptions
        {
            RiskPerTradePercent = 0.5m,
            MaxLeverage = 3m,
            StopAtrMultiplier = 1.5m,
            TakeProfitRiskMultiple = 2m,
            MinimumQuantity = 0.0001m,
            MaxAggregateOpenRiskPercent = 100m,
            TargetProfitUsdt = 0m
        }));
        var signal = new StrategySignal("ETH-USDT", TradeDirection.Long, DateTimeOffset.UtcNow,
            100.129m, 2m, "test");
        var contract = new ContractInfo("ETH-USDT", 2m, 2m, 0.01m, 2m, true);

        var plan = riskManager.CreatePlan(signal, 10_000m, null, 0m, 0m, contract);

        Assert.NotNull(plan);
        Assert.Equal(100.12m, plan.EntryPrice);
        Assert.Equal(97.12m, plan.StopLoss);
        Assert.Equal(106.12m, plan.TakeProfit);
        Assert.Equal(16.66m, plan.Quantity);
    }

    [Fact]
    public void RiskManager_TargetsOneUsdtGrossProfitWithTwoToOneReward()
    {
        var riskManager = new RiskManager(Options.Create(new RiskOptions
        {
            RiskPerTradePercent = 0.5m,
            MaxLeverage = 1m,
            StopAtrMultiplier = 1.5m,
            TakeProfitRiskMultiple = 2m,
            MinimumQuantity = 0.0001m,
            MaxAggregateOpenRiskPercent = 100m,
            TargetProfitUsdt = 1m
        }));
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 2m, "test");

        var plan = riskManager.CreatePlan(signal, 100m, null, 0m, 0m, null);

        Assert.NotNull(plan);
        Assert.Equal(1m / 6m, plan.Quantity);
        Assert.Equal(0.5m, plan.RiskAmount, 8);
        Assert.Equal(1m, plan.Quantity * (plan.TakeProfit - plan.EntryPrice), 8);
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
