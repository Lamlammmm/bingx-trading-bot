using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed record PositionTransition(PaperPosition Position, decimal CashDelta, bool IsClosed,
    decimal ExitPrice = 0m, string ExitReason = "", decimal TradeNetPnl = 0m, TradeMetrics? Metrics = null);

public sealed record PartialExitTarget(int Stage, decimal Quantity, decimal Price);

// Pure bar-based position management, shared by paper/replay. Changes at bar close affect the NEXT bar.
public sealed class PositionManager(RiskOptions risk, PaperTradingOptions costs)
{
    public IReadOnlyList<PartialExitTarget> PartialTargets(PaperPosition position, decimal reachedPrice, ContractInfo? contract)
    {
        if (!risk.UsePartialTakeProfit) return [];
        var sign = position.Direction == TradeDirection.Long ? 1m : -1m;
        var initial = position.InitialQuantity > 0m ? position.InitialQuantity : position.Quantity;
        var distance = Math.Abs(position.EntryPrice - (position.InitialStopLoss > 0m ? position.InitialStopLoss : position.StopLoss));
        var stage = Math.Max(position.Metrics?.TakeProfitStage ?? 0, position.PartialTaken ? 1 : 0);
        var levels = risk.UseDynamicPositionManagement
            ? new[] { (risk.PartialTakeProfitRMultiple, risk.PartialTakeProfitPercent), (risk.SecondTakeProfitRMultiple, risk.SecondTakeProfitPercent) }
            : new[] { (risk.PartialTakeProfitRMultiple, risk.PartialTakeProfitPercent) };
        var results = new List<PartialExitTarget>(); var remaining = position.Quantity;
        for (var i = stage; i < levels.Length; i++)
        {
            var (r, percent) = levels[i];
            var target = position.EntryPrice + sign * distance * r;
            var part = initial * percent / 100m;
            if (contract is not null)
            {
                target = decimal.Round(target, (int)contract.PricePrecision, sign > 0m ? MidpointRounding.ToPositiveInfinity : MidpointRounding.ToZero);
                part = decimal.Round(part, (int)contract.QuantityPrecision, MidpointRounding.ToZero);
            }
            var minimum = Math.Max(risk.MinimumQuantity, contract?.MinimumQuantity ?? 0m);
            if (distance <= 0m || r <= 0m || percent <= 0m || percent >= 100m || sign * (position.TakeProfit - target) <= 0m ||
                sign * (reachedPrice - target) < 0m || part < minimum || part <= 0m || remaining - part < minimum ||
                (contract is not null && (part * target < contract.MinimumOrderValue || (remaining - part) * target < contract.MinimumOrderValue))) break;
            results.Add(new(i + 1, part, target)); remaining -= part;
        }
        return results;
    }
    public PositionTransition Evaluate(PaperPosition position, Candle candle, decimal atr, ContractInfo? contract)
    {
        var metrics = position.Metrics ?? new PositionMetrics();
        if (!candle.IsClosed || candle.OpenTime < position.OpenedAt ||
            (metrics.LastEvaluatedAt is { } last && candle.CloseTime <= last)) return new(position, 0m, false);
        var sign = position.Direction == TradeDirection.Long ? 1m : -1m;
        var initialQuantity = position.InitialQuantity > 0m ? position.InitialQuantity : position.Quantity;
        var initialStop = position.InitialStopLoss > 0m ? position.InitialStopLoss : position.StopLoss;
        var initialDistance = Math.Abs(position.EntryPrice - initialStop);
        var rate = (costs.SlippageBasisPoints + costs.SpreadBasisPoints / 2m) / 10_000m;
        if (rate < 0m || rate >= 1m || risk.FeeRate < 0m || risk.FeeRate >= 1m) throw new InvalidOperationException("Invalid execution cost settings.");
        var quantity = position.Quantity;
        var cashDelta = 0m;
        var partialPnl = position.RealizedPartialPnl;
        var stage = Math.Max(metrics.TakeProfitStage, position.PartialTaken ? 1 : 0);
        var lastFundingTime = metrics.LastEvaluatedAt ?? position.OpenedAt;
        // Scenario: fixed rate at UTC 00/08/16 boundaries, charged to quantity held before this bar's exits.
        var fundingPeriods = Math.Max(0L, candle.CloseTime.ToUnixTimeMilliseconds() / 28_800_000L - lastFundingTime.ToUnixTimeMilliseconds() / 28_800_000L);
        var funding = position.EntryPrice * quantity * costs.FundingRatePerEightHours * fundingPeriods * sign;
        cashDelta -= funding;
        partialPnl -= funding;
        metrics = metrics with { FundingPaid = metrics.FundingPaid + funding, LastEvaluatedAt = candle.CloseTime };
        var stopHit = sign > 0m ? candle.Low <= position.StopLoss : candle.High >= position.StopLoss;
        var takeHit = sign > 0m ? candle.High >= position.TakeProfit : candle.Low <= position.TakeProfit;
        // Excursions on an exit bar cannot be ordered from OHLC. Bound them at the first modeled exit.
        var favorable = sign > 0m ? candle.High - position.EntryPrice : position.EntryPrice - candle.Low;
        var adverse = sign > 0m ? position.EntryPrice - candle.Low : candle.High - position.EntryPrice;
        if (stopHit) favorable = Math.Max(0m, sign * (candle.Open - position.EntryPrice));
        else if (takeHit) favorable = sign * (position.TakeProfit - position.EntryPrice);
        metrics = metrics with { Mfe = Math.Max(metrics.Mfe, Math.Max(0m, favorable)), Mae = Math.Max(metrics.Mae, Math.Max(0m, adverse)) };
        decimal exitPrice = 0m;
        var exitReason = "";
        if (stopHit)
        {
            // Stops gap through their trigger; fill from open when it is worse.
            exitPrice = sign > 0m ? Math.Min(position.StopLoss, candle.Open) : Math.Max(position.StopLoss, candle.Open);
            exitReason = position.StopLoss == initialStop ? "SL" : "TRAILING_SL";
        }
        else
        {
            foreach (var target in PartialTargets(position, sign > 0m ? candle.High : candle.Low, contract))
            {
                var fill = target.Price * (1m - sign * rate);
                var fee = fill * target.Quantity * risk.FeeRate;
                var realized = sign * (fill - position.EntryPrice) * target.Quantity - fee;
                cashDelta += realized; partialPnl += realized; quantity -= target.Quantity;
                metrics = metrics with { ExitFees = metrics.ExitFees + fee, ExitNotional = metrics.ExitNotional + fill * target.Quantity };
                stage = target.Stage;
            }
            if (takeHit) { exitPrice = position.TakeProfit; exitReason = "TP"; }
        }
        if (exitPrice > 0m)
        {
            var requestedExit = exitPrice;
            exitPrice *= 1m - sign * rate;
            var fee = exitPrice * quantity * risk.FeeRate;
            var realized = sign * (exitPrice - position.EntryPrice) * quantity - fee;
            cashDelta += realized;
            var totalPnl = partialPnl + realized - position.EntryFee;
            var requestedEntry = metrics.RequestedEntryPrice > 0m ? metrics.RequestedEntryPrice : position.EntryPrice;
            var outcome = new TradeMetrics(position.RiskAmount, metrics.Mfe, metrics.Mae,
                metrics.FundingPaid, position.EntryFee + metrics.ExitFees + fee,
                sign * (position.EntryPrice - requestedEntry) / requestedEntry * 10_000m,
                sign * (requestedExit - exitPrice) / requestedExit * 10_000m,
                (metrics.ExitNotional + exitPrice * quantity) / initialQuantity,
                (decimal)(candle.CloseTime - position.OpenedAt).TotalMinutes, metrics.Regime, metrics.Context, metrics.Macd);
            return new(position with { Metrics = metrics }, cashDelta, true, exitPrice, exitReason, totalPnl, outcome);
        }
        metrics = metrics with { TakeProfitStage = stage };
        var updated = position with { Quantity = quantity, PartialTaken = stage > 0,
            InitialQuantity = initialQuantity, InitialStopLoss = initialStop, RealizedPartialPnl = partialPnl, Metrics = metrics };
        return new(TightenStop(updated, candle.CloseTime, candle.Close, atr, contract), cashDelta, false);
    }

