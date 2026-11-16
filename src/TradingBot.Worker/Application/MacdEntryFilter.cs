using Microsoft.Extensions.Logging;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public static class MacdEntryFilter
{
    private static readonly EventId DecisionEvent = new(1_100, nameof(MacdEntryFilter));

    public static MacdEntryDecision Evaluate(IReadOnlyList<Candle> candles, StrategySignal signal, TimeFrame timeFrame,
        MacdOptions options)
    {
        var decision = new MacdEntryDecision(false, "missing closed signal candle", timeFrame, signal.Time,
            options.Fast, options.Slow, options.Signal);
        if (!options.Enabled) return decision with { Allowed = true, Reason = "filter disabled" };
        if (!options.IsValid) return decision with { Reason = "invalid MACD periods" };

        var minutes = timeFrame switch
        {
            TimeFrame.OneMinute => 1,
            TimeFrame.FiveMinutes => 5,
            TimeFrame.FifteenMinutes => 15,
            TimeFrame.ThirtyMinutes => 30,
            TimeFrame.OneHour => 60,
            TimeFrame.FourHours => 240,
            _ => 0
        };
        if (minutes == 0) return decision with { Reason = "unsupported MACD timeframe" };

        // Use a fixed trailing window to let both EMAs settle and keep results independent of older history.
        var required = Math.Max(5L * options.Slow, options.Slow + 5L * options.Signal);
        var closed = candles.Where(c => c.IsClosed && c.CloseTime <= signal.Time)
            .GroupBy(c => c.OpenTime).OrderBy(g => g.Key).ToArray();
        if (closed.Length == 0 || !closed[^1].Any(c => c.CloseTime == signal.Time)) return decision;
        if (closed.Length < required)
            return decision with { Reason = $"insufficient MACD warm-up ({closed.Length}/{required} closed candles)" };

        var duration = TimeSpan.FromMinutes(minutes);
        var window = new Candle[(int)required];
        for (var i = 0; i < window.Length; i++)
        {
            var group = closed[closed.Length - window.Length + i];
            var candle = group.First();
            if (group.Any(c => c.CloseTime != candle.CloseTime || c.Close != candle.Close))
                return decision with { Reason = "conflicting duplicate MACD candles" };
            var length = candle.CloseTime - candle.OpenTime;
            // Accept candle-end timestamps represented as the boundary or its final millisecond.
            if (candle.Close <= 0m || (length != duration && length != duration - TimeSpan.FromMilliseconds(1)))
                return decision with { Reason = "invalid MACD candle price or duration" };
            if (i > 0 && candle.OpenTime - window[i - 1].OpenTime != duration)
                return decision with { Reason = "gap in MACD candle history" };
            window[i] = candle;
        }

        try
        {
            var fast = TechnicalIndicators.Ema(window[..options.Fast], options.Fast);
            var slow = TechnicalIndicators.Ema(window[..options.Slow], options.Slow);
            var fastMultiplier = 2m / (options.Fast + 1);
            var slowMultiplier = 2m / (options.Slow + 1);
            var signalMultiplier = 2m / (options.Signal + 1);
            decimal macd = 0m, signalEma = 0m, histogram = 0m, previousHistogram = 0m;
            for (var i = options.Fast; i < window.Length; i++)
            {
                var close = window[i].Close;
                fast += (close - fast) * fastMultiplier;
                if (i < options.Slow - 1) continue;
                if (i >= options.Slow) slow += (close - slow) * slowMultiplier;

                macd = fast - slow;
                var macdCount = i - options.Slow + 2;
                if (macdCount <= options.Signal)
                {
                    signalEma += macd;
                    if (macdCount < options.Signal) continue;
                    signalEma /= options.Signal;
                }
                else signalEma += (macd - signalEma) * signalMultiplier;

                previousHistogram = histogram;
                histogram = macd - signalEma;
            }

            var allowed = signal.Direction switch
            {
                TradeDirection.Long => previousHistogram <= 0m && histogram > 0m,
                TradeDirection.Short => previousHistogram >= 0m && histogram < 0m,
                _ => false
            };
            return decision with { Allowed = allowed, Reason = allowed ? "histogram crossover confirmed" : "no matching histogram crossover",
                Macd = macd, Signal = signalEma, PreviousHistogram = previousHistogram, Histogram = histogram };
        }
        catch (OverflowException)
        {
            return decision with { Reason = "MACD calculation overflow" };
        }
    }

    public static void Log(ILogger logger, string mode, StrategySignal signal, MacdEntryDecision decision) =>
        logger.LogInformation(DecisionEvent,
            "{Mode} MACD {Result} | symbol={Symbol} | timeframe={TimeFrame} | candleClose={CandleCloseTime} | direction={Direction} | MACD={Macd} | signal={Signal} | previousHistogram={PreviousHistogram} | histogram={Histogram} | reason={Reason}",
            mode, decision.Allowed ? "Allowed" : "Blocked", signal.Symbol, decision.TimeFrame, decision.CandleCloseTime,
            signal.Direction, decision.Macd, decision.Signal, decision.PreviousHistogram, decision.Histogram, decision.Reason);
}
