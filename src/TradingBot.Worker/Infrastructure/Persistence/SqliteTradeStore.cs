using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.Persistence;

public sealed class SqliteTradeStore(IOptions<PersistenceOptions> options) : ITradeStore
{
    private readonly string _databasePath = options.Value.DatabasePath;

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

    public async Task<IReadOnlyList<PaperPosition>> LoadOpenPositionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Symbol, Direction, Quantity, EntryPrice, StopLoss, TakeProfit, RiskAmount, EntryFee, OpenedAt, EntryFeatures FROM OpenPositions;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var positions = new List<PaperPosition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var featuresJson = reader.IsDBNull(9) ? null : reader.GetString(9);
            positions.Add(new PaperPosition(reader.GetString(0), Enum.Parse<TradeDirection>(reader.GetString(1)),
                decimal.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                featuresJson is null ? null : JsonSerializer.Deserialize<TradeFeatures>(featuresJson)));
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
                    (Symbol, Direction, Quantity, EntryPrice, StopLoss, TakeProfit, RiskAmount, EntryFee, OpenedAt, EntryFeatures)
                VALUES ($symbol, $direction, $quantity, $entryPrice, $stopLoss, $takeProfit, $riskAmount, $entryFee, $openedAt, $entryFeatures);
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
