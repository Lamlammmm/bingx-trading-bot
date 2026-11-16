using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public static class TechnicalIndicators
{
    public static decimal Ema(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period) return 0m;
        var multiplier = 2m / (period + 1);
        var ema = candles.Take(period).Average(candle => candle.Close);
        for (var index = period; index < candles.Count; index++)
            ema = ((candles[index].Close - ema) * multiplier) + ema;
        return ema;
    }

    public static decimal AverageVolume(IReadOnlyList<Candle> candles, int period) =>
        candles.Count < period ? 0m : candles.TakeLast(period).Average(candle => candle.Volume);

    public static decimal HighestHigh(IReadOnlyList<Candle> candles, int period) =>
        candles.Count < period ? 0m : candles.TakeLast(period).Max(candle => candle.High);

    public static decimal LowestLow(IReadOnlyList<Candle> candles, int period) =>
        candles.Count < period ? 0m : candles.TakeLast(period).Min(candle => candle.Low);

    // Wilder's smoothing (RMA): seed with the simple average of the first `period` values, then
    // recursively smooth over the remaining history so the value reflects the whole series, not
    // just a fresh average of the trailing window (matches standard TradingView/exchange indicators).
    public static decimal Atr(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period + 1) return 0m;
        var trueRanges = new List<decimal>(candles.Count - 1);
        for (var index = 1; index < candles.Count; index++)
        {
            var current = candles[index];
            var previous = candles[index - 1];
            trueRanges.Add(Math.Max(current.High - current.Low,
                Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close))));
        }

        var atr = trueRanges.Take(period).Average();
        for (var index = period; index < trueRanges.Count; index++)
            atr = ((atr * (period - 1)) + trueRanges[index]) / period;
        return atr;
    }

    public static decimal Rsi(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period + 1) return 50m;
        var gains = new List<decimal>(candles.Count - 1);
        var losses = new List<decimal>(candles.Count - 1);
        for (var index = 1; index < candles.Count; index++)
        {
            var change = candles[index].Close - candles[index - 1].Close;
            gains.Add(change >= 0 ? change : 0m);
            losses.Add(change < 0 ? -change : 0m);
        }

        var averageGain = gains.Take(period).Average();
        var averageLoss = losses.Take(period).Average();
        for (var index = period; index < gains.Count; index++)
        {
            averageGain = ((averageGain * (period - 1)) + gains[index]) / period;
            averageLoss = ((averageLoss * (period - 1)) + losses[index]) / period;
        }

        if (averageLoss == 0m) return averageGain == 0m ? 50m : 100m;
        var relativeStrength = averageGain / averageLoss;
        return 100m - (100m / (1m + relativeStrength));
    }

    public static decimal BodyRatio(Candle candle)
    {
        var range = candle.High - candle.Low;
        return range <= 0m ? 0m : Math.Abs(candle.Close - candle.Open) / range;
    }

    public static decimal Adx(IReadOnlyList<Candle> candles, int period)
    {
        if (period < 2 || candles.Count < period * 2) return 0m;
        decimal tr = 0m, plus = 0m, minus = 0m, adx = 0m;
        var dxCount = 0;
        for (var i = 1; i < candles.Count; i++)
        {
            var up = candles[i].High - candles[i - 1].High;
            var down = candles[i - 1].Low - candles[i].Low;
            var range = Math.Max(candles[i].High - candles[i].Low,
                Math.Max(Math.Abs(candles[i].High - candles[i - 1].Close), Math.Abs(candles[i].Low - candles[i - 1].Close)));
            var positive = up > 0m && up > down ? up : 0m;
            var negative = down > 0m && down > up ? down : 0m;
            if (i <= period) { tr += range; plus += positive; minus += negative; }
            else { tr = tr - tr / period + range; plus = plus - plus / period + positive; minus = minus - minus / period + negative; }
            if (i < period) continue;
            var sum = plus + minus;
            var dx = tr == 0m || sum == 0m ? 0m : 100m * Math.Abs(plus - minus) / sum;
            dxCount++;
            adx = dxCount <= period ? adx + dx / period : (adx * (period - 1) + dx) / period;
        }
        return adx;
    }

    // Only complete UTC-aligned buckets. Missing/duplicate/forming 5m bars cannot create a 15m setup.
    public static IReadOnlyList<Candle> AggregateClosed(IReadOnlyList<Candle> candles, int minutes)
    {
        if (minutes < 5 || minutes % 5 != 0) throw new ArgumentOutOfRangeException(nameof(minutes));
        var duration = minutes * 60_000L;
        return candles.Where(c => c.IsClosed).OrderBy(c => c.OpenTime)
            .GroupBy(c => c.OpenTime.ToUnixTimeMilliseconds() / duration)
            .Where(g => g.Count() == minutes / 5 && g.Select(c => c.OpenTime).Distinct().Count() == g.Count() &&
                g.Select((c, i) => c.OpenTime.ToUnixTimeMilliseconds() == g.Key * duration + i * 300_000L &&
                    c.CloseTime == c.OpenTime.AddMinutes(5)).All(valid => valid))
            .Select(g => new Candle(g.First().OpenTime, g.First().OpenTime.AddMinutes(minutes), g.First().Open,
                g.Max(c => c.High), g.Min(c => c.Low), g.Last().Close, g.Sum(c => c.Volume), true)).ToArray();
    }
}
