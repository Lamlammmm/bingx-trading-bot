using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class BreakoutStrategy(IOptions<StrategyOptions> options) : ITradingStrategy
{
    private readonly StrategyOptions _options = options.Value;
    private DateTimeOffset? _lastSignalTime;
    private int? _lastSignalBarIndex;

    public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> fifteenMinuteCandles,
        IReadOnlyList<Candle> oneHourCandles, IReadOnlyList<Candle> fourHourCandles)
    {
        var minimumCandles = Math.Max(_options.SlowEmaPeriod, Math.Max(_options.BreakoutLookback, _options.VolumeLookback)) + 1;
        if (fifteenMinuteCandles.Count < minimumCandles || oneHourCandles.Count < _options.SlowEmaPeriod || fourHourCandles.Count < _options.SlowEmaPeriod)
            return null;

        var current = fifteenMinuteCandles[^1];
        var barIndex = fifteenMinuteCandles.Count - 1;
        if (_lastSignalTime == current.OpenTime) return null;
        if (_lastSignalBarIndex is not null && barIndex - _lastSignalBarIndex < _options.SignalCooldownBars) return null;

        var previousCandles = fifteenMinuteCandles.SkipLast(1).ToArray();
        var atr = TechnicalIndicators.Atr(fifteenMinuteCandles, _options.AtrPeriod);
        var rsi = TechnicalIndicators.Rsi(previousCandles, _options.RsiPeriod);
        var bodyRatio = TechnicalIndicators.BodyRatio(current);
        var averageVolume = TechnicalIndicators.AverageVolume(previousCandles, _options.VolumeLookback);
        var recentHigh = TechnicalIndicators.HighestHigh(previousCandles, _options.BreakoutLookback);
        var recentLow = TechnicalIndicators.LowestLow(previousCandles, _options.BreakoutLookback);
        var bullishTrend = IsBullishTrend(oneHourCandles) && IsBullishTrend(fourHourCandles);
        var bearishTrend = IsBearishTrend(oneHourCandles) && IsBearishTrend(fourHourCandles);
        if (atr <= 0m || averageVolume <= 0m || bodyRatio < _options.MinimumBodyRatio) return null;

        if (bullishTrend && current.Close > recentHigh && current.Volume >= averageVolume * _options.VolumeMultiplier
            && rsi < _options.RsiOverbought)
        {
            _lastSignalTime = current.OpenTime;
            _lastSignalBarIndex = barIndex;
            return new StrategySignal(symbol, TradeDirection.Long, current.CloseTime, current.Close, atr,
                $"4H/1H bullish; 15m close broke {recentHigh:0.########}; volume {current.Volume:0.########} >= {averageVolume * _options.VolumeMultiplier:0.########}; RSI={rsi:0.#}; body={bodyRatio:0.##}");
        }

        if (bearishTrend && current.Close < recentLow && current.Volume >= averageVolume * _options.VolumeMultiplier
            && rsi > _options.RsiOversold)
        {
            _lastSignalTime = current.OpenTime;
            _lastSignalBarIndex = barIndex;
            return new StrategySignal(symbol, TradeDirection.Short, current.CloseTime, current.Close, atr,
                $"4H/1H bearish; 15m close broke {recentLow:0.########}; volume {current.Volume:0.########} >= {averageVolume * _options.VolumeMultiplier:0.########}; RSI={rsi:0.#}; body={bodyRatio:0.##}");
        }

        return null;
    }

    private bool IsBullishTrend(IReadOnlyList<Candle> candles)
    {
        var fast = TechnicalIndicators.Ema(candles, _options.FastEmaPeriod);
        var slow = TechnicalIndicators.Ema(candles, _options.SlowEmaPeriod);
        return fast > slow && candles[^1].Close > fast;
    }

    private bool IsBearishTrend(IReadOnlyList<Candle> candles)
    {
        var fast = TechnicalIndicators.Ema(candles, _options.FastEmaPeriod);
        var slow = TechnicalIndicators.Ema(candles, _options.SlowEmaPeriod);
        return fast < slow && candles[^1].Close < fast;
    }
}
