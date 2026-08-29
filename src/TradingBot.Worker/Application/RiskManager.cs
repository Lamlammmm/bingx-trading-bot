using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class RiskManager(IOptions<RiskOptions> options) : IRiskManager
{
    private readonly RiskOptions _options = options.Value;

    public TradePlan? CreatePlan(StrategySignal signal, decimal accountBalance, PaperPosition? openPosition,
        decimal realizedPnlToday, decimal aggregateOpenRisk)
    {
        if (openPosition is not null || accountBalance <= 0m) return null;
        var maxDailyLoss = accountBalance * (_options.MaxDailyLossPercent / 100m);
        if (realizedPnlToday <= -maxDailyLoss) return null;

        var defaultStopDistance = signal.Atr * _options.StopAtrMultiplier;
        if (defaultStopDistance <= 0m) return null;

        var stopLoss = signal.SuggestedStopLoss ?? (signal.Direction == TradeDirection.Long
            ? signal.EntryPrice - defaultStopDistance
            : signal.EntryPrice + defaultStopDistance);
        var takeProfit = signal.SuggestedTakeProfit ?? (signal.Direction == TradeDirection.Long
            ? signal.EntryPrice + (defaultStopDistance * _options.TakeProfitRiskMultiple)
            : signal.EntryPrice - (defaultStopDistance * _options.TakeProfitRiskMultiple));
        var directionIsValid = signal.Direction == TradeDirection.Long
            ? stopLoss < signal.EntryPrice && takeProfit > signal.EntryPrice
            : stopLoss > signal.EntryPrice && takeProfit < signal.EntryPrice;
        if (!directionIsValid) return null;

        var stopDistance = Math.Abs(signal.EntryPrice - stopLoss);
        var rewardDistance = Math.Abs(takeProfit - signal.EntryPrice);
        if (stopDistance > signal.Atr * _options.MaximumStopAtrMultiplier || rewardDistance < stopDistance * _options.MinimumTakeProfitRiskMultiple)
            return null;

        var riskAmount = accountBalance * (_options.RiskPerTradePercent / 100m);
        var maxAggregateRisk = accountBalance * (_options.MaxAggregateOpenRiskPercent / 100m);
        if (aggregateOpenRisk + riskAmount > maxAggregateRisk) return null;

        var quantity = Math.Min(riskAmount / stopDistance, (accountBalance * _options.MaxLeverage) / signal.EntryPrice);
        if (quantity < _options.MinimumQuantity) return null;

        var entryFee = signal.EntryPrice * quantity * _options.FeeRate;
        return new TradePlan(signal.Symbol, signal.Direction, quantity, signal.EntryPrice, stopLoss, takeProfit,
            riskAmount, entryFee, signal.Reason);
    }
}
