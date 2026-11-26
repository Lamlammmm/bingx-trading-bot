using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public static class PerformanceAnalyzer
{
    public static IReadOnlyList<PerformanceSummary> Analyze(TradingMode mode, IEnumerable<TradingOutcome> outcomes, decimal initialCapital)
    {
        var trades = outcomes.Where(t => t.Mode == mode).GroupBy(t => t.TradeId)
            .Select(g => g.Last()).Where(t => t.Reconciled).OrderBy(t => t.ClosedAt).ToArray();
        var groups = new Dictionary<string, TradingOutcome[]> { ["all"] = trades };
        foreach (var grouping in new Func<TradingOutcome, string>[] { t => "strategy:" + t.Strategy.Version,
            t => "regime:" + t.Regime, t => "symbol:" + t.Symbol + "/" + t.TimeFrame, t => "exit:" + t.ExitReason })
            foreach (var group in trades.GroupBy(grouping)) groups[group.Key] = group.ToArray();
        return groups.Select(pair =>
        {
            var rows = pair.Value;
            var wins = rows.Where(t => t.NetPnl > 0m).ToArray();
            var losses = rows.Where(t => t.NetPnl < 0m).ToArray();
            var lossSum = -losses.Sum(t => t.NetPnl);
            var equity = initialCapital; var peak = equity; var dd = 0m; var ddPercent = 0m;
            var streak = 0; var maxStreak = 0;
            foreach (var t in rows)
            {
                equity += t.NetPnl; peak = Math.Max(peak, equity); dd = Math.Max(dd, peak - equity);
                if (peak > 0m) ddPercent = Math.Max(ddPercent, (peak - equity) / peak * 100m);
                streak = t.NetPnl < 0m ? streak + 1 : 0; maxStreak = Math.Max(maxStreak, streak);
            }
            var favorableMoney = rows.Sum(t => t.Mfe * t.Quantity);
            return new PerformanceSummary(mode, pair.Key, rows.Length, wins.Length, losses.Length,
                rows.Length == 0 ? null : wins.Length / (decimal)rows.Length,
                lossSum == 0m ? null : wins.Sum(t => t.NetPnl) / lossSum,
                rows.Length == 0 ? null : rows.Average(t => t.NetPnl),
                rows.Where(t => t.RealizedR.HasValue).Select(t => t.RealizedR).DefaultIfEmpty(null).Average(),
                rows.Sum(t => t.NetPnl), dd, initialCapital > 0m ? ddPercent : null, maxStreak,
                rows.Sum(t => t.TradingFees), rows.Sum(t => t.FundingFees),
                rows.Length == 0 ? 0m : rows.Average(t => (decimal)(t.ClosedAt - t.OpenedAt).TotalMinutes),
                rows.Select(t => t.SlippageBasisPoints).DefaultIfEmpty(null).Average(),
                rows.Length == 0 ? null : rows.Average(t => t.Mfe), rows.Length == 0 ? null : rows.Average(t => t.Mae),
                favorableMoney <= 0m ? null : rows.Sum(t => t.NetPnl) / favorableMoney,
                rows.Select(t => t.EntryLatencyMilliseconds).DefaultIfEmpty(null).Average(),
                rows.Select(t => t.ExitSlippageBasisPoints).DefaultIfEmpty(null).Average());
        }).ToArray();
    }

    public static AdaptiveDecision Decide(TradingConfiguration config, PerformanceSummary summary, decimal? spreadBasisPoints, bool halted)
    {
        if (halted) return new(false, 0m, "persistent kill switch");
        if (!config.Adaptive.Enabled) return new(true, 1m, "adaptive statistics disabled; core risk limits remain active");
        if (spreadBasisPoints is null || spreadBasisPoints < 0m || spreadBasisPoints > config.Adaptive.MaximumSpreadBasisPoints)
            return new(false, 0m, "spread is missing or above the approved limit");
        if (summary.Mode != TradingMode.LIVE && summary.MaximumDrawdownPercent >= config.Risk.MaxDrawdownPercent)
            return new(false, 0m, "realized drawdown limit reached");
        if (summary.TradeCount >= config.Adaptive.MinimumTrades && summary.NetExpectancy <= 0m &&
            (summary.ProfitFactor is null || summary.ProfitFactor < config.Adaptive.MinimumProfitFactor))
            return new(true, config.Adaptive.DegradedRiskMultiplier, "validated sample window shows degradation; risk reduced only");
        return new(true, 1m, "approved market/risk policy");
    }
}
