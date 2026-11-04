namespace TradingBot.Worker.Application;

public sealed record BotSnapshot(bool Initialized = false, long ProcessedUpdates = 0,
    DateTimeOffset? LastProcessedAt = null, DateTimeOffset? LastCandleCloseTime = null,
    string? LastSymbol = null, bool HasProcessingError = false);

public sealed class BotRuntimeState
{
    private BotSnapshot _snapshot = new();

    // TradingWorker is the single writer; HTTP readers see one immutable snapshot.
    public BotSnapshot Snapshot
    {
        get => Volatile.Read(ref _snapshot);
        set => Volatile.Write(ref _snapshot, value);
    }
}
