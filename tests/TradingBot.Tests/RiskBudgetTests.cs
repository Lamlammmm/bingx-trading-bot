using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class RiskBudgetTests
{
    private static readonly ContractInfo Contract = new("BTC-USDT", 4, 2, 0.0001m, 1m, true);

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void StopLossWithExitCostsFitsRiskBudgetForBothDirections(TradeDirection direction)
    {
        var options = new RiskOptions { RiskPerTradePercent = 1m, TargetProfitUsdt = 0m,
            MaxAggregateOpenRiskPercent = 100m, ExecutionCostBasisPoints = 10m };
        var plan = new RiskManager(Options.Create(options)).CreatePlan(new("BTC-USDT", direction,
            DateTimeOffset.UtcNow, 100m, 2m, "test"), 1000m, null, 0m, 0m, Contract);
        Assert.NotNull(plan);
        var sign = direction == TradeDirection.Long ? 1m : -1m;
        var stopFill = plan.StopLoss * (1m - sign * 0.001m);
        var loss = plan.Quantity * (sign * (plan.EntryPrice - stopFill) + (plan.EntryPrice + stopFill) * options.FeeRate);
        Assert.InRange(loss, 9.99m, 10m);
        Assert.Equal(loss, plan.RiskAmount);
    }

    [Fact]
    public void DailyLossLimitUsesStartOfDayCapitalRatherThanShrinkingBalance()
    {
        var manager = new RiskManager(Options.Create(new RiskOptions { MaxDailyLossPercent = 2m, TargetProfitUsdt = 0m }));
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 2m, "test");
        Assert.NotNull(manager.CreatePlan(signal, 981m, null, -19m, 0m, Contract));
        Assert.Null(manager.CreatePlan(signal, 980m, null, -20m, 0m, Contract));
    }

    [Fact]
    public void WeightedPartialPayoffMustPassRewardGate()
    {
        var options = new RiskOptions { FeeRate = 0m, UseDynamicPositionManagement = true,
            TakeProfitRiskMultiple = 3m, TargetProfitUsdt = 0m, MinimumNetRewardRiskRatio = 2m };
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 2m, "test");
        // 50% at 1R, 25% at 2R, 25% at 3R = 1.75R, below a 2R requirement.
        Assert.Null(new RiskManager(Options.Create(options)).CreatePlan(signal, 1000m, null, 0m, 0m, Contract));
        options.MinimumNetRewardRiskRatio = 1.75m;
        Assert.NotNull(new RiskManager(Options.Create(options)).CreatePlan(signal, 1000m, null, 0m, 0m, Contract));
    }

    [Fact]
    public void MalformedOrInactiveContractIsRejected()
    {
        var manager = new RiskManager(Options.Create(new RiskOptions()));
        var signal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow, 100m, 2m, "test");
        foreach (var contract in new[] { Contract with { PricePrecision = 29m }, Contract with { QuantityPrecision = 1.5m },
            Contract with { IsActive = false }, Contract with { Symbol = "ETH-USDT" } })
            Assert.Null(manager.CreatePlan(signal, 1000m, null, 0m, 0m, contract));
    }
}
