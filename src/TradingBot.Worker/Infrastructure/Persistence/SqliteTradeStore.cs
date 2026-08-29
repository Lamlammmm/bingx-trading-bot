using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
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
                RealizedPnlToday TEXT NOT NULL
            );
            """;
        await createAccountState.ExecuteNonQueryAsync(cancellationToken);

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
                Rsi15m TEXT NULL,
                Rsi1h TEXT NULL,
                Rsi4h TEXT NULL,
                Atr15m TEXT NULL,
                EmaFast15m TEXT NULL,
                EmaSlow15m TEXT NULL,
                VolumeRatio15m TEXT NULL,
                BodyRatio15m TEXT NULL,
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
            ("Rsi15m", "TEXT"), ("Rsi1h", "TEXT"), ("Rsi4h", "TEXT"), ("Atr15m", "TEXT"),
            ("EmaFast15m", "TEXT"), ("EmaSlow15m", "TEXT"), ("VolumeRatio15m", "TEXT"),
            ("BodyRatio15m", "TEXT"), ("Confidence", "TEXT"), ("HourOfDayUtc", "INTEGER"), ("DayOfWeekUtc", "INTEGER"),
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
        command.CommandText = "SELECT Balance, BalanceDate, RealizedPnlToday FROM AccountState WHERE Id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new AccountState(
            decimal.Parse(reader.GetString(0)),
            DateOnly.Parse(reader.GetString(1)),
            decimal.Parse(reader.GetString(2)));
    }

    public async Task SaveAccountStateAsync(AccountState state, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AccountState (Id, Balance, BalanceDate, RealizedPnlToday)
            VALUES (1, $balance, $balanceDate, $realizedPnlToday)
            ON CONFLICT(Id) DO UPDATE SET
                Balance = excluded.Balance,
                BalanceDate = excluded.BalanceDate,
                RealizedPnlToday = excluded.RealizedPnlToday;
            """;
        command.Parameters.AddWithValue("$balance", state.Balance.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$balanceDate", state.BalanceDate.ToString("O"));
        command.Parameters.AddWithValue("$realizedPnlToday", state.RealizedPnlToday.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordClosedTradeAsync(ClosedTrade trade, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(BuildConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ClosedTrades
                (Symbol, Direction, Quantity, EntryPrice, ExitPrice, StopLoss, TakeProfit, NetPnl, ExitReason, Source, OpenedAt, ClosedAt,
                 Rsi15m, Rsi1h, Rsi4h, Atr15m, EmaFast15m, EmaSlow15m, VolumeRatio15m, BodyRatio15m, Confidence, HourOfDayUtc, DayOfWeekUtc)
            VALUES
                ($symbol, $direction, $quantity, $entryPrice, $exitPrice, $stopLoss, $takeProfit, $netPnl, $exitReason, $source, $openedAt, $closedAt,
                 $rsi15m, $rsi1h, $rsi4h, $atr15m, $emaFast15m, $emaSlow15m, $volumeRatio15m, $bodyRatio15m, $confidence, $hourOfDayUtc, $dayOfWeekUtc);
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
        command.Parameters.AddWithValue("$rsi15m", (object?)features?.Rsi15m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$rsi1h", (object?)features?.Rsi1h.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$rsi4h", (object?)features?.Rsi4h.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$atr15m", (object?)features?.Atr15m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$emaFast15m", (object?)features?.EmaFast15m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$emaSlow15m", (object?)features?.EmaSlow15m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$volumeRatio15m", (object?)features?.VolumeRatio15m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$bodyRatio15m", (object?)features?.BodyRatio15m.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$confidence", (object?)features?.Confidence.ToString(invariant) ?? DBNull.Value);
        command.Parameters.AddWithValue("$hourOfDayUtc", (object?)features?.HourOfDayUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$dayOfWeekUtc", (object?)features?.DayOfWeekUtc ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string BuildConnectionString() => new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
}
