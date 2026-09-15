using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class RiskManager(IOptions<RiskOptions> options) : IRiskManager
{
    private readonly RiskOptions _options = options.Value;

    public TradePlan? CreatePlan(StrategySignal signal, decimal accountBalance, PaperPosition? openPosition,
        decimal realizedPnlToday, decimal aggregateOpenRisk, ContractInfo? contract)
    {
        if (openPosition is not null || accountBalance <= 0m) return null;
        var maxDailyLoss = accountBalance * (_options.MaxDailyLossPercent / 100m);
        if (realizedPnlToday <= -maxDailyLoss) return null;

        var pricePrecision = contract is null ? (int?)null : (int)contract.PricePrecision;
        var quantityPrecision = contract is null ? (int?)null : (int)contract.QuantityPrecision;
        var entryPrice = RoundDown(signal.EntryPrice, pricePrecision);

        decimal stopLoss, takeProfit;
        if (_options.UseFixedPercentSlTp)
        {
            // SL/TP hardcoded as % PnL-on-margin at MaxLeverage (isolated-margin style),
            // converted to a price distance: priceMove% = marginPnL% / leverage. Overrides ATR/AI levels for testing.
            if (_options.MaxLeverage <= 0m) return null;
            var stopDistancePct = entryPrice * (_options.StopLossMarginPercent / 100m) / _options.MaxLeverage;
            var takeProfitDistancePct = entryPrice * (_options.TakeProfitMarginPercent / 100m) / _options.MaxLeverage;
            if (stopDistancePct <= 0m || takeProfitDistancePct <= 0m) return null;

            stopLoss = signal.Direction == TradeDirection.Long
                ? entryPrice - stopDistancePct
                : entryPrice + stopDistancePct;
            takeProfit = signal.Direction == TradeDirection.Long
                ? entryPrice + takeProfitDistancePct
                : entryPrice - takeProfitDistancePct;
        }
        else
        {
            var defaultStopDistance = signal.Atr * _options.StopAtrMultiplier;
            if (defaultStopDistance <= 0m) return null;

            stopLoss = signal.SuggestedStopLoss ?? (signal.Direction == TradeDirection.Long
                ? entryPrice - defaultStopDistance
                : entryPrice + defaultStopDistance);
            takeProfit = signal.SuggestedTakeProfit ?? (signal.Direction == TradeDirection.Long
                ? entryPrice + (defaultStopDistance * _options.TakeProfitRiskMultiple)
                : entryPrice - (defaultStopDistance * _options.TakeProfitRiskMultiple));
        }
        stopLoss = signal.Direction == TradeDirection.Long
            ? RoundDown(stopLoss, pricePrecision)
            : RoundUp(stopLoss, pricePrecision);
        takeProfit = signal.Direction == TradeDirection.Long
            ? RoundUp(takeProfit, pricePrecision)
            : RoundDown(takeProfit, pricePrecision);
        var directionIsValid = signal.Direction == TradeDirection.Long
            ? stopLoss < entryPrice && takeProfit > entryPrice
            : stopLoss > entryPrice && takeProfit < entryPrice;
        if (!directionIsValid) return null;

        var stopDistance = Math.Abs(entryPrice - stopLoss);
        var rewardDistance = Math.Abs(takeProfit - entryPrice);
        if (stopDistance > signal.Atr * _options.MaximumStopAtrMultiplier || rewardDistance < stopDistance * _options.MinimumTakeProfitRiskMultiple)
            return null;

        var riskBudget = accountBalance * (_options.RiskPerTradePercent / 100m);
        var maxAggregateRisk = accountBalance * (_options.MaxAggregateOpenRiskPercent / 100m);

        var quantity = Math.Min(riskBudget / stopDistance, (accountBalance * _options.MaxLeverage) / entryPrice);
        if (_options.TargetProfitUsdt > 0m)
            quantity = Math.Min(quantity, _options.TargetProfitUsdt / rewardDistance);
        quantity = RoundDown(quantity, quantityPrecision);
        var minimumQuantity = Math.Max(_options.MinimumQuantity, contract?.MinimumQuantity ?? 0m);
        if (quantity < minimumQuantity) return null;
        if (contract is not null && quantity * entryPrice < contract.MinimumOrderValue) return null;

        var riskAmount = quantity * stopDistance;
        if (aggregateOpenRisk + riskAmount > maxAggregateRisk) return null;

        var entryFee = entryPrice * quantity * _options.FeeRate;
        return new TradePlan(signal.Symbol, signal.Direction, quantity, entryPrice, stopLoss, takeProfit,
            riskAmount, entryFee, signal.Reason);
    }

    private static decimal RoundDown(decimal value, int? precision) => precision is null
        ? value
        : decimal.Round(value, precision.Value, MidpointRounding.ToZero);

    private static decimal RoundUp(decimal value, int? precision) => precision is null
        ? value
        : decimal.Round(value, precision.Value, MidpointRounding.ToPositiveInfinity);
}
