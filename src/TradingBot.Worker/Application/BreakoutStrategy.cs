using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class BreakoutStrategy(IOptions<StrategyOptions> options) : ITradingStrategy
{
    private readonly StrategyOptions _options = options.Value;
    private readonly Dictionary<string, (DateTimeOffset? LastSignalTime, int? LastSignalBarIndex)> _cooldownBySymbol = new();
    private readonly Dictionary<string, PendingBreakout> _pendingBySymbol = new();

    private sealed record PendingBreakout(TradeDirection Direction, decimal Level, int DetectedBarIndex, string Reason);

    public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles)
    {
        var minimumCandles = Math.Max(_options.SlowEmaPeriod, Math.Max(_options.BreakoutLookback, _options.VolumeLookback)) + 1;
        if (entryCandles.Count < minimumCandles || mediumTrendCandles.Count < _options.SlowEmaPeriod || higherTrendCandles.Count < _options.SlowEmaPeriod)
            return null;

        var current = entryCandles[^1];
        var barIndex = entryCandles.Count - 1;
        var atr = TechnicalIndicators.Atr(entryCandles, _options.AtrPeriod);
        if (atr <= 0m) return null;

        if (_pendingBySymbol.TryGetValue(symbol, out var pending))
        {
            if (barIndex - pending.DetectedBarIndex > _options.RetestConfirmationBars)
            {
                _pendingBySymbol.Remove(symbol);
            }
            else
            {
                var confirmed = pending.Direction == TradeDirection.Long
                    ? current.Close > pending.Level
                    : current.Close < pending.Level;
                var failed = pending.Direction == TradeDirection.Long
                    ? current.Close < pending.Level
                    : current.Close > pending.Level;

                if (confirmed)
                {
                    _pendingBySymbol.Remove(symbol);
                    _cooldownBySymbol.TryGetValue(symbol, out var cooldown);
                    if (cooldown.LastSignalTime == current.OpenTime) return null;
                    if (cooldown.LastSignalBarIndex is not null && barIndex - cooldown.LastSignalBarIndex < _options.SignalCooldownBars) return null;

                    _cooldownBySymbol[symbol] = (current.OpenTime, barIndex);
                    return new StrategySignal(symbol, pending.Direction, current.CloseTime, current.Close, atr,
                        $"{pending.Reason}; retest confirmed {barIndex - pending.DetectedBarIndex} bar(s) later");
                }

                if (failed)
                {
                    _pendingBySymbol.Remove(symbol);
                }
                // otherwise: still waiting, keep pending for another bar within the confirmation window
                return null;
            }
        }

        _cooldownBySymbol.TryGetValue(symbol, out var existingCooldown);
        if (existingCooldown.LastSignalTime == current.OpenTime) return null;
        if (existingCooldown.LastSignalBarIndex is not null && barIndex - existingCooldown.LastSignalBarIndex < _options.SignalCooldownBars) return null;

        var previousCandles = entryCandles.SkipLast(1).ToArray();
        var rsi = TechnicalIndicators.Rsi(previousCandles, _options.RsiPeriod);
        var bodyRatio = TechnicalIndicators.BodyRatio(current);
        var averageVolume = TechnicalIndicators.AverageVolume(previousCandles, _options.VolumeLookback);
        var recentHigh = TechnicalIndicators.HighestHigh(previousCandles, _options.BreakoutLookback);
        var recentLow = TechnicalIndicators.LowestLow(previousCandles, _options.BreakoutLookback);
        var bullishTrend = IsBullishTrend(mediumTrendCandles) && IsBullishTrend(higherTrendCandles);
        var bearishTrend = IsBearishTrend(mediumTrendCandles) && IsBearishTrend(higherTrendCandles);
        if (averageVolume <= 0m || bodyRatio < _options.MinimumBodyRatio) return null;

        if (bullishTrend && current.Close > recentHigh && current.Volume >= averageVolume * _options.VolumeMultiplier
            && rsi < _options.RsiOverbought)
        {
            _pendingBySymbol[symbol] = new PendingBreakout(TradeDirection.Long, recentHigh, barIndex,
                $"4H/1H bullish; entry-TF close broke {recentHigh:0.########}; volume {current.Volume:0.########} >= {averageVolume * _options.VolumeMultiplier:0.########}; RSI={rsi:0.#}; body={bodyRatio:0.##}");
            return null;
        }

        if (bearishTrend && current.Close < recentLow && current.Volume >= averageVolume * _options.VolumeMultiplier
            && rsi > _options.RsiOversold)
        {
            _pendingBySymbol[symbol] = new PendingBreakout(TradeDirection.Short, recentLow, barIndex,
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
