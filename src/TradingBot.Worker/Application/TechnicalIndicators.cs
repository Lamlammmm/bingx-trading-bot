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

        if (averageLoss == 0m) return 100m;
        var relativeStrength = averageGain / averageLoss;
        return 100m - (100m / (1m + relativeStrength));
    }

    public static decimal BodyRatio(Candle candle)
    {
        var range = candle.High - candle.Low;
        return range <= 0m ? 0m : Math.Abs(candle.Close - candle.Open) / range;
    }
}
