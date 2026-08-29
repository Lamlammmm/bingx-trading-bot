using Microsoft.Extensions.Options;
using Npgsql;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.Persistence;

public sealed class PostgresTradeStore(IOptions<PersistenceOptions> options) : ITradeStore
{
    private readonly string _connectionString = NormalizeConnectionString(options.Value.ConnectionString
        ?? throw new InvalidOperationException("Persistence:ConnectionString is required when Persistence:Provider is Postgres."));

    private static string NormalizeConnectionString(string connectionString)
    {
        if (!connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        var uri = new Uri(connectionString);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            Database = uri.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Require
        };
        return builder.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var createAccountState = connection.CreateCommand();
        createAccountState.CommandText = """
            CREATE TABLE IF NOT EXISTS account_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                balance NUMERIC NOT NULL,
                balance_date DATE NOT NULL,
                realized_pnl_today NUMERIC NOT NULL
            );
            """;
        await createAccountState.ExecuteNonQueryAsync(cancellationToken);

        await using var createClosedTrades = connection.CreateCommand();
        createClosedTrades.CommandText = """
            CREATE TABLE IF NOT EXISTS closed_trades (
                id BIGSERIAL PRIMARY KEY,
                symbol TEXT NOT NULL,
                direction TEXT NOT NULL,
                quantity NUMERIC NOT NULL,
                entry_price NUMERIC NOT NULL,
                exit_price NUMERIC NOT NULL,
                stop_loss NUMERIC NOT NULL,
                take_profit NUMERIC NOT NULL,
                net_pnl NUMERIC NOT NULL,
                exit_reason TEXT NOT NULL,
                source TEXT NOT NULL,
                opened_at TIMESTAMPTZ NOT NULL,
                closed_at TIMESTAMPTZ NOT NULL,
                rsi_15m NUMERIC NULL,
                rsi_1h NUMERIC NULL,
                rsi_4h NUMERIC NULL,
                atr_15m NUMERIC NULL,
                ema_fast_15m NUMERIC NULL,
                ema_slow_15m NUMERIC NULL,
                volume_ratio_15m NUMERIC NULL,
                body_ratio_15m NUMERIC NULL,
                confidence NUMERIC NULL,
                hour_of_day_utc INTEGER NULL,
                day_of_week_utc INTEGER NULL
            );
            """;
        await createClosedTrades.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AccountState?> LoadAccountStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT balance, balance_date, realized_pnl_today FROM account_state WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new AccountState(
            reader.GetDecimal(0),
            DateOnly.FromDateTime(reader.GetDateTime(1)),
            reader.GetDecimal(2));
    }

    public async Task SaveAccountStateAsync(AccountState state, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO account_state (id, balance, balance_date, realized_pnl_today)
            VALUES (1, @balance, @balanceDate, @realizedPnlToday)
            ON CONFLICT (id) DO UPDATE SET
                balance = excluded.balance,
                balance_date = excluded.balance_date,
                realized_pnl_today = excluded.realized_pnl_today;
            """;
        command.Parameters.AddWithValue("balance", state.Balance);
        command.Parameters.AddWithValue("balanceDate", state.BalanceDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("realizedPnlToday", state.RealizedPnlToday);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordClosedTradeAsync(ClosedTrade trade, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO closed_trades
                (symbol, direction, quantity, entry_price, exit_price, stop_loss, take_profit, net_pnl, exit_reason, source, opened_at, closed_at,
                 rsi_15m, rsi_1h, rsi_4h, atr_15m, ema_fast_15m, ema_slow_15m, volume_ratio_15m, body_ratio_15m, confidence, hour_of_day_utc, day_of_week_utc)
            VALUES
                (@symbol, @direction, @quantity, @entryPrice, @exitPrice, @stopLoss, @takeProfit, @netPnl, @exitReason, @source, @openedAt, @closedAt,
                 @rsi15m, @rsi1h, @rsi4h, @atr15m, @emaFast15m, @emaSlow15m, @volumeRatio15m, @bodyRatio15m, @confidence, @hourOfDayUtc, @dayOfWeekUtc);
            """;
        command.Parameters.AddWithValue("symbol", trade.Symbol);
        command.Parameters.AddWithValue("direction", trade.Direction.ToString());
        command.Parameters.AddWithValue("quantity", trade.Quantity);
        command.Parameters.AddWithValue("entryPrice", trade.EntryPrice);
        command.Parameters.AddWithValue("exitPrice", trade.ExitPrice);
        command.Parameters.AddWithValue("stopLoss", trade.StopLoss);
        command.Parameters.AddWithValue("takeProfit", trade.TakeProfit);
        command.Parameters.AddWithValue("netPnl", trade.NetPnl);
        command.Parameters.AddWithValue("exitReason", trade.ExitReason);
        command.Parameters.AddWithValue("source", trade.Source);
        command.Parameters.AddWithValue("openedAt", trade.OpenedAt.UtcDateTime);
        command.Parameters.AddWithValue("closedAt", trade.ClosedAt.UtcDateTime);
        var features = trade.EntryFeatures;
        command.Parameters.AddWithValue("rsi15m", (object?)features?.Rsi15m ?? DBNull.Value);
        command.Parameters.AddWithValue("rsi1h", (object?)features?.Rsi1h ?? DBNull.Value);
        command.Parameters.AddWithValue("rsi4h", (object?)features?.Rsi4h ?? DBNull.Value);
        command.Parameters.AddWithValue("atr15m", (object?)features?.Atr15m ?? DBNull.Value);
        command.Parameters.AddWithValue("emaFast15m", (object?)features?.EmaFast15m ?? DBNull.Value);
        command.Parameters.AddWithValue("emaSlow15m", (object?)features?.EmaSlow15m ?? DBNull.Value);
        command.Parameters.AddWithValue("volumeRatio15m", (object?)features?.VolumeRatio15m ?? DBNull.Value);
        command.Parameters.AddWithValue("bodyRatio15m", (object?)features?.BodyRatio15m ?? DBNull.Value);
        command.Parameters.AddWithValue("confidence", (object?)features?.Confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("hourOfDayUtc", (object?)features?.HourOfDayUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("dayOfWeekUtc", (object?)features?.DayOfWeekUtc ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
