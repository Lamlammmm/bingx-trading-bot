using TradingBot.Worker.Infrastructure.Persistence;

namespace TradingBot.Worker.Infrastructure;

public sealed class SignalDiagnosticsQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? Window { get; init; }
    public string? Symbol { get; init; }
    public string? Timeframe { get; init; }
    public string? Mode { get; init; }
    public string? Strategy { get; init; }
    public string? Version { get; init; }
    public string? Regime { get; init; }
    public string? Decision { get; init; }
    public int? Limit { get; init; }
    public int? Offset { get; init; }
}

public static class SignalDiagnosticsApi
{
    public static void MapSignalDiagnostics(this WebApplication host)
    {
        host.MapGet("/api/diagnostics/signals/{view}", async (string view, [AsParameters] SignalDiagnosticsQuery query,
            Func<SignalScanStore> store, SignalDiagnostics diagnostics, CancellationToken token) =>
        {
            if (view is not ("history" or "overview")) return Results.NotFound();
            var days = query.Window switch { "24h" => 1, null or "7d" => 7, "30d" => 30, _ => 0 };
            var to = query.To ?? DateTimeOffset.UtcNow;
            var from = query.From ?? (to.ToUniversalTime() >= DateTimeOffset.MinValue.AddDays(days)
                ? to.ToUniversalTime().AddDays(-days) : DateTimeOffset.MinValue);
            var limit = query.Limit ?? 100;
            var offset = query.Offset ?? 0;
            if (days == 0 || to <= from || to - from > TimeSpan.FromDays(90) ||
                limit is < 1 or > 500 || offset is < 0 or > 1_000_000)
                return Results.BadRequest(new { Error = "Use window 24h, 7d or 30d; or from/to up to 90 days. Limit 1–500, offset 0–1000000." });
            var filter = new SignalScanFilter(from, to, query.Symbol, query.Timeframe, query.Mode,
                query.Strategy, query.Version, query.Regime, query.Decision);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                return view == "history"
                    ? Results.Ok(new { Limit = limit, Offset = offset, Items = await store().HistoryAsync(filter, limit, offset, deadline.Token) })
                    : Results.Ok(new { diagnostics.Enabled, diagnostics.FallbackCount, Report = await store().OverviewAsync(filter, deadline.Token) });
            }
            catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException ||
                (error is OperationCanceledException && !token.IsCancellationRequested))
            {
                return Results.Problem("Signal diagnostics storage is unavailable.", statusCode: 503);
            }
        });
    }
}
