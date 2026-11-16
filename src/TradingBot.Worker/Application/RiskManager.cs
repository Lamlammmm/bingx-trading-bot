using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class RiskManager(IOptions<RiskOptions> options) : IRiskManager
{
    private readonly RiskOptions _options = options.Value;
    public string? LastRejectionReason { get; private set; }

    public TradePlan? CreatePlan(StrategySignal signal, decimal accountBalance, PaperPosition? openPosition,
        decimal realizedPnlToday, decimal aggregateOpenRisk, ContractInfo? contract)
    {
        LastRejectionReason = null;
        if (openPosition is not null || accountBalance <= 0m || signal.EntryPrice <= 0m || signal.Atr <= 0m ||
            _options.RiskPerTradePercent <= 0m || _options.RiskPerTradePercent >= 100m || _options.MaxDailyLossPercent <= 0m ||
            _options.MaxLeverage < 1m || _options.MaxAggregateOpenRiskPercent <= 0m || aggregateOpenRisk < 0m ||
            (contract is not null && (!contract.IsActive || contract.Symbol != signal.Symbol ||
                contract.QuantityPrecision < 0m || contract.QuantityPrecision > 28m || contract.QuantityPrecision != decimal.Truncate(contract.QuantityPrecision) ||
                contract.PricePrecision < 0m || contract.PricePrecision > 28m || contract.PricePrecision != decimal.Truncate(contract.PricePrecision)))) return Reject("invalid signal, capital, risk options or contract metadata");
        // The caller can pass a fixed start-of-day budget via realized PnL and balance.
        var dayStartBalance = accountBalance - realizedPnlToday;
        var maxDailyLoss = dayStartBalance * (_options.MaxDailyLossPercent / 100m);
        if (realizedPnlToday <= -maxDailyLoss) return Reject("daily loss budget exhausted");

        var pricePrecision = contract is null ? (int?)null : (int)contract.PricePrecision;
        var quantityPrecision = contract is null ? (int?)null : (int)contract.QuantityPrecision;
        var entryPrice = RoundDown(signal.EntryPrice, pricePrecision);

        decimal stopLoss, takeProfit;
        if (_options.UseFixedPercentSlTp)
        {
            // SL/TP hardcoded as % PnL-on-margin at MaxLeverage (isolated-margin style),
            // converted to a price distance: priceMove% = marginPnL% / leverage. Overrides ATR/AI levels for testing.
            if (_options.MaxLeverage <= 0m) return Reject("fixed mode needs valid leverage");
            var stopDistancePct = entryPrice * (_options.StopLossMarginPercent / 100m) / _options.MaxLeverage;
            var takeProfitDistancePct = entryPrice * (_options.TakeProfitMarginPercent / 100m) / _options.MaxLeverage;
            if (stopDistancePct <= 0m || takeProfitDistancePct <= 0m) return Reject("invalid fixed protection distances");

            stopLoss = signal.Direction == TradeDirection.Long
                ? entryPrice - stopDistancePct
                : entryPrice + stopDistancePct;
            takeProfit = signal.Direction == TradeDirection.Long
                ? entryPrice + takeProfitDistancePct
                : entryPrice - takeProfitDistancePct;
        }
        else
        {
            var protection = CreateProtection(signal with { EntryPrice = entryPrice }, contract,
                _options.StopAtrMultiplier, _options.TakeProfitRiskMultiple, out var rejectionReason);
            LastRejectionReason = rejectionReason;
            if (protection is null) return Reject(LastRejectionReason ?? "protection rejected");
            stopLoss = protection.Value.StopLoss;
            takeProfit = protection.Value.TakeProfit;
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
        if (!directionIsValid) return Reject("protection on wrong side");

        var stopDistance = Math.Abs(entryPrice - stopLoss);
        var rewardDistance = Math.Abs(takeProfit - entryPrice);
        if (stopDistance > signal.Atr * _options.MaximumStopAtrMultiplier || rewardDistance < stopDistance * _options.MinimumTakeProfitRiskMultiple)
            return Reject("stop distance or gross reward invalid");

        var exitCostRate = _options.ExecutionCostBasisPoints / 10_000m;
        if (exitCostRate < 0m || exitCostRate >= 1m || _options.FeeRate < 0m || _options.FeeRate >= 1m) return Reject("invalid cost inputs");
        var stopFill = stopLoss * (1m - (signal.Direction == TradeDirection.Long ? 1m : -1m) * exitCostRate);
        var takeFill = takeProfit * (1m - (signal.Direction == TradeDirection.Long ? 1m : -1m) * exitCostRate);
        var lossPerUnit = Math.Abs(entryPrice - stopFill) + (entryPrice + stopFill) * _options.FeeRate;
        var netRewardPerUnit = (takeFill - entryPrice) * (signal.Direction == TradeDirection.Long ? 1m : -1m)
            - (entryPrice + takeFill) * _options.FeeRate;
        if (_options.UseDynamicPositionManagement && _options.UsePartialTakeProfit)
        {
            var sign = signal.Direction == TradeDirection.Long ? 1m : -1m;
            var firstWeight = _options.PartialTakeProfitPercent / 100m;
            var secondWeight = _options.SecondTakeProfitPercent / 100m;
            if (firstWeight <= 0m || secondWeight < 0m || firstWeight + secondWeight >= 1m ||
                _options.PartialTakeProfitRMultiple <= 0m || _options.SecondTakeProfitRMultiple <= _options.PartialTakeProfitRMultiple) return Reject("invalid partial allocation");
            var firstTarget = entryPrice + sign * stopDistance * _options.PartialTakeProfitRMultiple;
            var secondTarget = entryPrice + sign * stopDistance * _options.SecondTakeProfitRMultiple;
            // If a partial target is beyond the final target, PositionManager will not take that partial.
            if (sign * (takeProfit - firstTarget) <= 0m) firstWeight = 0m;
            if (sign * (takeProfit - secondTarget) <= 0m) secondWeight = 0m;
            var averageExit = (firstTarget * firstWeight + secondTarget * secondWeight + takeProfit * (1m - firstWeight - secondWeight))
                * (1m - sign * exitCostRate);
            netRewardPerUnit = sign * (averageExit - entryPrice) - (entryPrice + averageExit) * _options.FeeRate;
        }
        if (lossPerUnit <= 0m || netRewardPerUnit <= 0m || netRewardPerUnit < lossPerUnit * _options.MinimumNetRewardRiskRatio) return Reject("fee-inclusive weighted reward/risk below minimum");
        var riskBudget = accountBalance * (_options.RiskPerTradePercent / 100m);
        var maxAggregateRisk = accountBalance * (_options.MaxAggregateOpenRiskPercent / 100m);

        var quantity = Math.Min(riskBudget / lossPerUnit,
            accountBalance / (entryPrice / _options.MaxLeverage + entryPrice * _options.FeeRate));
        if (_options.TargetProfitUsdt > 0m)
            quantity = Math.Min(quantity, _options.TargetProfitUsdt / rewardDistance);
        quantity = RoundDown(quantity, quantityPrecision);
        var minimumQuantity = Math.Max(_options.MinimumQuantity, contract?.MinimumQuantity ?? 0m);
        if (quantity < minimumQuantity) return Reject("order below minimum quantity");
        if (contract is not null && quantity * entryPrice < contract.MinimumOrderValue) return Reject("order below minimum notional");

        var riskAmount = quantity * lossPerUnit;
        if (aggregateOpenRisk + riskAmount > maxAggregateRisk) return Reject("aggregate open risk cap reached");

        var entryFee = entryPrice * quantity * _options.FeeRate;
        return new TradePlan(signal.Symbol, signal.Direction, quantity, entryPrice, stopLoss, takeProfit,
            riskAmount, entryFee, signal.Reason, netRewardPerUnit / lossPerUnit);
    }

    // Shared by paper planning and live sizing so a technical setup has the same SL/TP rules.
    public (decimal StopLoss, decimal TakeProfit, decimal LossPerUnitAtStop, decimal NetRewardPerUnit)? CreateProtection(
        StrategySignal signal, ContractInfo? contract, decimal stopAtrMultiplier, decimal takeProfitRiskMultiple,
        out string? rejectionReason)
    {
        rejectionReason = null;
        if (signal.EntryPrice <= 0m || signal.Atr <= 0m || stopAtrMultiplier <= 0m || takeProfitRiskMultiple <= 0m ||
            _options.FeeRate < 0m || _options.FeeRate >= 1m || _options.MinimumNetRewardRiskRatio < 0m ||
            _options.StructureBufferAtr < 0m || _options.ExecutionCostBasisPoints < 0m || _options.ExecutionCostBasisPoints >= 10_000m ||
            (contract is not null && (contract.PricePrecision < 0m || contract.PricePrecision > 28m ||
                contract.PricePrecision != decimal.Truncate(contract.PricePrecision))))
        {
            rejectionReason = "invalid protection inputs";
            return null;
        }
        var precision = contract is null ? (int?)null : (int)contract.PricePrecision;
        var sign = signal.Direction == TradeDirection.Long ? 1m : -1m;
        var stop = signal.SuggestedStopLoss ?? signal.EntryPrice - sign * signal.Atr * stopAtrMultiplier;
        if (stop <= 0m || sign * (signal.EntryPrice - stop) <= 0m)
        {
            rejectionReason = "suggested stop is on the wrong side of entry";
            return null;
        }
        if (signal.InvalidationPrice is { } invalidation)
        {
            if (contract is null || invalidation <= 0m || sign * (signal.EntryPrice - invalidation) <= 0m)
            {
                rejectionReason = "invalid retest structure";
                return null;
            }
            var tick = 1m / (decimal)Math.Pow(10, precision!.Value);
            var buffer = signal.Atr * _options.StructureBufferAtr + signal.EntryPrice * _options.ExecutionCostBasisPoints / 10_000m;
            var structuralStop = sign > 0m ? RoundDown(invalidation - buffer, precision) - tick : RoundUp(invalidation + buffer, precision) + tick;
            stop = sign > 0m ? Math.Min(stop, structuralStop) : Math.Max(stop, structuralStop);
        }
        stop = sign > 0m ? RoundDown(stop, precision) : RoundUp(stop, precision);
        var distance = sign * (signal.EntryPrice - stop);
        if (stop <= 0m || distance <= 0m || distance > signal.Atr * _options.MaximumStopAtrMultiplier)
        {
            rejectionReason = "stop is invalid or exceeds maximum ATR distance";
            return null;
        }
        var take = signal.SuggestedTakeProfit ?? signal.EntryPrice + sign * distance * takeProfitRiskMultiple;
        take = sign > 0m ? RoundUp(take, precision) : RoundDown(take, precision);
        var reward = sign * (take - signal.EntryPrice);
        var costRate = _options.ExecutionCostBasisPoints / 10_000m;
        var stopFill = stop * (1m - sign * costRate);
        var takeFill = take * (1m - sign * costRate);
        var lossPerUnit = sign * (signal.EntryPrice - stopFill) + (signal.EntryPrice + stopFill) * _options.FeeRate;
        var netReward = sign * (takeFill - signal.EntryPrice) - (signal.EntryPrice + takeFill) * _options.FeeRate;
        if (take <= 0m || reward <= 0m || reward < distance * _options.MinimumTakeProfitRiskMultiple ||
            netReward <= 0m || netReward < lossPerUnit * _options.MinimumNetRewardRiskRatio)
        {
            rejectionReason = "reward/risk after fees is below minimum";
            return null;
        }
        return (stop, take, lossPerUnit, netReward);
    }

    private TradePlan? Reject(string reason)
    {
        LastRejectionReason = reason;
        return null;
    }

    private static decimal RoundDown(decimal value, int? precision) => precision is null
        ? value
        : decimal.Round(value, precision.Value, MidpointRounding.ToZero);

    private static decimal RoundUp(decimal value, int? precision) => precision is null
        ? value
        : decimal.Round(value, precision.Value, MidpointRounding.ToPositiveInfinity);
}
