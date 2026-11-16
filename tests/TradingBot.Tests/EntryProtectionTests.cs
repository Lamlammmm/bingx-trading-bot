using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class EntryProtectionTests
{
    private static readonly ContractInfo Contract = new("BTC-USDT", 4, 2, 0.0001m, 2m, true);

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void StructuralStopClearsRetestByOneTickAndTargetUsesChosenRisk(TradeDirection direction)
    {
        var manager = new RiskManager(Options.Create(new RiskOptions { FeeRate = 0m }));
        var signal = new StrategySignal("BTC-USDT", direction, DateTimeOffset.UtcNow, 100m, 1m, "retest",
            InvalidationPrice: direction == TradeDirection.Long ? 98.205m : 101.795m);

        var protection = manager.CreateProtection(signal, Contract, 1.5m, 2m, out var reason);

        Assert.NotNull(protection);
        Assert.Null(reason);
        Assert.Equal(direction == TradeDirection.Long ? 98.19m : 101.81m, protection.Value.StopLoss);
        Assert.Equal(direction == TradeDirection.Long ? 103.62m : 96.38m, protection.Value.TakeProfit);
        Assert.Equal(1.81m, protection.Value.LossPerUnitAtStop);
        Assert.Equal(3.62m, protection.Value.NetRewardPerUnit);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void RetestDoesNotPullAFartherAtrStopCloser(TradeDirection direction)
    {
        var manager = new RiskManager(Options.Create(new RiskOptions { FeeRate = 0m }));
        var signal = new StrategySignal("BTC-USDT", direction, DateTimeOffset.UtcNow, 100m, 1m, "retest",
            InvalidationPrice: direction == TradeDirection.Long ? 99m : 101m);

        var protection = manager.CreateProtection(signal, Contract, 1.5m, 2m, out var reason);

        Assert.NotNull(protection);
        Assert.Null(reason);
        Assert.Equal(direction == TradeDirection.Long ? 98.5m : 101.5m, protection.Value.StopLoss);
        Assert.Equal(direction == TradeDirection.Long ? 103m : 97m, protection.Value.TakeProfit);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void StructuralStopBeyondMaximumAtrRejectsInsteadOfMovingInsideWick(TradeDirection direction)
    {
        var manager = new RiskManager(Options.Create(new RiskOptions { FeeRate = 0m, MaximumStopAtrMultiplier = 3m }));
        var signal = new StrategySignal("BTC-USDT", direction, DateTimeOffset.UtcNow, 100m, 1m, "retest",
            InvalidationPrice: direction == TradeDirection.Long ? 97m : 103m);

        var protection = manager.CreateProtection(signal, Contract, 1.5m, 2m, out var reason);

        Assert.Null(protection);
        Assert.Equal("stop is invalid or exceeds maximum ATR distance", reason);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void DefaultTargetUsesSuggestedStopAfterOutwardRounding(TradeDirection direction)
    {
        var manager = new RiskManager(Options.Create(new RiskOptions { FeeRate = 0m }));
        var signal = new StrategySignal("BTC-USDT", direction, DateTimeOffset.UtcNow, 100m, 1m, "suggested stop",
            SuggestedStopLoss: direction == TradeDirection.Long ? 98.234m : 101.766m);

        var protection = manager.CreateProtection(signal, Contract, 1.5m, 2m, out var reason);

        Assert.NotNull(protection);
        Assert.Null(reason);
        Assert.Equal(direction == TradeDirection.Long ? 98.23m : 101.77m, protection.Value.StopLoss);
        Assert.Equal(direction == TradeDirection.Long ? 103.54m : 96.46m, protection.Value.TakeProfit);
    }

    [Theory]
    [InlineData(TradeDirection.Long, true)]
    [InlineData(TradeDirection.Short, true)]
    [InlineData(TradeDirection.Long, false)]
    [InlineData(TradeDirection.Short, false)]
    public void ExplicitAiTargetIsRespectedAndStillMustPassRewardGate(TradeDirection direction, bool sufficientReward)
    {
        var manager = new RiskManager(Options.Create(new RiskOptions()));
        var target = direction == TradeDirection.Long
            ? sufficientReward ? 110m : 103m
            : sufficientReward ? 90m : 97m;
        var signal = new StrategySignal("BTC-USDT", direction, DateTimeOffset.UtcNow, 100m, 2m, "AI levels",
            SuggestedStopLoss: direction == TradeDirection.Long ? 97m : 103m,
            SuggestedTakeProfit: target, Source: "openai");

        var protection = manager.CreateProtection(signal, Contract, 1.5m, 2m, out var reason);

        if (sufficientReward)
        {
            Assert.NotNull(protection);
            Assert.Null(reason);
            Assert.Equal(target, protection.Value.TakeProfit);
        }
        else
        {
            Assert.Null(protection);
            Assert.Equal("reward/risk after fees is below minimum", reason);
        }
    }

    [Theory]
    [InlineData(TradeDirection.Long, false)]
    [InlineData(TradeDirection.Short, false)]
    [InlineData(TradeDirection.Long, true)]
    [InlineData(TradeDirection.Short, true)]
    public void TightNominalTwoRTradeFailsWithFeesButPassesWithoutFees(TradeDirection direction, bool zeroFees)
    {
        var manager = new RiskManager(Options.Create(new RiskOptions { FeeRate = zeroFees ? 0m : 0.0005m }));
        var signal = new StrategySignal("BTC-USDT", direction, DateTimeOffset.UtcNow, 100m, 0.2m, "tight stop",
            SuggestedStopLoss: direction == TradeDirection.Long ? 99.8m : 100.2m,
            SuggestedTakeProfit: direction == TradeDirection.Long ? 100.4m : 99.6m);

        var protection = manager.CreateProtection(signal, Contract, 1.5m, 2m, out var reason);

        Assert.Equal(zeroFees, protection is not null);
        if (zeroFees)
        {
            Assert.Null(reason);
            Assert.Equal(0.2m, protection!.Value.LossPerUnitAtStop);
            Assert.Equal(0.4m, protection.Value.NetRewardPerUnit);
        }
        else
        {
            Assert.Equal("reward/risk after fees is below minimum", reason);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NetRewardRiskAcceptsExactMinimumAndRejectsHigherRequirement(bool atBoundary)
    {
        var manager = new RiskManager(Options.Create(new RiskOptions
        {
            FeeRate = 0m, MinimumNetRewardRiskRatio = atBoundary ? 2m : 2.0001m
        }));
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 1m, "boundary");

        var protection = manager.CreateProtection(signal, Contract, 1.5m, 2m, out var reason);

        Assert.Equal(atBoundary, protection is not null);
        Assert.Equal(atBoundary ? null : "reward/risk after fees is below minimum", reason);
    }

    [Fact]
    public void RoundedPricesMustPassNetRewardRiskEvenWhenUnroundedPricesWouldPass()
    {
        var manager = new RiskManager(Options.Create(new RiskOptions()));
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 1m,
            "rounding boundary", SuggestedStopLoss: 99.004m, SuggestedTakeProfit: 101.749m);

        var unrounded = manager.CreateProtection(signal, null, 1.5m, 2m, out _);
        var rounded = manager.CreateProtection(signal, Contract, 1.5m, 2m, out var reason);

        Assert.NotNull(unrounded);
        Assert.Null(rounded);
        Assert.Equal("reward/risk after fees is below minimum", reason);
    }

    [Fact]
    public void StructuralStopRequiresContractTickMetadata()
    {
        var manager = new RiskManager(Options.Create(new RiskOptions()));
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 1m,
            "retest", InvalidationPrice: 99m);

        var protection = manager.CreateProtection(signal, null, 1.5m, 2m, out var reason);

        Assert.Null(protection);
        Assert.Equal("invalid retest structure", reason);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void WrongSideAiStopIsRejectedBeforeApplyingStructuralBoundary(TradeDirection direction)
    {
        var manager = new RiskManager(Options.Create(new RiskOptions()));
        var signal = new StrategySignal("BTC-USDT", direction, DateTimeOffset.UtcNow, 100m, 1m, "invalid AI",
            SuggestedStopLoss: direction == TradeDirection.Long ? 101m : 99m, Source: "openai",
            InvalidationPrice: direction == TradeDirection.Long ? 99m : 101m);

        Assert.Null(manager.CreateProtection(signal, Contract, 1.5m, 2m, out var reason));
        Assert.Equal("suggested stop is on the wrong side of entry", reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaperPlanningUsesTheSharedProtectionGate(bool tightStop)
    {
        var manager = new RiskManager(Options.Create(new RiskOptions { TargetProfitUsdt = 0m }));
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m,
            tightStop ? 0.1m : 1m, "shared gate", InvalidationPrice: 99.99m);
        var protection = manager.CreateProtection(signal, Contract, 1.5m, 2m, out _);

        var plan = manager.CreatePlan(signal, 10_000m, null, 0m, 0m, Contract);

        Assert.Equal(!tightStop, plan is not null);
        if (plan is not null)
        {
            Assert.NotNull(protection);
            Assert.Equal(protection.Value.StopLoss, plan.StopLoss);
            Assert.Equal(protection.Value.TakeProfit, plan.TakeProfit);
        }
    }
}
