using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

// An opt-in research filter around the existing entry logic. No invented signal score.
public sealed class MultiTimeframeStrategy(IOptions<StrategyOptions> options) : ITradingStrategy
{
    public const string AdaptiveVersion = "current-adaptive-v1";
    private readonly StrategyOptions _options = options.Value;
    private readonly BreakoutStrategy _entry = new(options);
    private AdaptiveCurrentEntryPolicy? _adaptive;
    public SignalEvaluation? LastEvaluation { get; private set; }

    public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles)
    {
        if (_options.UseAdaptiveCurrentEntries)
        {
            _adaptive ??= new AdaptiveCurrentEntryPolicy(_options, _entry);
            var adaptiveSignal = _adaptive.Evaluate(symbol, entryCandles, mediumTrendCandles, higherTrendCandles);
            LastEvaluation = _adaptive.LastEvaluation;
            return adaptiveSignal;
        }
        var entry = entryCandles.Where(c => c.IsClosed).OrderBy(c => c.OpenTime).ToArray();
        LastEvaluation = null;
        if (entry.Length == 0) return null;
        var time = entry[^1].CloseTime;
        var medium = mediumTrendCandles.Where(c => c.IsClosed && c.CloseTime <= time).OrderBy(c => c.OpenTime).ToArray();
        var higher = higherTrendCandles.Where(c => c.IsClosed && c.CloseTime <= time).OrderBy(c => c.OpenTime).ToArray();
        var setup = TechnicalIndicators.AggregateClosed(entry, 15);
        LastEvaluation = new(time, MarketRegime.Unknown, "insufficient closed multi-timeframe history", ReasonCode: "INSUFFICIENT_HISTORY");
        if (_options.StructureLookback < 2 || _options.FastEmaPeriod < 2 || _options.SlowEmaPeriod <= _options.FastEmaPeriod ||
            _options.AdxPeriod < 2 || _options.AtrPeriod < 2 || _options.RsiPeriod < 2 ||
            _options.PrimaryEmaPeriod < _options.SlowEmaPeriod ||
            higher.Length < Math.Max(_options.PrimaryEmaPeriod, 2 * _options.AdxPeriod) ||
            medium.Length < Math.Max(_options.SlowEmaPeriod, 2 * _options.StructureLookback) || setup.Count < Math.Max(_options.SlowEmaPeriod, 2 * _options.StructureLookback)) return null;
        if (time - medium[^1].CloseTime >= TimeSpan.FromMinutes(30) || time - higher[^1].CloseTime >= TimeSpan.FromHours(1) ||
            time - setup[^1].CloseTime >= TimeSpan.FromMinutes(15) ||
            entry.TakeLast(60).Zip(entry.TakeLast(60).Skip(1), (a, b) => b.OpenTime == a.OpenTime.AddMinutes(5)).Any(valid => !valid))
        {
            LastEvaluation = LastEvaluation with { Reason = "stale or gapped market data", ReasonCode = "STALE_MARKET_DATA" };
            return null;
        }
        var fast = TechnicalIndicators.Ema(higher, _options.FastEmaPeriod);
        var slow = TechnicalIndicators.Ema(higher, _options.SlowEmaPeriod);
        var primary = TechnicalIndicators.Ema(higher, _options.PrimaryEmaPeriod);
        var adx = TechnicalIndicators.Adx(higher, _options.AdxPeriod);
        var atr = TechnicalIndicators.Atr(higher, _options.AtrPeriod);
        var atrPercent = higher[^1].Close <= 0m ? 0m : atr / higher[^1].Close * 100m;
        var bullish = fast > slow && slow > primary && higher[^1].Close > fast;
        var bearish = fast < slow && slow < primary && higher[^1].Close < fast;
        var regime = atrPercent > _options.MaximumAtrPercent ? MarketRegime.HighVolatility
            : atrPercent < _options.MinimumAtrPercent ? MarketRegime.LowVolatility
            : adx < _options.MinimumAdx ? MarketRegime.Sideways
            : bullish ? MarketRegime.TrendingUp : bearish ? MarketRegime.TrendingDown : MarketRegime.Sideways;
        var rsi = TechnicalIndicators.Rsi(setup, _options.RsiPeriod);
        // Past-only rolling structure; no pivot confirmed using future bars.
        var recent = medium.TakeLast(_options.StructureLookback).ToArray();
        var previous = medium.SkipLast(_options.StructureLookback).TakeLast(_options.StructureLookback).ToArray();
        var structure = recent.Max(c => c.High) > previous.Max(c => c.High) && recent.Min(c => c.Low) > previous.Min(c => c.Low) ? "HH/HL"
            : recent.Max(c => c.High) < previous.Max(c => c.High) && recent.Min(c => c.Low) < previous.Min(c => c.Low) ? "LH/LL" : "mixed";
        LastEvaluation = new(time, regime, "market regime does not permit a trend entry", adx, atrPercent, fast, slow, primary, rsi, structure,
            ReasonCode: "MARKET_REGIME_UNSUITABLE");
        if (regime is not (MarketRegime.TrendingUp or MarketRegime.TrendingDown)) return null;
        var sign = regime == MarketRegime.TrendingUp ? 1m : -1m;
        var mediumFast = TechnicalIndicators.Ema(medium, _options.FastEmaPeriod);
        var mediumSlow = TechnicalIndicators.Ema(medium, _options.SlowEmaPeriod);
        var setupFast = TechnicalIndicators.Ema(setup, _options.FastEmaPeriod);
        var setupAtr = TechnicalIndicators.Atr(setup, _options.AtrPeriod);
        if (sign * (mediumFast - mediumSlow) <= 0m || sign * (medium[^1].Close - mediumFast) <= 0m ||
            structure != (sign > 0m ? "HH/HL" : "LH/LL"))
        {
            LastEvaluation = LastEvaluation with { Reason = "30m trend or rolling structure conflicts with 1h",
                ReasonCode = "TREND_CONFLICT", AtrValue = setupAtr };
            return null;
        }
        if (sign * (setup[^1].Close - setupFast) <= 0m || (sign > 0m ? rsi < 50m : rsi > 50m) ||
            setupAtr <= 0m || Math.Abs(entry[^1].Close - setupFast) > setupAtr * _options.MaximumSetupDistanceAtr)
        {
            LastEvaluation = LastEvaluation with { Reason = "15m setup lacks momentum or entry is extended",
                ReasonCode = "SETUP_MOMENTUM_OR_EXTENSION", AtrValue = setupAtr };
            return null;
        }
        var signal = _entry.Evaluate(symbol, entry, medium, higher);
        var directionMatches = signal?.Direction == (sign > 0m ? TradeDirection.Long : TradeDirection.Short);
        LastEvaluation = LastEvaluation with
        {
            Reason = directionMatches ? "multi-timeframe breakout/retest confirmed" : "waiting for qualified 5m breakout/retest or cooldown",
            ReasonCode = directionMatches ? null : signal is null ? _entry.LastEvaluation?.ReasonCode : "TREND_CONFLICT",
            SecondaryReasons = _entry.LastEvaluation?.SecondaryReasons,
            AtrValue = setupAtr, RsiEntry = _entry.LastEvaluation?.RsiEntry, VolumeRatio = _entry.LastEvaluation?.VolumeRatio
        };
        if (signal is null || !directionMatches) return null;
        var setupBoundary = sign > 0m ? setup.TakeLast(_options.StructureLookback).Min(c => c.Low)
            : setup.TakeLast(_options.StructureLookback).Max(c => c.High);
        return signal with { Atr = setupAtr,
            InvalidationPrice = sign > 0m ? Math.Min(signal.InvalidationPrice ?? setupBoundary, setupBoundary)
                : Math.Max(signal.InvalidationPrice ?? setupBoundary, setupBoundary),
            Evaluation = LastEvaluation, Reason = $"{LastEvaluation.Reason}; regime={regime}; structure30m={structure}; ADX1h={adx:0.##}; RSI15m={rsi:0.##}; risk uses ATR15m and setup swing; {signal.Reason}" };
    }
}
