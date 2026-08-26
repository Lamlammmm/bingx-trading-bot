using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.BingX;

internal sealed class ClosedCandleTracker
{
    private readonly Dictionary<TimeFrame, Candle> _currentCandles = new();

    public MarketUpdate? Observe(MarketUpdate update)
    {
        var current = update.Candle with { IsClosed = false };
        if (!_currentCandles.TryGetValue(update.TimeFrame, out var previous))
        {
            _currentCandles[update.TimeFrame] = current;
            return null;
        }

        if (current.OpenTime < previous.OpenTime) return null;
        if (current.OpenTime == previous.OpenTime)
        {
            _currentCandles[update.TimeFrame] = current;
            return null;
        }

        _currentCandles[update.TimeFrame] = current;
        return new MarketUpdate(update.TimeFrame, previous with { IsClosed = true });
    }

    public void DiscardThrough(TimeFrame timeFrame, DateTimeOffset closedOpenTime)
    {
        if (_currentCandles.TryGetValue(timeFrame, out var current) && current.OpenTime <= closedOpenTime)
            _currentCandles.Remove(timeFrame);
    }
}
