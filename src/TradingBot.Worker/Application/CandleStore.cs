using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class InMemoryCandleStore : ICandleStore
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Symbol, TimeFrame TimeFrame), List<Candle>> _candles = new();

    public void Upsert(string symbol, TimeFrame timeFrame, Candle candle)
    {
        lock (_sync)
        {
            var key = (symbol, timeFrame);
            if (!_candles.TryGetValue(key, out var candles))
            {
                candles = new List<Candle>();
                _candles[key] = candles;
            }

            var index = candles.FindIndex(existing => existing.OpenTime == candle.OpenTime);
            if (index >= 0) candles[index] = candle;
            else candles.Add(candle);
            candles.Sort((left, right) => left.OpenTime.CompareTo(right.OpenTime));
            if (candles.Count > 1_000) candles.RemoveRange(0, candles.Count - 1_000);
        }
    }

    public IReadOnlyList<Candle> Get(string symbol, TimeFrame timeFrame)
    {
        lock (_sync)
        {
            return _candles.TryGetValue((symbol, timeFrame), out var candles) ? candles.ToArray() : Array.Empty<Candle>();
        }
    }
}
