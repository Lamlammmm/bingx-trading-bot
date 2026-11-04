using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure;

public sealed class BotReadinessHealthCheck(BotRuntimeState state, ICandleStore candles,
    IOptions<BotOptions> botOptions, IOptions<BingXOptions> bingXOptions,
    IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot;
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-10);
        var ready = botOptions.Value.Enabled && snapshot.Initialized && !snapshot.HasProcessingError &&
            !lifetime.ApplicationStopping.IsCancellationRequested && snapshot.LastProcessedAt >= cutoff &&
            bingXOptions.Value.Symbols.All(symbol => candles.Get(symbol, TimeFrame.FiveMinutes)
                .Any(candle => candle.IsClosed && candle.CloseTime >= cutoff && candle.CloseTime <= now.AddSeconds(10)));
        return Task.FromResult(ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
    }
}