    // Shared with live. This computes a proposal only; it never infers fills or calls an exchange.
    public PaperPosition TightenStop(PaperPosition position, DateTimeOffset time, decimal referencePrice,
        decimal atr, ContractInfo? contract)
    {
        var metrics = position.Metrics ?? new PositionMetrics();
        var initialQuantity = position.InitialQuantity > 0m ? position.InitialQuantity : position.Quantity;
        var initialStop = position.InitialStopLoss > 0m ? position.InitialStopLoss : position.StopLoss;
        var initialDistance = Math.Abs(position.EntryPrice - initialStop);
        var sign = position.Direction == TradeDirection.Long ? 1m : -1m;
        var rate = (costs.SlippageBasisPoints + costs.SpreadBasisPoints / 2m) / 10_000m;
        var stop = position.StopLoss;
        if (initialDistance > 0m && risk.UseDynamicPositionManagement && atr > 0m &&
            (metrics.LastStopAdjustedAt is null || time - metrics.LastStopAdjustedAt >= TimeSpan.FromMinutes(5 * risk.StopAdjustmentCooldownBars)))
        {
            // Use CLOSE, not the high/low that may have occurred before a partial fill.
            var currentR = sign * (referencePrice - position.EntryPrice) / initialDistance;
            if (currentR >= risk.BreakEvenTriggerR)
            {
                // Remaining quantity covers its share of entry fee and exit costs. Partial gains do not justify loosening it.
                var fundingPerUnit = Math.Max(0m, metrics.FundingPaid) / initialQuantity;
                var breakEvenFill = sign > 0m ? (position.EntryPrice * (1m + risk.FeeRate) + fundingPerUnit) / (1m - risk.FeeRate)
                    : (position.EntryPrice * (1m - risk.FeeRate) - fundingPerUnit) / (1m + risk.FeeRate);
                var breakEvenTrigger = breakEvenFill / (1m - sign * rate);
                stop = sign > 0m ? Math.Max(stop, breakEvenTrigger) : Math.Min(stop, breakEvenTrigger);
            }
            if (currentR >= risk.TrailingStartR)
            {
                var trailing = referencePrice - sign * atr * risk.TrailingAtrMultiplier;
                stop = sign > 0m ? Math.Max(stop, trailing) : Math.Min(stop, trailing);
            }
            if (contract is not null) stop = decimal.Round(stop, (int)contract.PricePrecision,
                sign > 0m ? MidpointRounding.ToPositiveInfinity : MidpointRounding.ToZero);
            if (sign * (stop - position.StopLoss) < atr * risk.MinimumTrailingStepAtr || sign * (referencePrice - stop) <= 0m)
                stop = position.StopLoss;
            if (stop != position.StopLoss) metrics = metrics with { LastStopAdjustedAt = time };
        }
        // Existing partial mode retains its behavior, but the amended stop becomes active only next bar.
        else if (!risk.UseDynamicPositionManagement && position.PartialTaken)
            stop = sign > 0m ? Math.Max(stop, position.EntryPrice) : Math.Min(stop, position.EntryPrice);
        return position with { StopLoss = stop, Metrics = metrics };
    }
}
