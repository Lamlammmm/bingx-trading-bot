using System.Text.Json;
using System.Threading.Channels;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Persistence;

namespace TradingBot.Worker.Infrastructure;

public sealed class SignalDiagnostics(Func<SignalScanStore> store, IConfiguration configuration,
    ILogger<SignalDiagnostics> logger) : BackgroundService
{
    private readonly Channel<SignalScan> _queue = Channel.CreateBounded<SignalScan>(new BoundedChannelOptions(2048)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly bool _enabled = configuration.GetValue("SignalDiagnostics:Enabled", true);
    private readonly int _retentionDays = Math.Clamp(configuration.GetValue("SignalDiagnostics:RetentionDays", 35), 1, 90);
    private long _fallbackCount;

    public long FallbackCount => Interlocked.Read(ref _fallbackCount);
    public bool Enabled => _enabled;

    public SignalScan? Begin(MarketUpdate update, string mode, string strategy, string version) =>
        _enabled && update.TimeFrame == TimeFrame.FiveMinutes && update.Candle.IsClosed
            ? new SignalScan(update, mode, strategy, version) : null;

    public void Record(SignalScan scan)
    {
        if (!_queue.Writer.TryWrite(scan)) Fallback(scan);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var initialized = false;
        var nextRetention = DateTimeOffset.MinValue;
        var batch = new List<SignalScan>(64);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    if (!initialized)
                    {
                        await store().InitializeAsync(deadline.Token);
                        initialized = true;
                    }
                    // Bound the work per tick, including while catching up after an outage.
                    for (var i = 0; i < 4; i++)
                    {
                        while (batch.Count < 64 && _queue.Reader.TryRead(out var scan)) batch.Add(scan);
                        if (batch.Count == 0) break;
                        await store().AppendAsync(batch, deadline.Token);
                        batch.Clear();
                    }
                    if (DateTimeOffset.UtcNow >= nextRetention)
                    {
                        await store().RetainAsync(DateTimeOffset.UtcNow.AddDays(-_retentionDays), deadline.Token);
                        nextRetention = DateTimeOffset.UtcNow.AddHours(1);
                    }
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Signal diagnostics persistence unavailable ({ErrorType}); retrying next tick", error.GetType().Name);
                    // The same scan IDs make retry idempotent if a database commit was ambiguous.
                    initialized = false;
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _queue.Writer.TryComplete();
            foreach (var scan in batch) Fallback(scan);
            while (_queue.Reader.TryRead(out var scan)) Fallback(scan);
        }
    }

    private void Fallback(SignalScan scan)
    {
        Interlocked.Increment(ref _fallbackCount);
        logger.LogWarning("SIGNAL DIAGNOSTICS FALLBACK {SignalScan}", JsonSerializer.Serialize(scan, SignalScanStore.Json));
    }
}
