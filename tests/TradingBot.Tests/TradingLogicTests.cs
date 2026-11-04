using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class TradingLogicTests
{
    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void Strategy_ReturnsDirectionalSignalForConfirmedBreakout(TradeDirection direction)
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions
        {
            FastEmaPeriod = 20,
            SlowEmaPeriod = 50,
            BreakoutLookback = 20,
            VolumeLookback = 20,
            VolumeMultiplier = 1.2m,
            AtrPeriod = 14,
            RsiOverbought = 101m, RsiOversold = -1m,
            MaxRetestEntryDistanceAtr = 0m // Synthetic distant retests here isolate the older directional/quality gates.
        }));
        var fiveMinute = BuildOscillatingCandles(80, TimeSpan.FromMinutes(5), 100m, 100m, 130m, 200m).ToList();
        var fifteenMinute = BuildCandles(80, TimeSpan.FromMinutes(15), 100m, 1m, 100m, 179m, 100m);
        var oneHour = BuildCandles(80, TimeSpan.FromHours(1), 100m, 2m, 100m, 258m, 100m);

        if (direction == TradeDirection.Short)
        {
            fiveMinute = fiveMinute.Select(c => c with { Open = 300m - c.Open, High = 300m - c.Low,
                Low = 300m - c.High, Close = 300m - c.Close }).ToList();
            fifteenMinute = fifteenMinute.Select(c => c with { Open = 300m - c.Open, High = 300m - c.Low,
                Low = 300m - c.High, Close = 300m - c.Close }).ToArray();
            oneHour = oneHour.Select(c => c with { Open = 300m - c.Open, High = 300m - c.Low,
                Low = 300m - c.High, Close = 300m - c.Close }).ToArray();
        }
        var breakoutSignal = strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour);
        Assert.Null(breakoutSignal); // breakout candle only arms a pending signal, awaiting retest confirmation

        var lastOpenTime = fiveMinute[^1].CloseTime;
        fiveMinute.Add(direction == TradeDirection.Long
            ? new Candle(lastOpenTime, lastOpenTime.AddMinutes(5), 130m, 132m, 129m, 131m, 100m, true)
            : new Candle(lastOpenTime, lastOpenTime.AddMinutes(5), 170m, 171m, 168m, 169m, 100m, true));
        Assert.Null(strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour));

        lastOpenTime = fiveMinute[^1].CloseTime;
        fiveMinute.Add(direction == TradeDirection.Long
            ? new Candle(lastOpenTime, lastOpenTime.AddMinutes(5), 112m, 132m, 110m, 131m, 100m, true)
            : new Candle(lastOpenTime, lastOpenTime.AddMinutes(5), 188m, 190m, 168m, 169m, 100m, true));
        var signal = strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour);

        Assert.NotNull(signal);
        Assert.Equal(direction, signal.Direction);
        Assert.Equal(direction == TradeDirection.Long ? 131m : 169m, signal.EntryPrice);
    }

    [Fact]
    public void Strategy_RejectsAbnormalBreakoutVolume()
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions
        {
            MaximumVolumeMultiplier = 3m,
            RsiOverbought = 101m,
            MaxRetestEntryDistanceAtr = 0m
        }));
        var fiveMinute = BuildOscillatingCandles(80, TimeSpan.FromMinutes(5), 100m, 100m, 130m, 400m).ToList();
        var fifteenMinute = BuildCandles(80, TimeSpan.FromMinutes(15), 100m, 1m, 100m, 179m, 100m);
        var oneHour = BuildCandles(80, TimeSpan.FromHours(1), 100m, 2m, 100m, 258m, 100m);

        Assert.Null(strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour));
        var openTime = fiveMinute[^1].CloseTime;
        fiveMinute.Add(new Candle(openTime, openTime.AddMinutes(5), 112m, 132m, 110m, 131m, 100m, true));

        Assert.Null(strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour));
    }

    [Fact]
    public void Strategy_RejectsWeakRetest()
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m }));
        var fiveMinute = BuildOscillatingCandles(80, TimeSpan.FromMinutes(5), 100m, 100m, 130m, 200m).ToList();
        var fifteenMinute = BuildCandles(80, TimeSpan.FromMinutes(15), 100m, 1m, 100m, 179m, 100m);
        var oneHour = BuildCandles(80, TimeSpan.FromHours(1), 100m, 2m, 100m, 258m, 100m);

        Assert.Null(strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour));
        var openTime = fiveMinute[^1].CloseTime;
        fiveMinute.Add(new Candle(openTime, openTime.AddMinutes(5), 131m, 132m, 110m, 131m, 100m, true));
        Assert.Null(strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour));
        openTime = fiveMinute[^1].CloseTime;
        fiveMinute.Add(new Candle(openTime, openTime.AddMinutes(5), 112m, 132m, 110m, 131m, 100m, true));

        Assert.Null(strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour));
    }

    [Fact]
    public void Strategy_DoesNotArmExcludedSymbol()
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions
        {
            ExcludedSymbols = ["BTC-USDT"],
            RsiOverbought = 101m,
            MaxRetestEntryDistanceAtr = 0m
        }));
        var fiveMinute = BuildOscillatingCandles(80, TimeSpan.FromMinutes(5), 100m, 100m, 130m, 200m).ToList();
        var fifteenMinute = BuildCandles(80, TimeSpan.FromMinutes(15), 100m, 1m, 100m, 179m, 100m);
        var oneHour = BuildCandles(80, TimeSpan.FromHours(1), 100m, 2m, 100m, 258m, 100m);

        Assert.Null(strategy.Evaluate("btc-usdt", fiveMinute, fifteenMinute, oneHour));
        var openTime = fiveMinute[^1].CloseTime;
        fiveMinute.Add(new Candle(openTime, openTime.AddMinutes(5), 112m, 132m, 110m, 131m, 100m, true));

        Assert.Null(strategy.Evaluate("BTC-USDT", fiveMinute, fifteenMinute, oneHour));
    }

    [Theory]
    [InlineData("trend-flipped")]
    [InlineData("bearish-retest")]
    [InlineData("forming-trend")]
    [InlineData("future-trend")]
    [InlineData("same-breakout-candle")]
    public void Strategy_RejectsRetestWithoutConfirmedDirectionalContext(string scenario)
    {
        var strategy = new BreakoutStrategy(Options.Create(new StrategyOptions { RsiOverbought = 101m, MaxRetestEntryDistanceAtr = 0m }));
        var entry = BuildOscillatingCandles(80, TimeSpan.FromMinutes(5), 100m, 100m, 130m, 200m).ToList();
        var medium = BuildCandles(80, TimeSpan.FromHours(1), 100m, 1m, 100m, 179m, 100m).ToList();
        var higher = BuildCandles(80, TimeSpan.FromHours(4), 100m, 2m, 100m, 258m, 100m).ToList();
        Assert.Null(strategy.Evaluate("SOL-USDT", entry, medium, higher));
        if (scenario == "same-breakout-candle")
        {
            Assert.Null(strategy.Evaluate("SOL-USDT", entry, medium, higher));
            return;
        }
        var time = entry[^1].CloseTime;
        entry.Add(scenario == "bearish-retest"
            ? new Candle(time, time.AddMinutes(5), 145m, 146m, 110m, 125m, 100m, true)
            : new Candle(time, time.AddMinutes(5), 112m, 132m, 110m, 131m, 100m, true));
        if (scenario == "trend-flipped")
            medium.Add(new Candle(time, time.AddMinutes(5), 179m, 180m, 49m, 50m, 100m, true));
        if (scenario is "forming-trend" or "future-trend")
        {
            // Only 49 usable closed trend candles: future/forming rows cannot provide the missing history.
            medium = medium.TakeLast(49).ToList();
            medium.Add(new Candle(time, scenario == "future-trend" ? time.AddHours(1) : time.AddMinutes(5),
                179m, 181m, 178m, 180m, 100m, scenario != "forming-trend"));
        }
        Assert.Null(strategy.Evaluate("SOL-USDT", entry, medium, higher));
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
