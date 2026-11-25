using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

internal sealed class AdaptiveCurrentEntryPolicy(StrategyOptions options, BreakoutStrategy breakout) : ITradingStrategy
{
    private readonly Dictionary<string, DateTimeOffset> _lastSignalBySymbol = new(StringComparer.OrdinalIgnoreCase);

    public SignalEvaluation? LastEvaluation { get; private set; }

    public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles)
    {
        LastEvaluation = null;
        var entry = entryCandles.Where(c => c.IsClosed).OrderBy(c => c.OpenTime).ToArray();
        if (entry.Length == 0) return null;
        var current = entry[^1];
        var time = current.CloseTime;
        LastEvaluation = new(time, MarketRegime.Unknown, "insufficient closed multi-timeframe history", ReasonCode: "INSUFFICIENT_HISTORY");
        if (options.ExcludedSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
        {
            LastEvaluation = LastEvaluation with { Reason = "symbol excluded by strategy", ReasonCode = "SYMBOL_EXCLUDED" };
            breakout.ResetPending(symbol);
            return null;
        }

        if (!options.UseMultiTimeframeAnalysis || options.FastEmaPeriod < 2 || options.SlowEmaPeriod <= options.FastEmaPeriod ||
            options.PrimaryEmaPeriod < options.SlowEmaPeriod || options.StructureLookback is < 2 or > 250 ||
            options.AdxPeriod is < 2 or > 250 || options.AtrPeriod is < 2 or > 150 || options.RsiPeriod is < 2 or > 150 ||
            options.MinimumAdx is <= 0m or > 100m || options.MinimumAtrPercent <= 0m || options.MaximumAtrPercent <= options.MinimumAtrPercent ||
            options.RsiOversold is < 0m or >= 50m || options.RsiOverbought is <= 50m or > 100m || options.MaximumSetupDistanceAtr <= 0m ||
            options.SlowEmaPeriod > 500 || options.BreakoutLookback is < 2 or > 500 || options.VolumeLookback is < 1 or > 500 ||
            options.MinimumBodyRatio is <= 0m or > 1m || options.VolumeMultiplier <= 0m || options.MaximumVolumeMultiplier <= options.VolumeMultiplier ||
            options.MaxRetestEntryDistanceAtr <= 0m || options.SignalCooldownBars < 0 || options.RetestConfirmationBars < 1)
            throw new InvalidOperationException("Invalid adaptive Current strategy configuration.");

        var medium = mediumTrendCandles.Where(c => c.IsClosed && c.CloseTime <= time).OrderBy(c => c.OpenTime).ToArray();
        var higher = higherTrendCandles.Where(c => c.IsClosed && c.CloseTime <= time).OrderBy(c => c.OpenTime).ToArray();
        var prior = entry[..^1];
        var setup = TechnicalIndicators.AggregateClosed(prior, 15);
        var setupBars = Math.Max(options.FastEmaPeriod, 1 + Math.Max(options.AtrPeriod, options.RsiPeriod));
        var entryBars = 1 + Math.Max(3 * setupBars + 2, Math.Max(options.SlowEmaPeriod, Math.Max(options.VolumeLookback, options.BreakoutLookback)));
        var mediumBars = Math.Max(options.SlowEmaPeriod, 2 * options.StructureLookback);
        var higherBars = Math.Max(options.PrimaryEmaPeriod, Math.Max(2 * options.AdxPeriod + 1, options.AtrPeriod + 1));
        if (entry.Length < entryBars || medium.Length < mediumBars || higher.Length < higherBars || setup.Count < setupBars)
        {
            breakout.ResetPending(symbol);
            return null;
        }
        if (time - medium[^1].CloseTime >= TimeSpan.FromMinutes(30) || time - higher[^1].CloseTime >= TimeSpan.FromHours(1) ||
            current.OpenTime - setup[^1].CloseTime >= TimeSpan.FromMinutes(15) ||
            !Contiguous(entry.TakeLast(entryBars), 5) || !Contiguous(medium.TakeLast(mediumBars), 30) ||
            !Contiguous(higher.TakeLast(higherBars), 60))
        {
            LastEvaluation = LastEvaluation with { Reason = "stale or gapped market data", ReasonCode = "STALE_MARKET_DATA" };
            breakout.ResetPending(symbol);
            return null;
        }

        var fast = TechnicalIndicators.Ema(higher, options.FastEmaPeriod);
        var slow = TechnicalIndicators.Ema(higher, options.SlowEmaPeriod);
        var primary = TechnicalIndicators.Ema(higher, options.PrimaryEmaPeriod);
        var adx = TechnicalIndicators.Adx(higher, options.AdxPeriod);
        var atr = TechnicalIndicators.Atr(higher, options.AtrPeriod);
        var atrPercent = higher[^1].Close > 0m ? atr / higher[^1].Close * 100m : 0m;
        var sign = fast > slow && slow > primary && higher[^1].Close > primary ? 1m
            : fast < slow && slow < primary && higher[^1].Close < primary ? -1m : 0m;
        var recent = medium.TakeLast(options.StructureLookback).ToArray();
        var previous = medium.SkipLast(options.StructureLookback).TakeLast(options.StructureLookback).ToArray();
        var structureSign = recent.Max(c => c.High) > previous.Max(c => c.High) && recent.Min(c => c.Low) > previous.Min(c => c.Low) ? 1m
            : recent.Max(c => c.High) < previous.Max(c => c.High) && recent.Min(c => c.Low) < previous.Min(c => c.Low) ? -1m : 0m;
        var established = adx >= options.MinimumAdx;
        // A rising ADX just below the established threshold needs directional 30m structure and the 1h fast EMA reclaim.
        var emerging = !established && adx >= options.MinimumAdx * 0.75m && sign != 0m && sign == structureSign &&
            sign * (higher[^1].Close - fast) > 0m && adx > TechnicalIndicators.Adx(higher[..^1], options.AdxPeriod);
        var regime = atrPercent > options.MaximumAtrPercent ? MarketRegime.HighVolatility
            : atrPercent < options.MinimumAtrPercent ? MarketRegime.LowVolatility
            : !established && !emerging ? MarketRegime.Sideways
            : sign > 0m ? MarketRegime.TrendingUp : sign < 0m ? MarketRegime.TrendingDown : MarketRegime.Sideways;
        var setupAtr = TechnicalIndicators.Atr(setup, options.AtrPeriod);
        var setupRsi = TechnicalIndicators.Rsi(setup, options.RsiPeriod);
        LastEvaluation = new(time, regime, "market regime does not permit a trend entry", adx, atrPercent, fast, slow, primary, setupRsi,
            structureSign > 0m ? "HH/HL" : structureSign < 0m ? "LH/LL" : "mixed", "MARKET_REGIME_UNSUITABLE", AtrValue: setupAtr);
        if (regime is not (MarketRegime.TrendingUp or MarketRegime.TrendingDown))
        {
            breakout.ResetPending(symbol);
            return null;
        }
        var mediumSlow = TechnicalIndicators.Ema(medium, options.SlowEmaPeriod);
        if (structureSign == -sign || sign * (medium[^1].Close - mediumSlow) <= 0m)
        {
            LastEvaluation = LastEvaluation with { Reason = "30m context opposes the 1h trend", ReasonCode = "TREND_CONFLICT" };
            breakout.ResetPending(symbol);
            return null;
        }
        if (setupAtr <= 0m)
        {
            LastEvaluation = LastEvaluation with { Reason = "15m ATR unavailable", ReasonCode = "ATR_TOO_LOW" };
            breakout.ResetPending(symbol);
            return null;
        }
        if (_lastSignalBySymbol.TryGetValue(symbol, out var lastSignal) &&
            (time <= lastSignal || time - lastSignal < TimeSpan.FromMinutes(5d * options.SignalCooldownBars)))
        {
            LastEvaluation = LastEvaluation with { Reason = "adaptive Current signal cooldown", ReasonCode = "SIGNAL_COOLDOWN_ACTIVE" };
            return null;
        }

        var direction = sign > 0m ? TradeDirection.Long : TradeDirection.Short;
        var breakoutSignal = breakout.Evaluate(symbol, entry, medium, higher, direction);
        LastEvaluation = LastEvaluation with { Reason = breakout.LastEvaluation?.Reason ?? "waiting for qualified breakout",
            ReasonCode = breakout.LastEvaluation?.ReasonCode ?? "NO_VALID_SETUP", SecondaryReasons = breakout.LastEvaluation?.SecondaryReasons,
            RsiEntry = breakout.LastEvaluation?.RsiEntry, VolumeRatio = breakout.LastEvaluation?.VolumeRatio };
        var maxDistance = setupAtr * options.MaximumSetupDistanceAtr;
        if (breakoutSignal is not null)
        {
            var setupFast = TechnicalIndicators.Ema(setup, options.FastEmaPeriod);
            if (sign * (setupRsi - 50m) >= 0m && sign * (current.Close - setupFast) > 0m &&
                Math.Abs(current.Close - setupFast) <= maxDistance)
            {
                _lastSignalBySymbol[symbol] = time;
                LastEvaluation = LastEvaluation with { Reason = "adaptive trend breakout retest confirmed", ReasonCode = null,
                    Setup = emerging ? "emerging-breakout-retest" : "breakout-retest" };
                return breakoutSignal with { Atr = setupAtr, Evaluation = LastEvaluation };
            }
            LastEvaluation = LastEvaluation with { Reason = "retest lacks 15m momentum or is extended", ReasonCode = "SETUP_MOMENTUM_OR_EXTENSION" };
            return null;
        }
        if (!established || LastEvaluation.ReasonCode is "RETEST_PENDING" or "RETEST_QUALITY_REJECTED" or "BREAKOUT_INVALIDATED") return null;

        var reference = TechnicalIndicators.Ema(prior, options.FastEmaPeriod);
        var pullback = prior.TakeLast(3).ToArray();
        var pullbackBoundary = sign > 0m ? pullback.Min(c => c.Low) : pullback.Max(c => c.High);
        var pivot = sign > 0m ? prior[^1].High : prior[^1].Low;
        var breakoutBoundary = sign > 0m ? TechnicalIndicators.HighestHigh(prior, options.BreakoutLookback)
            : TechnicalIndicators.LowestLow(prior, options.BreakoutLookback);
        if (sign * (pullbackBoundary - reference) > 0m || sign * (prior[^1].Close - prior[^1].Open) >= 0m ||
            sign * (current.Close - pivot) <= 0m || sign * (current.Close - breakoutBoundary) > 0m || sign * (current.Close - reference) <= 0m ||
            sign * (current.Close - current.Open) <= 0m || TechnicalIndicators.BodyRatio(current) < options.MinimumBodyRatio)
            return null;

        var rsi = TechnicalIndicators.Rsi(entry, options.RsiPeriod);
        var previousRsi = TechnicalIndicators.Rsi(prior, options.RsiPeriod);
        var averageVolume = TechnicalIndicators.AverageVolume(prior, options.VolumeLookback);
        LastEvaluation = LastEvaluation with { RsiEntry = rsi, VolumeRatio = averageVolume > 0m ? current.Volume / averageVolume : null };
        if (sign * (rsi - 50m) < 0m || sign * (rsi - previousRsi) <= 0m ||
            (direction == TradeDirection.Long ? rsi >= options.RsiOverbought : rsi <= options.RsiOversold))
        {
            LastEvaluation = LastEvaluation with { Reason = "pullback reclaim RSI outside directional band", ReasonCode = "RSI_FILTER_REJECTED" };
            return null;
        }
        if (averageVolume <= 0m || current.Volume <= 0m || current.Volume < prior[^1].Volume ||
            current.Volume >= averageVolume * options.MaximumVolumeMultiplier)
        {
            LastEvaluation = LastEvaluation with { Reason = "pullback reclaim volume outside range", ReasonCode = "PULLBACK_VOLUME_REJECTED" };
            return null;
        }
        if (Math.Abs(current.Close - reference) > maxDistance)
        {
            LastEvaluation = LastEvaluation with { Reason = "pullback reclaim is extended", ReasonCode = "ENTRY_TOO_EXTENDED" };
            return null;
        }

        breakout.ResetPending(symbol);
        _lastSignalBySymbol[symbol] = time;
        var invalidation = sign > 0m ? Math.Min(current.Low, pullbackBoundary) : Math.Max(current.High, pullbackBoundary);
        LastEvaluation = LastEvaluation with { Reason = "adaptive trend pullback reclaimed", ReasonCode = null,
            SecondaryReasons = null, Setup = "pullback-reclaim" };
        return new(symbol, direction, time, current.Close, setupAtr, LastEvaluation.Reason,
            InvalidationPrice: invalidation, Evaluation: LastEvaluation);
    }

    private static bool Contiguous(IEnumerable<Candle> candles, int minutes)
    {
        DateTimeOffset? previousClose = null;
        foreach (var candle in candles)
        {
            if (candle.CloseTime != candle.OpenTime.AddMinutes(minutes) ||
                previousClose is not null && candle.OpenTime != previousClose) return false;
            previousClose = candle.CloseTime;
        }
        return true;
    }
}
