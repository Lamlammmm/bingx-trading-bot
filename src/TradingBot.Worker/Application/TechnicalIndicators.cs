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

    public static decimal Atr(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period + 1) return 0m;
        var trueRanges = new List<decimal>(period);
        for (var index = candles.Count - period; index < candles.Count; index++)
        {
            var current = candles[index];
            var previous = candles[index - 1];
            trueRanges.Add(Math.Max(current.High - current.Low,
                Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close))));
        }
        return trueRanges.Average();
    }
}
