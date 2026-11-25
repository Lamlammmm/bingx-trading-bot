using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public sealed class ExperimentalStrategy(IOptions<StrategyOptions> indicators,
    IOptions<ExperimentalStrategyOptions> options) : ITradingStrategy
{
    public const string Version = "trend-pullback-v1";
    public SignalEvaluation? LastEvaluation { get; private set; }

    public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> entryCandles,
        IReadOnlyList<Candle> mediumTrendCandles, IReadOnlyList<Candle> higherTrendCandles)
    {
        LastEvaluation = null;
        var entry = entryCandles.Where(c => c.IsClosed).OrderBy(c => c.OpenTime).ToArray();
        if (entry.Length == 0) return null;
        var current = entry[^1];
        var time = current.CloseTime;
        var settings = indicators.Value;
        var policy = options.Value;
        LastEvaluation = new(time, MarketRegime.Unknown, "insufficient closed multi-timeframe history",
            ReasonCode: "INSUFFICIENT_HISTORY");
        if (settings.ExcludedSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
        {
            LastEvaluation = LastEvaluation with { Reason = "symbol is excluded", ReasonCode = "SYMBOL_EXCLUDED" };
            return null;
        }

        if (settings.FastEmaPeriod < 2 || settings.SlowEmaPeriod <= settings.FastEmaPeriod ||
            settings.PrimaryEmaPeriod < settings.SlowEmaPeriod || settings.AdxPeriod is < 2 or > 500 ||
            settings.AtrPeriod is < 2 or > 332 || settings.VolumeLookback < 1 || settings.MaximumVolumeMultiplier <= 0m ||
            policy.PullbackBars is < 2 or > 20 || policy.MaximumEntryDistanceAtr <= 0m ||
            policy.MinimumAdx is <= 0m or > 100m || policy.MinimumAtrPercent <= 0m ||
            policy.MaximumAtrPercent <= policy.MinimumAtrPercent)
            throw new InvalidOperationException("Invalid experimental strategy indicator or pullback configuration.");

        var medium = mediumTrendCandles.Where(c => c.IsClosed && c.CloseTime <= time).OrderBy(c => c.OpenTime).ToArray();
        var higher = higherTrendCandles.Where(c => c.IsClosed && c.CloseTime <= time).OrderBy(c => c.OpenTime).ToArray();
        var requiredEntry = Math.Max(3 * (settings.AtrPeriod + 1) + 2,
            1 + Math.Max(settings.FastEmaPeriod, Math.Max(settings.VolumeLookback, policy.PullbackBars)));
        var requiredHigher = Math.Max(settings.PrimaryEmaPeriod, Math.Max(2 * settings.AdxPeriod, settings.AtrPeriod + 1));
        if (entry.Length < requiredEntry || medium.Length < settings.SlowEmaPeriod || higher.Length < requiredHigher)
            return null;
        if (time - medium[^1].CloseTime >= TimeSpan.FromMinutes(30) ||
            time - higher[^1].CloseTime >= TimeSpan.FromHours(1) ||
            !IsContiguous(entry.TakeLast(requiredEntry), TimeSpan.FromMinutes(5)) ||
            !IsContiguous(medium.TakeLast(settings.SlowEmaPeriod), TimeSpan.FromMinutes(30)) ||
            !IsContiguous(higher.TakeLast(requiredHigher), TimeSpan.FromHours(1)))
        {
            LastEvaluation = LastEvaluation with { Reason = "stale or gapped market data", ReasonCode = "STALE_MARKET_DATA" };
            return null;
        }

        var fast = TechnicalIndicators.Ema(higher, settings.FastEmaPeriod);
        var slow = TechnicalIndicators.Ema(higher, settings.SlowEmaPeriod);
        var primary = TechnicalIndicators.Ema(higher, settings.PrimaryEmaPeriod);
        var adx = TechnicalIndicators.Adx(higher, settings.AdxPeriod);
        var atr = TechnicalIndicators.Atr(higher, settings.AtrPeriod);
        var atrPercent = higher[^1].Close > 0m ? atr / higher[^1].Close * 100m : 0m;
        var regime = atrPercent > policy.MaximumAtrPercent ? MarketRegime.HighVolatility
            : atrPercent < policy.MinimumAtrPercent ? MarketRegime.LowVolatility
            : adx < policy.MinimumAdx ? MarketRegime.Sideways
            : fast > slow && slow > primary && higher[^1].Close > fast ? MarketRegime.TrendingUp
            : fast < slow && slow < primary && higher[^1].Close < fast ? MarketRegime.TrendingDown
            : MarketRegime.Sideways;
        LastEvaluation = new(time, regime, "market regime does not permit a trend entry", adx, atrPercent, fast, slow, primary,
            ReasonCode: regime == MarketRegime.LowVolatility ? "ATR_TOO_LOW" : "MARKET_REGIME_UNSUITABLE", AtrValue: atr);
        if (regime is not (MarketRegime.TrendingUp or MarketRegime.TrendingDown)) return null;

        var sign = regime == MarketRegime.TrendingUp ? 1m : -1m;
        var mediumFast = TechnicalIndicators.Ema(medium, settings.FastEmaPeriod);
        var mediumSlow = TechnicalIndicators.Ema(medium, settings.SlowEmaPeriod);
        if (sign * (mediumFast - mediumSlow) <= 0m || sign * (medium[^1].Close - mediumFast) <= 0m)
        {
            LastEvaluation = LastEvaluation with { Reason = "30m trend conflicts with 1h", ReasonCode = "NO_TREND_ALIGNMENT" };
            return null;
        }

        var prior = entry[..^1];
        var reference = TechnicalIndicators.Ema(prior, settings.FastEmaPeriod);
        var pullback = prior.TakeLast(policy.PullbackBars).ToArray();
        var previous = prior[^1];
        var pivot = sign > 0m ? previous.High : previous.Low;
        var invalidation = sign > 0m ? Math.Min(current.Low, pullback.Min(c => c.Low))
            : Math.Max(current.High, pullback.Max(c => c.High));
        if (sign * (invalidation - reference) > 0m || sign * (previous.Close - previous.Open) >= 0m ||
            sign * (current.Close - pivot) <= 0m || sign * (current.Close - reference) <= 0m ||
            sign * (current.Close - current.Open) <= 0m || TechnicalIndicators.BodyRatio(current) < settings.MinimumBodyRatio)
        {
            LastEvaluation = LastEvaluation with { Reason = "waiting for 5m trend pullback and reclaim", ReasonCode = "NO_VALID_SETUP" };
            return null;
        }

        var averageVolume = TechnicalIndicators.AverageVolume(prior, settings.VolumeLookback);
        var volumeRatio = averageVolume > 0m ? current.Volume / averageVolume : (decimal?)null;
        LastEvaluation = LastEvaluation with { VolumeRatio = volumeRatio };
        if (averageVolume <= 0m || current.Volume <= 0m || current.Volume >= averageVolume * settings.MaximumVolumeMultiplier)
        {
            LastEvaluation = LastEvaluation with { Reason = "pullback reclaim volume is zero or abnormal",
                ReasonCode = averageVolume <= 0m || current.Volume <= 0m ? "LOW_VOLUME" : "VOLUME_SPIKE" };
            return null;
        }

        // Use only 15m bars that were complete before this trigger bar.
        var setup = TechnicalIndicators.AggregateClosed(prior, 15);
        var setupAtr = TechnicalIndicators.Atr(setup, settings.AtrPeriod);
        if (setupAtr <= 0m || Math.Abs(current.Close - reference) > setupAtr * policy.MaximumEntryDistanceAtr)
        {
            LastEvaluation = LastEvaluation with { Reason = "15m ATR unavailable or pullback entry is extended",
                ReasonCode = setupAtr <= 0m ? "ATR_TOO_LOW" : "ENTRY_TOO_EXTENDED" };
            return null;
        }

        LastEvaluation = LastEvaluation with { Reason = "5m trend pullback reclaimed", ReasonCode = null,
            Structure = sign > 0m ? "swing low" : "swing high" };
        return new StrategySignal(symbol, sign > 0m ? TradeDirection.Long : TradeDirection.Short, time,
            current.Close, setupAtr, LastEvaluation.Reason, Source: Version,
            InvalidationPrice: invalidation, Evaluation: LastEvaluation);
    }

    private static bool IsContiguous(IEnumerable<Candle> candles, TimeSpan duration)
    {
        DateTimeOffset? previousClose = null;
        foreach (var candle in candles)
        {
            if (candle.CloseTime - candle.OpenTime != duration ||
                previousClose is not null && candle.OpenTime != previousClose) return false;
            previousClose = candle.CloseTime;
        }
        return true;
    }
}
