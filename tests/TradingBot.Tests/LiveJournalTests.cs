using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Persistence;

namespace TradingBot.Tests;

public sealed class LiveJournalTests
{
    [Fact]
    public async Task JournalSurvivesReopenDeduplicatesAndDoesNotAffectPaperPerformance()
    {
        var path = Path.Combine(Path.GetTempPath(), "live-journal-" + Guid.NewGuid() + ".db");
        try
        {
            var options = Options.Create(new PersistenceOptions { DatabasePath = path });
            var store = new SqliteTradeStore(options);
            await store.InitializeAsync(default);
            await store.InitializeJournalAsync(default);
            var opened = new LiveTradeEvent("trade:entry", "trade", "SOL-USDT", "entry_requested",
                DateTimeOffset.UtcNow, "{\"Reason\":\"retest\",\"Quantity\":0.03}");
            await store.RecordLiveEventAsync(opened, default);
            await store.RecordLiveEventAsync(opened, default);
            await store.RecordLiveEventAsync(opened with { EventId = "trade:close", Kind = "closed_observed", PayloadJson = "{\"NetPnl\":null}" }, default);
            var reopened = new SqliteTradeStore(options);
            await reopened.InitializeJournalAsync(default);
            var events = new List<LiveTradeEvent>();
            await foreach (var entry in reopened.ReadLiveEventsAsync(default)) events.Add(entry);
            Assert.Equal(2, events.Count);
            Assert.Equal(opened, Assert.Single(events.Where(e => e.EventId == opened.EventId)));
            Assert.Null(await reopened.LoadTradePerformanceAsync(default));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
