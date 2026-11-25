using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Tests;

public sealed class PositionManagerTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T07:55:00Z");
    private static readonly ContractInfo Contract = new("BTC-USDT", 2, 2, 0.01m, 1m, true);

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void PartialDoesNotActivateNewStopInsideTheSameBar(TradeDirection direction)
    {
        var risk = new RiskOptions { FeeRate = 0m, UseDynamicPositionManagement = true, BreakEvenTriggerR = 1m,
            MinimumTrailingStepAtr = 0m, SecondTakeProfitRMultiple = 2m };
        var manager = new PositionManager(risk, new PaperTradingOptions { SlippageBasisPoints = 0m });
        var position = Position(direction);
        var bar = Bar(direction, 100m, 112m, 99m, 111m);
        var update = manager.Evaluate(position, bar, 2m, Contract);

        Assert.False(update.IsClosed);
        Assert.Equal(5m, update.Position.Quantity);
        Assert.Equal(100m, update.Position.StopLoss);
        Assert.Equal(50m, update.CashDelta);
        var next = manager.Evaluate(update.Position, bar with { OpenTime = bar.CloseTime, CloseTime = bar.CloseTime.AddMinutes(5) }, 2m, Contract);
        Assert.True(next.IsClosed);
        Assert.Equal(50m, next.TradeNetPnl);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void StopWinsAmbiguousBarAndGapUsesWorseOpen(TradeDirection direction)
    {
        var manager = new PositionManager(new RiskOptions { FeeRate = 0m }, new PaperTradingOptions { SlippageBasisPoints = 0m });
        var update = manager.Evaluate(Position(direction), Bar(direction, 85m, 140m, 84m, 120m), 2m, Contract);

        Assert.True(update.IsClosed);
        Assert.Equal("SL", update.ExitReason);
        Assert.Equal(direction == TradeDirection.Long ? 85m : 115m, update.ExitPrice);
        Assert.Equal(-150m, update.TradeNetPnl);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void TwoPartialTargetsAndFinalRemainderConserveQuantityAndFees(TradeDirection direction)
    {
        var risk = new RiskOptions { FeeRate = 0.001m, UseDynamicPositionManagement = true,
            PartialTakeProfitPercent = 50m, SecondTakeProfitPercent = 25m };
        var manager = new PositionManager(risk, new PaperTradingOptions { SlippageBasisPoints = 0m });
        var position = Position(direction) with { EntryFee = 1m };
        var update = manager.Evaluate(position, Bar(direction, 100m, 131m, 99m, 130m), 2m, Contract);
        // 5 * 1R + 2.5 * 2R + 2.5 * 3R = 175 gross, then both entry and all exit fees.
        var expectedExitNotional = direction == TradeDirection.Long ? 1175m : 825m;
        Assert.True(update.IsClosed);
        Assert.Equal(175m - expectedExitNotional * 0.001m - 1m, update.TradeNetPnl);
        Assert.Equal(expectedExitNotional / 10m, update.Metrics!.AverageExitPrice);
        Assert.Equal(1m + expectedExitNotional * 0.001m, update.Metrics.TotalFees);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void BreakEvenIncludesCostsAndTrailingNeverLoosens(TradeDirection direction)
    {
        var risk = new RiskOptions { FeeRate = 0.001m, UsePartialTakeProfit = false,
            UseDynamicPositionManagement = true, BreakEvenTriggerR = 1m, TrailingStartR = 2m,
            MinimumTrailingStepAtr = 0m, StopAdjustmentCooldownBars = 0 };
        var manager = new PositionManager(risk, new PaperTradingOptions { SlippageBasisPoints = 5m, SpreadBasisPoints = 4m });
        var firstBar = Bar(direction, 110m, 112m, 109m, 111m);
        var first = manager.Evaluate(Position(direction), firstBar, 2m, Contract);
        Assert.False(first.IsClosed);
        Assert.True(direction == TradeDirection.Long ? first.Position.StopLoss > 100.2m : first.Position.StopLoss < 99.8m);
        var later = firstBar with { OpenTime = firstBar.CloseTime, CloseTime = firstBar.CloseTime.AddMinutes(5) };
        var second = manager.Evaluate(first.Position, later, 20m, Contract);
        Assert.Equal(first.Position.StopLoss, second.Position.StopLoss);
        var duplicate = manager.Evaluate(second.Position, later, 2m, Contract);
        Assert.Equal(0m, duplicate.CashDelta);
        Assert.Equal(second.Position, duplicate.Position);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void FundingUsesUtcSettlementAndQuantityHeldBeforePartial(TradeDirection direction)
    {
        var manager = new PositionManager(new RiskOptions { FeeRate = 0m },
            new PaperTradingOptions { SlippageBasisPoints = 0m, FundingRatePerEightHours = 0.001m });
        var first = manager.Evaluate(Position(direction), Bar(direction, 100m, 111m, 99m, 110m), 2m, Contract);
        var sign = direction == TradeDirection.Long ? 1m : -1m;
        Assert.Equal(sign * 1m, first.Position.Metrics!.FundingPaid);
        Assert.Equal(50m - sign * 1m, first.CashDelta);
        var last = Bar(direction, 100m, 101m, 99m, 100m) with { OpenTime = Start.AddMinutes(5), CloseTime = Start.AddMinutes(10) };
        var closed = manager.Evaluate(first.Position, last, 2m, Contract);
        Assert.True(closed.IsClosed);
        Assert.Equal(50m - sign * 1m, closed.TradeNetPnl);
        Assert.Equal(sign * 1m, closed.Metrics!.FundingPaid);
    }

    [Theory]
    [InlineData(TradeDirection.Long)]
    [InlineData(TradeDirection.Short)]
    public void SettledReplayFundingOverridesFixedFundingAndIsNotChargedOnDuplicateBars(TradeDirection direction)
    {
        var manager = new PositionManager(new RiskOptions { FeeRate = 0m },
            new PaperTradingOptions { SlippageBasisPoints = 0m, FundingRatePerEightHours = 0.001m });
        var settled = direction == TradeDirection.Long ? 2m : -2m;
        var bar = Bar(direction, 100m, 111m, 99m, 110m);
        var update = manager.Evaluate(Position(direction), bar, 2m, Contract, settled);

        Assert.Equal(50m - settled, update.CashDelta);
        Assert.Equal(settled, update.Position.Metrics!.FundingPaid);
        var duplicate = manager.Evaluate(update.Position, bar, 2m, Contract, settled);
        Assert.Equal(0m, duplicate.CashDelta);
        Assert.Equal(settled, duplicate.Position.Metrics!.FundingPaid);
    }

    [Fact]
    public void PartialRespectsMinimumNotionalAndQuantityPrecision()
    {
        var manager = new PositionManager(new RiskOptions { FeeRate = 0m }, new PaperTradingOptions { SlippageBasisPoints = 0m });
        var position = Position(TradeDirection.Long) with { Quantity = 0.01m, InitialQuantity = 0.01m };
        var update = manager.Evaluate(position, Bar(TradeDirection.Long, 100m, 111m, 99m, 110m), 2m, Contract);
        Assert.Equal(0.01m, update.Position.Quantity);
        Assert.False(update.Position.PartialTaken);
    }

    private static PaperPosition Position(TradeDirection direction) => new("BTC-USDT", direction, 10m, 100m,
        direction == TradeDirection.Long ? 90m : 110m, direction == TradeDirection.Long ? 130m : 70m,
        100m, 0m, Start, InitialStopLoss: direction == TradeDirection.Long ? 90m : 110m, InitialQuantity: 10m);

    private static Candle Bar(TradeDirection direction, decimal open, decimal high, decimal low, decimal close) => direction == TradeDirection.Long
        ? new(Start, Start.AddMinutes(5), open, high, low, close, 100m, true)
        : new(Start, Start.AddMinutes(5), 200m - open, 200m - low, 200m - high, 200m - close, 100m, true);
}
