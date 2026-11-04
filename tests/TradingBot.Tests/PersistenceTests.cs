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
                0.5m, 0.01m, DateTimeOffset.UtcNow, new TradeFeatures(55m, 60m, 65m, 2m, 101m, 99m, 1.4m, 0.7m, 0m, 10, 2),
                InitialStopLoss: 97m, PartialTaken: true, InitialQuantity: 0.02m, RealizedPartialPnl: 0.75m);
            await store.SaveOpenPositionsAsync(new[] { position }, CancellationToken.None);
            await store.RecordClosedTradeAsync(new ClosedTrade("BTC-USDT", TradeDirection.Long, 0.01m,
                100m, 106m, 97m, 106m, 0.75m, "TP", "paper", DateTimeOffset.UtcNow.AddHours(-1),
                DateTimeOffset.UtcNow), CancellationToken.None);
            await store.RecordClosedTradeAsync(new ClosedTrade("ETH-USDT", TradeDirection.Short, 0.1m,
                100m, 103m, 103m, 94m, -0.4m, "SL", "paper", DateTimeOffset.UtcNow.AddHours(-1),
                DateTimeOffset.UtcNow), CancellationToken.None);
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE ClosedTrades SET Rsi15m = '55.5' WHERE Id = 1;";
                await command.ExecuteNonQueryAsync();
            }

            var reopenedStore = new SqliteTradeStore(options);
            await reopenedStore.InitializeAsync(CancellationToken.None);
            var account = await reopenedStore.LoadAccountStateAsync(CancellationToken.None);
            var positions = await reopenedStore.LoadOpenPositionsAsync(CancellationToken.None);
            var performance = await reopenedStore.LoadTradePerformanceAsync(CancellationToken.None);
            var closed = new List<StoredPaperTrade>();
            await foreach (var row in reopenedStore.ReadClosedTradesAsync(CancellationToken.None)) closed.Add(row);

            Assert.NotNull(account);
            Assert.Equal(99.5m, account.Balance);
            Assert.Equal(3, account.TotalWins);
            Assert.Equal(2, account.TotalLosses);
            Assert.Equal(1.25m, account.TotalNetPnl);
            var restored = Assert.Single(positions);
            Assert.Equal(position.Symbol, restored.Symbol);
            Assert.Equal(position.EntryPrice, restored.EntryPrice);
            Assert.Equal(position.EntryFeatures, restored.EntryFeatures);
            Assert.Equal(position.InitialQuantity, restored.InitialQuantity);
            Assert.Equal(position.RealizedPartialPnl, restored.RealizedPartialPnl);
            Assert.Equal(new TradePerformance(1, 1, 0.35m), performance);
            Assert.Equal(2, closed.Count);
            Assert.Equal("BTC-USDT", closed[0].Trade.Symbol);
            Assert.Equal(0.75m, closed[0].Trade.NetPnl);
            Assert.Null(closed[0].Features.Rsi5m);
            Assert.Equal(55.5m, closed[0].Features.Rsi15m);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
