using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Persistence;

namespace TradingBot.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task SqliteStore_PersistsAccountStatisticsAndOpenPosition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bingx-tradingbot-{Guid.NewGuid():N}.db");
        try
        {
            var options = Options.Create(new PersistenceOptions { DatabasePath = path });
            var store = new SqliteTradeStore(options);
            await store.InitializeAsync(CancellationToken.None);
            await store.SaveAccountStateAsync(new AccountState(99.5m, DateOnly.FromDateTime(DateTime.UtcNow),
                -0.5m, 3, 2, 1.25m), CancellationToken.None);
            var position = new PaperPosition("BTC-USDT", TradeDirection.Long, 0.01m, 100m, 97m, 106m,
                0.5m, 0.01m, DateTimeOffset.UtcNow, new TradeFeatures(55m, 60m, 65m, 2m, 101m, 99m, 1.4m, 0.7m, 0m, 10, 2));
            await store.SaveOpenPositionsAsync(new[] { position }, CancellationToken.None);

            var reopenedStore = new SqliteTradeStore(options);
            await reopenedStore.InitializeAsync(CancellationToken.None);
            var account = await reopenedStore.LoadAccountStateAsync(CancellationToken.None);
            var positions = await reopenedStore.LoadOpenPositionsAsync(CancellationToken.None);

            Assert.NotNull(account);
            Assert.Equal(99.5m, account.Balance);
            Assert.Equal(3, account.TotalWins);
            Assert.Equal(2, account.TotalLosses);
            Assert.Equal(1.25m, account.TotalNetPnl);
            var restored = Assert.Single(positions);
            Assert.Equal(position.Symbol, restored.Symbol);
            Assert.Equal(position.EntryPrice, restored.EntryPrice);
            Assert.Equal(position.EntryFeatures, restored.EntryFeatures);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
