using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class BreakoutStrategy(IOptions<StrategyOptions> options) : ITradingStrategy
{
    private readonly StrategyOptions _options = options.Value;
    private readonly Dictionary<string, DateTimeOffset> _lastSignalTimeBySymbol = new();
    private readonly Dictionary<string, PendingBreakout> _pendingBySymbol = new();
    public SignalEvaluation? LastEvaluation { get; private set; }

    private sealed record PendingBreakout(TradeDirection Direction, decimal Level, decimal AtrBeforeBreakout,
        DateTimeOffset DetectedAt, string Reason);

    public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles) =>
        Evaluate(symbol, entryCandles, mediumTrendCandles, higherTrendCandles, null);

    internal void ResetPending(string symbol) => _pendingBySymbol.Remove(symbol);

    internal StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles, TradeDirection? confirmedDirection)
    {
        LastEvaluation = null;
        // Higher-timeframe snapshots may include a forming candle or future context during replay.
        entryCandles = entryCandles.Where(c => c.IsClosed).ToArray();
        if (entryCandles.Count == 0) return null;
        var signalTime = entryCandles[^1].CloseTime;
        if (_options.ExcludedSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
        {
            LastEvaluation = new(signalTime, MarketRegime.Unknown, "symbol excluded by strategy", ReasonCode: "SYMBOL_EXCLUDED");
            return null;
        }
        mediumTrendCandles = mediumTrendCandles.Where(c => c.IsClosed && c.CloseTime <= signalTime).ToArray();
        higherTrendCandles = higherTrendCandles.Where(c => c.IsClosed && c.CloseTime <= signalTime).ToArray();

        var minimumCandles = Math.Max(_options.SlowEmaPeriod, Math.Max(_options.BreakoutLookback, _options.VolumeLookback)) + 1;
        if (entryCandles.Count < minimumCandles || mediumTrendCandles.Count < _options.SlowEmaPeriod || higherTrendCandles.Count < _options.SlowEmaPeriod)
        {
            LastEvaluation = new(signalTime, MarketRegime.Unknown, "insufficient closed history", ReasonCode: "INSUFFICIENT_HISTORY");
            _pendingBySymbol.Remove(symbol);
            return null;
        }
        if (signalTime - mediumTrendCandles[^1].CloseTime > TimeSpan.FromMinutes(30) ||
            signalTime - higherTrendCandles[^1].CloseTime > TimeSpan.FromHours(1))
        {
            LastEvaluation = new(signalTime, MarketRegime.Unknown, "trend candles are stale", ReasonCode: "STALE_MARKET_DATA");
            _pendingBySymbol.Remove(symbol);
            return null;
        }

        var current = entryCandles[^1];
        var atr = TechnicalIndicators.Atr(entryCandles, _options.AtrPeriod);
        LastEvaluation = new(signalTime, MarketRegime.Unknown, "waiting for qualified breakout", ReasonCode: "NO_VALID_SETUP", AtrValue: atr);
        if (atr <= 0m)
        {
            LastEvaluation = LastEvaluation with { ReasonCode = "ATR_TOO_LOW" };
            return null;
        }
        var barDuration = current.CloseTime - current.OpenTime;

        if (_pendingBySymbol.TryGetValue(symbol, out var pending))
        {
            if ((confirmedDirection is not null && confirmedDirection != pending.Direction) ||
                current.OpenTime - pending.DetectedAt > barDuration * _options.RetestConfirmationBars)
            {
                _pendingBySymbol.Remove(symbol);
            }
            else
            {
                if (current.OpenTime <= pending.DetectedAt) return null;
                // A missed update must not revive a setup that already closed back through the level.
                var failed = entryCandles.Any(candle => candle.OpenTime > pending.DetectedAt &&
                    (pending.Direction == TradeDirection.Long ? candle.Close < pending.Level : candle.Close > pending.Level));
                if (failed)
                {
                    LastEvaluation = LastEvaluation with { ReasonCode = "BREAKOUT_INVALIDATED" };
                    _pendingBySymbol.Remove(symbol);
                    return null;
                }
                var confirmed = pending.Direction == TradeDirection.Long
                    ? current.Low <= pending.Level && current.Close > pending.Level
                    : current.High >= pending.Level && current.Close < pending.Level;

                if (confirmed)
                {
                    var retestPreviousCandles = entryCandles.SkipLast(1).ToArray();
                    var retestRsi = TechnicalIndicators.Rsi(entryCandles, _options.RsiPeriod);
                    var retestBodyRatio = TechnicalIndicators.BodyRatio(current);
                    var retestAverageVolume = TechnicalIndicators.AverageVolume(retestPreviousCandles, _options.VolumeLookback);
                    LastEvaluation = LastEvaluation with { RsiEntry = retestRsi,
                        VolumeRatio = retestAverageVolume > 0m ? current.Volume / retestAverageVolume : null };
                    var retestQualityIsValid = retestAverageVolume > 0m &&
                        (_options.MaxRetestEntryDistanceAtr == 0m ||
                            Math.Abs(current.Close - pending.Level) <= pending.AtrBeforeBreakout * _options.MaxRetestEntryDistanceAtr) &&
                        retestBodyRatio >= _options.MinimumBodyRatio &&
                        current.Volume < retestAverageVolume * _options.MaximumVolumeMultiplier &&
                        (pending.Direction == TradeDirection.Long
                            ? current.Close > current.Open && retestRsi < _options.RsiOverbought &&
                                (confirmedDirection is not null || IsBullishTrend(mediumTrendCandles) && IsBullishTrend(higherTrendCandles))
                            : current.Close < current.Open && retestRsi > _options.RsiOversold &&
                                (confirmedDirection is not null || IsBearishTrend(mediumTrendCandles) && IsBearishTrend(higherTrendCandles)));
                    if (!retestQualityIsValid)
                    {
                        LastEvaluation = LastEvaluation with { ReasonCode = "RETEST_QUALITY_REJECTED" };
                        _pendingBySymbol.Remove(symbol);
                        return null;
                    }

                    _pendingBySymbol.Remove(symbol);
                    if (confirmedDirection is null && _lastSignalTimeBySymbol.TryGetValue(symbol, out var lastSignalTime) &&
                        current.OpenTime - lastSignalTime < barDuration * _options.SignalCooldownBars)
                    {
                        LastEvaluation = LastEvaluation with { ReasonCode = "SIGNAL_COOLDOWN_ACTIVE" };
                        return null;
                    }

                    if (confirmedDirection is null) _lastSignalTimeBySymbol[symbol] = current.OpenTime;
                    var retestCandles = entryCandles.Where(candle => candle.OpenTime > pending.DetectedAt).ToArray();
                    var invalidationPrice = pending.Direction == TradeDirection.Long
                        ? retestCandles.Min(candle => candle.Low)
                        : retestCandles.Max(candle => candle.High);
                    LastEvaluation = LastEvaluation with { Reason = "breakout retest confirmed", ReasonCode = null,
                        Regime = pending.Direction == TradeDirection.Long ? MarketRegime.TrendingUp : MarketRegime.TrendingDown };
                    return new StrategySignal(symbol, pending.Direction, current.CloseTime, current.Close, atr,
                        $"{pending.Reason}; level retested and held; entry distance={Math.Abs(current.Close - pending.Level):0.########}; ATR before breakout={pending.AtrBeforeBreakout:0.########}; closed medium/higher {(confirmedDirection is null ? "trends" : "context")} and candle direction reconfirmed",
                        InvalidationPrice: invalidationPrice);
                }
                // otherwise: still waiting, keep pending for another bar within the confirmation window
                LastEvaluation = LastEvaluation with { ReasonCode = "RETEST_PENDING" };
                return null;
            }
        }

        if (confirmedDirection is null && _lastSignalTimeBySymbol.TryGetValue(symbol, out var existingLastSignalTime) &&
            current.OpenTime - existingLastSignalTime < barDuration * _options.SignalCooldownBars)
        {
            LastEvaluation = LastEvaluation with { ReasonCode = "SIGNAL_COOLDOWN_ACTIVE" };
            return null;
        }

        var previousCandles = entryCandles.SkipLast(1).ToArray();
        var atrBeforeBreakout = TechnicalIndicators.Atr(previousCandles, _options.AtrPeriod);
        if (atrBeforeBreakout <= 0m)
        {
            LastEvaluation = LastEvaluation with { ReasonCode = "ATR_TOO_LOW" };
            return null;
        }
        var rsi = TechnicalIndicators.Rsi(previousCandles, _options.RsiPeriod);
        var bodyRatio = TechnicalIndicators.BodyRatio(current);
        var averageVolume = TechnicalIndicators.AverageVolume(previousCandles, _options.VolumeLookback);
        var recentHigh = TechnicalIndicators.HighestHigh(previousCandles, _options.BreakoutLookback);
        var recentLow = TechnicalIndicators.LowestLow(previousCandles, _options.BreakoutLookback);
        var bullishTrend = confirmedDirection is null
            ? IsBullishTrend(mediumTrendCandles) && IsBullishTrend(higherTrendCandles)
            : confirmedDirection == TradeDirection.Long;
        var bearishTrend = confirmedDirection is null
            ? IsBearishTrend(mediumTrendCandles) && IsBearishTrend(higherTrendCandles)
            : confirmedDirection == TradeDirection.Short;
        LastEvaluation = LastEvaluation with
        {
            Regime = bullishTrend ? MarketRegime.TrendingUp : bearishTrend ? MarketRegime.TrendingDown : MarketRegime.Sideways,
            RsiEntry = rsi, VolumeRatio = averageVolume > 0m ? current.Volume / averageVolume : null,
            ReasonCode = !bullishTrend && !bearishTrend ? "NO_TREND" : "NO_VALID_SETUP"
        };
        if (averageVolume <= 0m || bodyRatio < _options.MinimumBodyRatio ||
            current.Volume >= averageVolume * _options.MaximumVolumeMultiplier)
        {
            LastEvaluation = LastEvaluation with { ReasonCode = averageVolume <= 0m ? "VOLUME_UNAVAILABLE"
                : bodyRatio < _options.MinimumBodyRatio ? "CANDLE_BODY_TOO_SMALL" : "VOLUME_ABOVE_CAP" };
            return null;
        }

        if (bullishTrend && current.Close > current.Open && current.Close > recentHigh && current.Volume >= averageVolume * _options.VolumeMultiplier
            && rsi < _options.RsiOverbought)
        {
            _pendingBySymbol[symbol] = new PendingBreakout(TradeDirection.Long, recentHigh, atrBeforeBreakout, current.OpenTime,
                $"{(confirmedDirection is null ? "Medium/higher trends bullish" : "Adaptive trend bullish")}; entry-TF close broke {recentHigh:0.########}; volume {current.Volume:0.########} >= {averageVolume * _options.VolumeMultiplier:0.########}; RSI={rsi:0.#}; body={bodyRatio:0.##}");
            LastEvaluation = LastEvaluation with { ReasonCode = "RETEST_PENDING" };
            return null;
        }

        if (bearishTrend && current.Close < current.Open && current.Close < recentLow && current.Volume >= averageVolume * _options.VolumeMultiplier
            && rsi > _options.RsiOversold)
        {
            _pendingBySymbol[symbol] = new PendingBreakout(TradeDirection.Short, recentLow, atrBeforeBreakout, current.OpenTime,
                $"{(confirmedDirection is null ? "Medium/higher trends bearish" : "Adaptive trend bearish")}; entry-TF close broke {recentLow:0.########}; volume {current.Volume:0.########} >= {averageVolume * _options.VolumeMultiplier:0.########}; RSI={rsi:0.#}; body={bodyRatio:0.##}");
            LastEvaluation = LastEvaluation with { ReasonCode = "RETEST_PENDING" };
            return null;
        }

        var priceBrokeTrendLevel = (bullishTrend && current.Close > current.Open && current.Close > recentHigh) ||
            (bearishTrend && current.Close < current.Open && current.Close < recentLow);
        if (priceBrokeTrendLevel)
        {
            var lowVolume = current.Volume < averageVolume * _options.VolumeMultiplier;
            var rsiRejected = bullishTrend ? rsi >= _options.RsiOverbought : rsi <= _options.RsiOversold;
            LastEvaluation = LastEvaluation with { ReasonCode = lowVolume ? "LOW_VOLUME" : "RSI_FILTER_REJECTED",
                SecondaryReasons = lowVolume && rsiRejected ? ["RSI_FILTER_REJECTED"] : null };
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
