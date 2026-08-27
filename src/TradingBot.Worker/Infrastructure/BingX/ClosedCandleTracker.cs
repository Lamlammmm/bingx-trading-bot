using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.BingX;

internal sealed class ClosedCandleTracker
{
    private readonly Dictionary<(string Symbol, TimeFrame TimeFrame), Candle> _currentCandles = new();

    public MarketUpdate? Observe(MarketUpdate update)
    {
        var key = (update.Symbol, update.TimeFrame);
        var current = update.Candle with { IsClosed = false };
        if (!_currentCandles.TryGetValue(key, out var previous))
        {
            _currentCandles[key] = current;
            return null;
        }

        if (current.OpenTime < previous.OpenTime) return null;
        if (current.OpenTime == previous.OpenTime)
        {
            _currentCandles[key] = current;
            return null;
        }

        _currentCandles[key] = current;
        return new MarketUpdate(update.Symbol, update.TimeFrame, previous with { IsClosed = true });
    }

    public void DiscardThrough(string symbol, TimeFrame timeFrame, DateTimeOffset closedOpenTime)
    {
        var key = (symbol, timeFrame);
        if (_currentCandles.TryGetValue(key, out var current) && current.OpenTime <= closedOpenTime)
            _currentCandles.Remove(key);
    }
}
