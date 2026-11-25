using System.Data.Common;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Live;
using TradingBot.Worker.Infrastructure.Persistence;

namespace TradingBot.Worker.Infrastructure;

public static class LiveTradingMetricsApi
{
    private const int LookbackDays = 30;

    public static void MapLiveTradingMetrics(this WebApplication host)
    {
        host.MapGet("/api/bot/live/performance", async (DateOnly? date, IOptions<TradingFrequencyOptions> frequency,
            IOptions<BotOptions> bot, IOptions<LiveOptions> live, SignalDiagnostics diagnostics,
            Func<SignalScanStore> scans, ICandleStore candles, IServiceProvider services, CancellationToken token) =>
        {
            var options = frequency.Value;
            if (options.ForceEntryToMeetTarget || options.TargetEntriesPerDay < 1 || string.IsNullOrWhiteSpace(options.TimeZone) ||
                !TimeZoneInfo.TryFindSystemTimeZoneById(options.TimeZone, out var zone))
                return Results.Problem("Invalid TradingFrequency configuration. Disable ForceEntryToMeetTarget and use a positive target and supported time zone.", statusCode: 503);

            var now = DateTimeOffset.UtcNow;
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            var day = date ?? localDate;
            if (day > localDate || day < localDate.AddDays(1 - LookbackDays))
                return Results.BadRequest("Choose a date in the last 30 calendar days.");
            var from = Midnight(day, zone);
            var to = day == localDate ? now : Midnight(day.AddDays(1), zone);
            var since = Midnight(day.AddDays(1 - LookbackDays), zone);
            var frequencyEnd = day < localDate ? to : from;
            var frequencyStart = day < localDate ? since : Midnight(day.AddDays(-LookbackDays), zone);
            var liveEnabled = bot.Value.Enabled && live.Value.Enabled;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var ct = deadline.Token;
            try
            {
                var governance = services.GetRequiredService<TradingGovernance>();
                var store = governance.Store;
                // Read the previous local day as well so a fill and a later protection check can straddle midnight.
                var entrySince = day < localDate ? Midnight(day.AddDays(-LookbackDays), zone) : Midnight(day.AddDays(-LookbackDays - 1), zone);
                var fills = new Dictionary<string, DateTimeOffset>();
                foreach (var kind in new[] { "entry_filled", "entry_protected" })
                    await foreach (var entry in store.ReadAsync(TradingMode.LIVE, entrySince, ct, kind))
                        fills.TryAdd(entry.TradeId, entry.OccurredAt);
                var targetEntries = fills;
                if (!options.CountOnlyFilledEntries)
                {
                    targetEntries = new(fills);
                    await foreach (var entry in store.ReadAsync(TradingMode.LIVE, entrySince, ct, "entry_response"))
                        if (entry.OccurredAt < targetEntries.GetValueOrDefault(entry.TradeId, DateTimeOffset.MaxValue))
                            targetEntries[entry.TradeId] = entry.OccurredAt;
                }

                var outcomes = (await governance.OutcomesAsync(TradingMode.LIVE, since, ct)).Where(t => t.ClosedAt < to).ToArray();
                var verified = outcomes.Where(t => t.Reconciled).ToArray();
                var pending = outcomes.Where(t => !t.Reconciled).ToDictionary(t => t.TradeId, t => t.ClosedAt);
                var verifiedIds = verified.Select(t => t.TradeId).ToHashSet();
                await foreach (var entry in store.ReadAsync(TradingMode.LIVE, since, ct, "closed_observed"))
                    if (entry.OccurredAt < to && !verifiedIds.Contains(entry.TradeId)) pending.TryAdd(entry.TradeId, entry.OccurredAt);
                var dailyOutcomes = verified.Where(t => t.ClosedAt >= from).ToArray();
                var dailyPending = pending.Values.Count(t => t >= from);
                var summary = PerformanceAnalyzer.Analyze(TradingMode.LIVE, verified, verified.FirstOrDefault()?.EquityAtEntry ?? 0m)[0];

                IReadOnlyList<LiveScanActivity>? activity = null;
                if (diagnostics.Enabled)
                {
                    // A deployment can have no signal diagnostics yet, while the Live journal is readable.
                    try { activity = await scans().LiveActivityAsync(from, to, ct); }
                    catch (DbException) { }
                }
                var rejections = activity?.Where(a => a.Decision == "REJECTED" && a.Reason is not null)
                    .GroupBy(a => (a.Stage, a.Reason)).Select(g => new { g.Key.Stage, g.Key.Reason, Count = g.Sum(a => a.Count) })
                    .OrderByDescending(r => r.Count).ThenBy(r => r.Reason).ToArray();
                var dailyFilled = fills.Values.Count(t => t >= from && t < to);
                var currentTargetEntries = targetEntries.Values.Count(t => t >= from && t < to);
                bool? targetMet = options.Enabled ? currentTargetEntries >= options.TargetEntriesPerDay : null;
                string? followUp = null;
                if (targetMet == false)
                {
                    if (activity is null || activity.Count == 0)
                        followUp = "Check Live and signal diagnostics coverage for the date.";
                    else if (activity.Any(a => a.HasError || a.Execution is "REJECTED" or "UNKNOWN" || a.Reason == "PENDING_ORDER_RECONCILIATION"))
                        followUp = "Review exchange errors and reconcile ambiguous order outcomes before tuning signals.";
                    else if (rejections?.FirstOrDefault() is { } top)
                        followUp = top.Stage == "STRATEGY" && top.Reason != "MACD_FILTER_REJECTED"
                            ? $"Replay the {top.Reason} cases out of sample and compare valid setups after costs."
                            : $"Review the {top.Reason} guard against its configuration and runtime state.";
                    else followUp = "Review the execution and fill journal for accepted setups.";
                }
                var latestPositions = liveEnabled ? services.GetRequiredService<LiveTradingEngine>().PositionsForMonitoring : null;
                var marks = latestPositions?.Select(position =>
                {
                    var mark = candles.Get(position.Symbol, TimeFrame.FiveMinutes)
                        .LastOrDefault(c => c.IsClosed && c.Close > 0m && c.CloseTime <= now && c.CloseTime >= now.AddMinutes(-10));
                    return new { position.Symbol, Direction = position.Direction.ToString(), position.Quantity, ReferencePrice = mark?.Close, ReferenceAt = mark?.CloseTime,
                        GrossNotionalUsdt = mark?.Close * position.Quantity };
                }).ToArray();

                return Results.Ok(new
                {
                    Date = day, options.TimeZone, From = from, To = to, DayComplete = day < localDate, LiveEnabled = liveEnabled,
                    DailyFilledEntries = dailyFilled,
                    DailyTradingTargetAchievement = new
                    {
                        options.Enabled, options.TargetEntriesPerDay, options.CountOnlyFilledEntries,
                        CountedEntries = options.Enabled ? (int?)currentTargetEntries : null, Met = targetMet,
                        ForceEntryToMeetTarget = false,
                        Basis = options.CountOnlyFilledEntries ? "confirmed entry" : "acknowledged entry or confirmed entry"
                    },
                    DailyNetPnlUsdt = dailyPending == 0 && dailyOutcomes.Length > 0 ? (decimal?)dailyOutcomes.Sum(t => t.NetPnl) : null,
                    DailyTradingFeesUsdt = dailyPending == 0 && dailyOutcomes.Length > 0 ? (decimal?)dailyOutcomes.Sum(t => t.TradingFees) : null,
                    DailyReconciledCloses = dailyOutcomes.Length, DailyUnreconciledCloses = dailyPending,
                    PnlBasis = "Reconciled bot trades, assigned by observed close time. Includes fees and funding across each trade's lifetime.",
                    Performance = new
                    {
                        From = since, To = to, ReconciledCloses = summary.TradeCount, UnreconciledCloses = pending.Count,
                        summary.WinRate, NetExpectancyUsdt = summary.NetExpectancy,
                        TradingFeesUsdt = summary.TradeCount > 0 ? (decimal?)summary.Fees : null,
                        MaxRealizedDrawdownUsdt = pending.Count == 0 && summary.TradeCount > 0 ? (decimal?)summary.MaximumDrawdownAmount : null,
                        MaxRealizedDrawdownPercent = pending.Count == 0 ? summary.MaximumDrawdownPercent : null,
                        ConsecutiveLosses = pending.Count == 0 && summary.TradeCount > 0
                            ? (int?)verified.Reverse().TakeWhile(t => t.NetPnl < 0m).Count() : null,
                        MaximumConsecutiveLosses = pending.Count == 0 && summary.TradeCount > 0 ? (int?)summary.MaximumConsecutiveLosses : null
                    },
                    FrequencyWindow = new
                    {
                        From = frequencyStart, To = frequencyEnd, CalendarDays = LookbackDays,
                        AverageFilledEntriesPerCalendarDay = fills.Values.Count(t => t >= frequencyStart && t < frequencyEnd) / (decimal)LookbackDays
                    },
                    SignalDiagnostics = new
                    {
                        diagnostics.Enabled, diagnostics.FallbackCount, MarketScans = activity?.Sum(a => a.Count),
                        AcceptedSignals = activity?.Where(a => a.Decision == "ACCEPTED").Sum(a => a.Count),
                        OrderAttempts = activity?.Where(a => a.Execution != "NOT_ATTEMPTED").Sum(a => a.Count),
                        ExchangeRejections = activity?.Where(a => a.Execution == "REJECTED").Sum(a => a.Count),
                        UnknownOrderOutcomes = activity?.Where(a => a.Execution == "UNKNOWN").Sum(a => a.Count),
                        Errors = activity?.Where(a => a.HasError).Sum(a => a.Count), RejectionReasons = rejections
                    },
                    Exposure = new
                    {
                        AsOf = now, Positions = marks,
                        Basis = "Tracked bot positions valued at the latest closed 5m candle, up to 10 minutes old. Excludes external positions and pending orders.",
                        TotalGrossNotionalUsdt = marks?.All(p => p.GrossNotionalUsdt.HasValue) == true
                            ? (decimal?)marks.Sum(p => p.GrossNotionalUsdt!.Value) : null
                    },
                    FollowUp = followUp
                });
            }
            catch (Exception error) when (error is DbException or InvalidOperationException or System.Text.Json.JsonException ||
                error is OperationCanceledException && !token.IsCancellationRequested)
            {
                return Results.Problem("Live performance data is unavailable.", statusCode: 503);
            }
        });
    }

    private static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var time = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(time)) time = time.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(time) ? zone.GetAmbiguousTimeOffsets(time).Max() : zone.GetUtcOffset(time);
        return new DateTimeOffset(time, offset).ToUniversalTime();
    }
}
