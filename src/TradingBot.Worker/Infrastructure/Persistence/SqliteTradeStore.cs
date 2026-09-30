using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.Persistence;

public sealed class SqliteTradeStore(IOptions<PersistenceOptions> options) : ITradeStore, ILiveTradeJournal
{
    private readonly string _databasePath = options.Value.DatabasePath;


    public async Task InitializeJournalAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS live_trade_events (
                event_id TEXT PRIMARY KEY,
                trade_id TEXT NOT NULL,
                symbol TEXT NOT NULL,
                kind TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_live_trade_events_trade ON live_trade_events(trade_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordLiveEventAsync(LiveTradeEvent entry, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(entry.PayloadJson);
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO live_trade_events(event_id, trade_id, symbol, kind, occurred_at, payload_json)
            VALUES (@id, @trade, @symbol, @kind, @time, @payload)
            ON CONFLICT(event_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@id", entry.EventId);
        command.Parameters.AddWithValue("@trade", entry.TradeId);
        command.Parameters.AddWithValue("@symbol", entry.Symbol);
        command.Parameters.AddWithValue("@kind", entry.Kind);
        command.Parameters.AddWithValue("@time", entry.OccurredAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@payload", entry.PayloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async IAsyncEnumerable<LiveTradeEvent> ReadLiveEventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, trade_id, symbol, kind, occurred_at, payload_json FROM live_trade_events ORDER BY occurred_at, event_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            yield return new LiveTradeEvent(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(5));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var createAccountState = connection.CreateCommand();
        createAccountState.CommandText = """
            CREATE TABLE IF NOT EXISTS AccountState (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Balance TEXT NOT NULL,
                BalanceDate TEXT NOT NULL,
                RealizedPnlToday TEXT NOT NULL,
                TotalWins INTEGER NOT NULL DEFAULT 0,
                TotalLosses INTEGER NOT NULL DEFAULT 0,
                TotalNetPnl TEXT NOT NULL DEFAULT '0'
            );
            """;
        await createAccountState.ExecuteNonQueryAsync(cancellationToken);

        await using var accountColumnsCommand = connection.CreateCommand();
        accountColumnsCommand.CommandText = "PRAGMA table_info(AccountState);";
        var accountColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await accountColumnsCommand.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) accountColumns.Add(reader.GetString(1));
        foreach (var (name, sqlType) in new[] { ("TotalWins", "INTEGER NOT NULL DEFAULT 0"), ("TotalLosses", "INTEGER NOT NULL DEFAULT 0"), ("TotalNetPnl", "TEXT NOT NULL DEFAULT '0'") })
        {
            if (accountColumns.Contains(name)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE AccountState ADD COLUMN {name} {sqlType};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var createOpenPositions = connection.CreateCommand();
        createOpenPositions.CommandText = """
            CREATE TABLE IF NOT EXISTS OpenPositions (
                Symbol TEXT PRIMARY KEY,
                Direction TEXT NOT NULL,
                Quantity TEXT NOT NULL,
                EntryPrice TEXT NOT NULL,
                StopLoss TEXT NOT NULL,
                TakeProfit TEXT NOT NULL,
                RiskAmount TEXT NOT NULL,
                EntryFee TEXT NOT NULL,
                OpenedAt TEXT NOT NULL,
                EntryFeatures TEXT NULL
            );
            """;
        await createOpenPositions.ExecuteNonQueryAsync(cancellationToken);

        await using var openPositionColumnsCommand = connection.CreateCommand();
        openPositionColumnsCommand.CommandText = "PRAGMA table_info(OpenPositions);";
        var openPositionColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await openPositionColumnsCommand.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) openPositionColumns.Add(reader.GetString(1));
        foreach (var (name, sqlType) in new[]
        {
            ("InitialStopLoss", "TEXT"), ("PartialTaken", "INTEGER NOT NULL DEFAULT 0"),
            ("InitialQuantity", "TEXT"), ("RealizedPartialPnl", "TEXT NOT NULL DEFAULT '0'")
        })
        {
            if (openPositionColumns.Contains(name)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE OpenPositions ADD COLUMN {name} {sqlType};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var backfillInitialStopLoss = connection.CreateCommand();
        backfillInitialStopLoss.CommandText = "UPDATE OpenPositions SET InitialStopLoss = StopLoss WHERE InitialStopLoss IS NULL;";
        await backfillInitialStopLoss.ExecuteNonQueryAsync(cancellationToken);
        await using var backfillInitialQuantity = connection.CreateCommand();
        backfillInitialQuantity.CommandText = "UPDATE OpenPositions SET InitialQuantity = Quantity WHERE InitialQuantity IS NULL;";
        await backfillInitialQuantity.ExecuteNonQueryAsync(cancellationToken);

        await using var createClosedTrades = connection.CreateCommand();
        createClosedTrades.CommandText = """
            CREATE TABLE IF NOT EXISTS ClosedTrades (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Symbol TEXT NOT NULL,
                Direction TEXT NOT NULL,
                Quantity TEXT NOT NULL,
                EntryPrice TEXT NOT NULL,
                ExitPrice TEXT NOT NULL,
                StopLoss TEXT NOT NULL,
                TakeProfit TEXT NOT NULL,
                NetPnl TEXT NOT NULL,
                ExitReason TEXT NOT NULL,
                Source TEXT NOT NULL,
                OpenedAt TEXT NOT NULL,
                ClosedAt TEXT NOT NULL,
                Rsi5m TEXT NULL,
                Rsi15m TEXT NULL,
                Rsi1h TEXT NULL,
                Atr5m TEXT NULL,
                EmaFast5m TEXT NULL,
                EmaSlow5m TEXT NULL,
                VolumeRatio5m TEXT NULL,
                BodyRatio5m TEXT NULL,
                Confidence TEXT NULL,
                HourOfDayUtc INTEGER NULL,
                DayOfWeekUtc INTEGER NULL
            );
            """;
        await createClosedTrades.ExecuteNonQueryAsync(cancellationToken);

        await using var existingColumnsCommand = connection.CreateCommand();
        existingColumnsCommand.CommandText = "PRAGMA table_info(ClosedTrades);";
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await existingColumnsCommand.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) existingColumns.Add(reader.GetString(1));

        var featureColumns = new (string Name, string SqlType)[]
        {
            ("Rsi5m", "TEXT"), ("Rsi15m", "TEXT"), ("Rsi1h", "TEXT"), ("Atr5m", "TEXT"),
            ("EmaFast5m", "TEXT"), ("EmaSlow5m", "TEXT"), ("VolumeRatio5m", "TEXT"),
            ("BodyRatio5m", "TEXT"), ("Confidence", "TEXT"), ("HourOfDayUtc", "INTEGER"), ("DayOfWeekUtc", "INTEGER"),
        };
        foreach (var (name, sqlType) in featureColumns)
        {
            if (existingColumns.Contains(name)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE ClosedTrades ADD COLUMN {name} {sqlType} NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<AccountState?> LoadAccountStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Balance, BalanceDate, RealizedPnlToday, TotalWins, TotalLosses, TotalNetPnl FROM AccountState WHERE Id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new AccountState(
            decimal.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
            DateOnly.Parse(reader.GetString(1)),
            decimal.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetInt32(3),
            reader.GetInt32(4),
            decimal.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task SaveAccountStateAsync(AccountState state, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AccountState (Id, Balance, BalanceDate, RealizedPnlToday, TotalWins, TotalLosses, TotalNetPnl)
            VALUES (1, $balance, $balanceDate, $realizedPnlToday, $totalWins, $totalLosses, $totalNetPnl)
            ON CONFLICT(Id) DO UPDATE SET
                Balance = excluded.Balance,
                BalanceDate = excluded.BalanceDate,
                RealizedPnlToday = excluded.RealizedPnlToday,
                TotalWins = excluded.TotalWins,
                TotalLosses = excluded.TotalLosses,
                TotalNetPnl = excluded.TotalNetPnl;
            """;
        command.Parameters.AddWithValue("$balance", state.Balance.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$balanceDate", state.BalanceDate.ToString("O"));
        command.Parameters.AddWithValue("$realizedPnlToday", state.RealizedPnlToday.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$totalWins", state.TotalWins);
        command.Parameters.AddWithValue("$totalLosses", state.TotalLosses);
        command.Parameters.AddWithValue("$totalNetPnl", state.TotalNetPnl.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TradePerformance?> LoadTradePerformanceAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT NetPnl FROM ClosedTrades;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var wins = 0;
        var losses = 0;
        var totalNetPnl = 0m;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        while (await reader.ReadAsync(cancellationToken))
        {
            var netPnl = decimal.Parse(reader.GetString(0), invariant);
            if (netPnl >= 0m) wins++; else losses++;
            totalNetPnl += netPnl;
        }
        return wins + losses == 0 ? null : new TradePerformance(wins, losses, totalNetPnl);
    }

    public async IAsyncEnumerable<StoredPaperTrade> ReadClosedTradesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Symbol, Direction, Quantity, EntryPrice, ExitPrice, StopLoss, TakeProfit,
                   NetPnl, ExitReason, Source, OpenedAt, ClosedAt, Rsi5m, Rsi15m, Rsi1h,
                   Atr5m, EmaFast5m, EmaSlow5m, VolumeRatio5m, BodyRatio5m, Confidence,
                   HourOfDayUtc, DayOfWeekUtc
            FROM ClosedTrades ORDER BY Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        while (await reader.ReadAsync(cancellationToken))
        {
            var features = new StoredTradeFeatures(
                reader.IsDBNull(13) ? null : decimal.Parse(reader.GetString(13), invariant),
                reader.IsDBNull(14) ? null : decimal.Parse(reader.GetString(14), invariant),
                reader.IsDBNull(15) ? null : decimal.Parse(reader.GetString(15), invariant),
                reader.IsDBNull(16) ? null : decimal.Parse(reader.GetString(16), invariant),
                reader.IsDBNull(17) ? null : decimal.Parse(reader.GetString(17), invariant),
                reader.IsDBNull(18) ? null : decimal.Parse(reader.GetString(18), invariant),
                reader.IsDBNull(19) ? null : decimal.Parse(reader.GetString(19), invariant),
                reader.IsDBNull(20) ? null : decimal.Parse(reader.GetString(20), invariant),
                reader.IsDBNull(21) ? null : decimal.Parse(reader.GetString(21), invariant),
                reader.IsDBNull(22) ? null : reader.GetInt32(22), reader.IsDBNull(23) ? null : reader.GetInt32(23));
            var trade = new ClosedTrade(reader.GetString(1), Enum.Parse<TradeDirection>(reader.GetString(2)),
                decimal.Parse(reader.GetString(3), invariant), decimal.Parse(reader.GetString(4), invariant),
                decimal.Parse(reader.GetString(5), invariant), decimal.Parse(reader.GetString(6), invariant),
                decimal.Parse(reader.GetString(7), invariant), decimal.Parse(reader.GetString(8), invariant),
                reader.GetString(9), reader.GetString(10), DateTimeOffset.Parse(reader.GetString(11), invariant),
                DateTimeOffset.Parse(reader.GetString(12), invariant));
            yield return new StoredPaperTrade(reader.GetInt64(0), trade, features);
        }
    }

    public async Task<IReadOnlyList<PaperPosition>> LoadOpenPositionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Symbol, Direction, Quantity, EntryPrice, StopLoss, TakeProfit, RiskAmount, EntryFee, OpenedAt, EntryFeatures, InitialStopLoss, PartialTaken, InitialQuantity, RealizedPartialPnl FROM OpenPositions;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var positions = new List<PaperPosition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var featuresJson = reader.IsDBNull(9) ? null : reader.GetString(9);
            var stopLoss = decimal.Parse(reader.GetString(4), invariant);
            var initialStopLoss = reader.IsDBNull(10) ? stopLoss : decimal.Parse(reader.GetString(10), invariant);
            var partialTaken = !reader.IsDBNull(11) && reader.GetInt64(11) != 0;
            positions.Add(new PaperPosition(reader.GetString(0), Enum.Parse<TradeDirection>(reader.GetString(1)),
                decimal.Parse(reader.GetString(2), invariant),
                decimal.Parse(reader.GetString(3), invariant),
                stopLoss,
                decimal.Parse(reader.GetString(5), invariant),
                decimal.Parse(reader.GetString(6), invariant),
                decimal.Parse(reader.GetString(7), invariant),
                DateTimeOffset.Parse(reader.GetString(8), invariant),
                featuresJson is null ? null : JsonSerializer.Deserialize<TradeFeatures>(featuresJson),
                initialStopLoss, partialTaken,
                reader.IsDBNull(12) ? decimal.Parse(reader.GetString(2), invariant) : decimal.Parse(reader.GetString(12), invariant),
                reader.IsDBNull(13) ? 0m : decimal.Parse(reader.GetString(13), invariant)));
        }
        return positions;
    }

    public async Task SaveOpenPositionsAsync(IReadOnlyCollection<PaperPosition> positions, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM OpenPositions;";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var position in positions)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO OpenPositions
                    (Symbol, Direction, Quantity, EntryPrice, StopLoss, TakeProfit, RiskAmount, EntryFee, OpenedAt, EntryFeatures, InitialStopLoss, PartialTaken, InitialQuantity, RealizedPartialPnl)
                VALUES ($symbol, $direction, $quantity, $entryPrice, $stopLoss, $takeProfit, $riskAmount, $entryFee, $openedAt, $entryFeatures, $initialStopLoss, $partialTaken, $initialQuantity, $realizedPartialPnl);
                """;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            insert.Parameters.AddWithValue("$symbol", position.Symbol);
            insert.Parameters.AddWithValue("$direction", position.Direction.ToString());
            insert.Parameters.AddWithValue("$quantity", position.Quantity.ToString(invariant));
            insert.Parameters.AddWithValue("$entryPrice", position.EntryPrice.ToString(invariant));
            insert.Parameters.AddWithValue("$stopLoss", position.StopLoss.ToString(invariant));
            insert.Parameters.AddWithValue("$takeProfit", position.TakeProfit.ToString(invariant));
            insert.Parameters.AddWithValue("$riskAmount", position.RiskAmount.ToString(invariant));
            insert.Parameters.AddWithValue("$entryFee", position.EntryFee.ToString(invariant));
            insert.Parameters.AddWithValue("$openedAt", position.OpenedAt.ToString("O"));
            insert.Parameters.AddWithValue("$entryFeatures", (object?)JsonSerializer.Serialize(position.EntryFeatures) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$initialStopLoss", (position.InitialStopLoss == 0m ? position.StopLoss : position.InitialStopLoss).ToString(invariant));
            insert.Parameters.AddWithValue("$partialTaken", position.PartialTaken ? 1 : 0);
            insert.Parameters.AddWithValue("$initialQuantity", (position.InitialQuantity == 0m ? position.Quantity : position.InitialQuantity).ToString(invariant));
            insert.Parameters.AddWithValue("$realizedPartialPnl", position.RealizedPartialPnl.ToString(invariant));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordClosedTradeAsync(ClosedTrade trade, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ClosedTrades
                (Symbol, Direction, Quantity, EntryPrice, ExitPrice, StopLoss, TakeProfit, NetPnl, ExitReason, Source, OpenedAt, ClosedAt,
                 Rsi5m, Rsi15m, Rsi1h, Atr5m, EmaFast5m, EmaSlow5m, VolumeRatio5m, BodyRatio5m, Confidence, HourOfDayUtc, DayOfWeekUtc)
            VALUES
                ($symbol, $direction, $quantity, $entryPrice, $exitPrice, $stopLoss, $takeProfit, $netPnl, $exitReason, $source, $openedAt, $closedAt,
                 $rsi5m, $rsi15m, $rsi1h, $atr5m, $emaFast5m, $emaSlow5m, $volumeRatio5m, $bodyRatio5m, $confidence, $hourOfDayUtc, $dayOfWeekUtc);
            """;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        command.Parameters.AddWithValue("$symbol", trade.Symbol);
        command.Parameters.AddWithValue("$direction", trade.Direction.ToString());
        command.Parameters.AddWithValue("$quantity", trade.Quantity.ToString(invariant));
        command.Parameters.AddWithValue("$entryPrice", trade.EntryPrice.ToString(invariant));
        command.Parameters.AddWithValue("$exitPrice", trade.ExitPrice.ToString(invariant));
        command.Parameters.AddWithValue("$stopLoss", trade.StopLoss.ToString(invariant));
        command.Parameters.AddWithValue("$takeProfit", trade.TakeProfit.ToString(invariant));
        command.Parameters.AddWithValue("$netPnl", trade.NetPnl.ToString(invariant));
        command.Parameters.AddWithValue("$exitReason", trade.ExitReason);
        command.Parameters.AddWithValue("$source", trade.Source);
        command.Parameters.AddWithValue("$openedAt", trade.OpenedAt.ToString("O"));
        command.Parameters.AddWithValue("$closedAt", trade.ClosedAt.ToString("O"));
        var features = trade.EntryFeatures;
        command.Parameters.AddWithValue("$rsi5m", (object?)features?.Rsi5m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$rsi15m", (object?)features?.Rsi15m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$rsi1h", (object?)features?.Rsi1h.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$atr5m", (object?)features?.Atr5m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$emaFast5m", (object?)features?.EmaFast5m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$emaSlow5m", (object?)features?.EmaSlow5m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$volumeRatio5m", (object?)features?.VolumeRatio5m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$bodyRatio5m", (object?)features?.BodyRatio5m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$confidence", (object?)features?.Confidence.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$hourOfDayUtc", (object?)features?.HourOfDayUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$dayOfWeekUtc", (object?)features?.DayOfWeekUtc ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string BuildConnectionString() => new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
}
