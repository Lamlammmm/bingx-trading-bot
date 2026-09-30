using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class BreakoutStrategy(IOptions<StrategyOptions> options) : ITradingStrategy
{
    private readonly StrategyOptions _options = options.Value;
    private readonly Dictionary<string, DateTimeOffset> _lastSignalTimeBySymbol = new();
    private readonly Dictionary<string, PendingBreakout> _pendingBySymbol = new();

    private sealed record PendingBreakout(TradeDirection Direction, decimal Level, DateTimeOffset DetectedAt, string Reason);

    public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles)
    {
        if (_options.ExcludedSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase)) return null;

        var minimumCandles = Math.Max(_options.SlowEmaPeriod, Math.Max(_options.BreakoutLookback, _options.VolumeLookback)) + 1;
        if (entryCandles.Count < minimumCandles || mediumTrendCandles.Count < _options.SlowEmaPeriod || higherTrendCandles.Count < _options.SlowEmaPeriod)
            return null;

        var current = entryCandles[^1];
        var atr = TechnicalIndicators.Atr(entryCandles, _options.AtrPeriod);
        if (atr <= 0m) return null;
        var barDuration = current.CloseTime - current.OpenTime;

        if (_pendingBySymbol.TryGetValue(symbol, out var pending))
        {
            if (current.OpenTime - pending.DetectedAt > barDuration * _options.RetestConfirmationBars)
            {
                _pendingBySymbol.Remove(symbol);
            }
            else
            {
                var confirmed = pending.Direction == TradeDirection.Long
                    ? current.Low <= pending.Level && current.Close > pending.Level
                    : current.High >= pending.Level && current.Close < pending.Level;
                var failed = pending.Direction == TradeDirection.Long
                    ? current.Close < pending.Level
                    : current.Close > pending.Level;

                if (confirmed)
                {
                    var retestPreviousCandles = entryCandles.SkipLast(1).ToArray();
                    var retestRsi = TechnicalIndicators.Rsi(entryCandles, _options.RsiPeriod);
                    var retestBodyRatio = TechnicalIndicators.BodyRatio(current);
                    var retestAverageVolume = TechnicalIndicators.AverageVolume(retestPreviousCandles, _options.VolumeLookback);
                    var retestQualityIsValid = retestAverageVolume > 0m &&
                        retestBodyRatio >= _options.MinimumBodyRatio &&
                        current.Volume < retestAverageVolume * _options.MaximumVolumeMultiplier &&
                        (pending.Direction == TradeDirection.Long
                            ? retestRsi < _options.RsiOverbought
                            : retestRsi > _options.RsiOversold);
                    if (!retestQualityIsValid)
                    {
                        _pendingBySymbol.Remove(symbol);
                        return null;
                    }

                    _pendingBySymbol.Remove(symbol);
                    if (_lastSignalTimeBySymbol.TryGetValue(symbol, out var lastSignalTime) &&
                        current.OpenTime - lastSignalTime < barDuration * _options.SignalCooldownBars) return null;

                    _lastSignalTimeBySymbol[symbol] = current.OpenTime;
                    return new StrategySignal(symbol, pending.Direction, current.CloseTime, current.Close, atr,
                        $"{pending.Reason}; level retested and held");
                }

                if (failed)
                {
                    _pendingBySymbol.Remove(symbol);
                }
                // otherwise: still waiting, keep pending for another bar within the confirmation window
                return null;
            }
        }

        if (_lastSignalTimeBySymbol.TryGetValue(symbol, out var existingLastSignalTime) &&
            current.OpenTime - existingLastSignalTime < barDuration * _options.SignalCooldownBars) return null;

        var previousCandles = entryCandles.SkipLast(1).ToArray();
        var rsi = TechnicalIndicators.Rsi(previousCandles, _options.RsiPeriod);
        var bodyRatio = TechnicalIndicators.BodyRatio(current);
        var averageVolume = TechnicalIndicators.AverageVolume(previousCandles, _options.VolumeLookback);
        var recentHigh = TechnicalIndicators.HighestHigh(previousCandles, _options.BreakoutLookback);
        var recentLow = TechnicalIndicators.LowestLow(previousCandles, _options.BreakoutLookback);
        var bullishTrend = IsBullishTrend(mediumTrendCandles) && IsBullishTrend(higherTrendCandles);
        var bearishTrend = IsBearishTrend(mediumTrendCandles) && IsBearishTrend(higherTrendCandles);
        if (averageVolume <= 0m || bodyRatio < _options.MinimumBodyRatio ||
            current.Volume >= averageVolume * _options.MaximumVolumeMultiplier) return null;

        if (bullishTrend && current.Close > recentHigh && current.Volume >= averageVolume * _options.VolumeMultiplier
            && rsi < _options.RsiOverbought)
        {
            _pendingBySymbol[symbol] = new PendingBreakout(TradeDirection.Long, recentHigh, current.OpenTime,
                $"4H/1H bullish; entry-TF close broke {recentHigh:0.########}; volume {current.Volume:0.########} >= {averageVolume * _options.VolumeMultiplier:0.########}; RSI={rsi:0.#}; body={bodyRatio:0.##}");
            return null;
        }

        if (bearishTrend && current.Close < recentLow && current.Volume >= averageVolume * _options.VolumeMultiplier
            && rsi > _options.RsiOversold)
        {
            _pendingBySymbol[symbol] = new PendingBreakout(TradeDirection.Short, recentLow, current.OpenTime,
                $"4H/1H bearish; entry-TF close broke {recentLow:0.########}; volume {current.Volume:0.########} >= {averageVolume * _options.VolumeMultiplier:0.########}; RSI={rsi:0.#}; body={bodyRatio:0.##}");
            return null;
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
