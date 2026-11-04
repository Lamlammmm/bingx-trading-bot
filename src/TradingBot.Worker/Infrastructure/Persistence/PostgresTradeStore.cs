using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.Persistence;

public sealed class PostgresTradeStore(IOptions<PersistenceOptions> options) : ITradeStore, ILiveTradeJournal
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


    public async Task InitializeJournalAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
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
        await using var connection = new NpgsqlConnection(_connectionString);
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
        await using var connection = new NpgsqlConnection(_connectionString);
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
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var createAccountState = connection.CreateCommand();
        createAccountState.CommandText = """
            CREATE TABLE IF NOT EXISTS account_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                balance NUMERIC NOT NULL,
                balance_date DATE NOT NULL,
                realized_pnl_today NUMERIC NOT NULL,
                total_wins INTEGER NOT NULL DEFAULT 0,
                total_losses INTEGER NOT NULL DEFAULT 0,
                total_net_pnl NUMERIC NOT NULL DEFAULT 0
            );
            """;
        await createAccountState.ExecuteNonQueryAsync(cancellationToken);

        await using var alterAccountState = connection.CreateCommand();
        alterAccountState.CommandText = """
            ALTER TABLE account_state ADD COLUMN IF NOT EXISTS total_wins INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE account_state ADD COLUMN IF NOT EXISTS total_losses INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE account_state ADD COLUMN IF NOT EXISTS total_net_pnl NUMERIC NOT NULL DEFAULT 0;
            """;
        await alterAccountState.ExecuteNonQueryAsync(cancellationToken);

        await using var createOpenPositions = connection.CreateCommand();
        createOpenPositions.CommandText = """
            CREATE TABLE IF NOT EXISTS open_positions (
                symbol TEXT PRIMARY KEY,
                direction TEXT NOT NULL,
                quantity NUMERIC NOT NULL,
                entry_price NUMERIC NOT NULL,
                stop_loss NUMERIC NOT NULL,
                take_profit NUMERIC NOT NULL,
                risk_amount NUMERIC NOT NULL,
                entry_fee NUMERIC NOT NULL,
                opened_at TIMESTAMPTZ NOT NULL,
                entry_features TEXT NULL
            );
            """;
        await createOpenPositions.ExecuteNonQueryAsync(cancellationToken);

        await using var alterOpenPositions = connection.CreateCommand();
        alterOpenPositions.CommandText = """
            ALTER TABLE open_positions ADD COLUMN IF NOT EXISTS initial_stop_loss NUMERIC NULL;
            ALTER TABLE open_positions ADD COLUMN IF NOT EXISTS partial_taken BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE open_positions ADD COLUMN IF NOT EXISTS initial_quantity NUMERIC NULL;
            ALTER TABLE open_positions ADD COLUMN IF NOT EXISTS realized_partial_pnl NUMERIC NOT NULL DEFAULT 0;
            UPDATE open_positions SET initial_stop_loss = stop_loss WHERE initial_stop_loss IS NULL;
            UPDATE open_positions SET initial_quantity = quantity WHERE initial_quantity IS NULL;
            """;
        await alterOpenPositions.ExecuteNonQueryAsync(cancellationToken);

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
                rsi_5m NUMERIC NULL,
                rsi_15m NUMERIC NULL,
                rsi_1h NUMERIC NULL,
                atr_5m NUMERIC NULL,
                ema_fast_5m NUMERIC NULL,
                ema_slow_5m NUMERIC NULL,
                volume_ratio_5m NUMERIC NULL,
                body_ratio_5m NUMERIC NULL,
                confidence NUMERIC NULL,
                hour_of_day_utc INTEGER NULL,
                day_of_week_utc INTEGER NULL
            );
            """;
        await createClosedTrades.ExecuteNonQueryAsync(cancellationToken);

        await using var alterClosedTrades = connection.CreateCommand();
        alterClosedTrades.CommandText = """
            ALTER TABLE closed_trades ADD COLUMN IF NOT EXISTS rsi_5m NUMERIC NULL;
            ALTER TABLE closed_trades ADD COLUMN IF NOT EXISTS atr_5m NUMERIC NULL;
            ALTER TABLE closed_trades ADD COLUMN IF NOT EXISTS ema_fast_5m NUMERIC NULL;
            ALTER TABLE closed_trades ADD COLUMN IF NOT EXISTS ema_slow_5m NUMERIC NULL;
            ALTER TABLE closed_trades ADD COLUMN IF NOT EXISTS volume_ratio_5m NUMERIC NULL;
            ALTER TABLE closed_trades ADD COLUMN IF NOT EXISTS body_ratio_5m NUMERIC NULL;
            """;
        await alterClosedTrades.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AccountState?> LoadAccountStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT balance, balance_date, realized_pnl_today, total_wins, total_losses, total_net_pnl FROM account_state WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new AccountState(
            reader.GetDecimal(0),
            DateOnly.FromDateTime(reader.GetDateTime(1)),
            reader.GetDecimal(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetDecimal(5));
    }

    public async Task SaveAccountStateAsync(AccountState state, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO account_state (id, balance, balance_date, realized_pnl_today, total_wins, total_losses, total_net_pnl)
            VALUES (1, @balance, @balanceDate, @realizedPnlToday, @totalWins, @totalLosses, @totalNetPnl)
            ON CONFLICT (id) DO UPDATE SET
                balance = excluded.balance,
                balance_date = excluded.balance_date,
                realized_pnl_today = excluded.realized_pnl_today,
                total_wins = excluded.total_wins,
                total_losses = excluded.total_losses,
                total_net_pnl = excluded.total_net_pnl;
            """;
        command.Parameters.AddWithValue("balance", state.Balance);
        command.Parameters.AddWithValue("balanceDate", state.BalanceDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("realizedPnlToday", state.RealizedPnlToday);
        command.Parameters.AddWithValue("totalWins", state.TotalWins);
        command.Parameters.AddWithValue("totalLosses", state.TotalLosses);
        command.Parameters.AddWithValue("totalNetPnl", state.TotalNetPnl);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TradePerformance?> LoadTradePerformanceAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FILTER (WHERE net_pnl >= 0)::integer,
                   COUNT(*) FILTER (WHERE net_pnl < 0)::integer,
                   COALESCE(SUM(net_pnl), 0)::text
            FROM closed_trades;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var wins = reader.GetInt32(0);
        var losses = reader.GetInt32(1);
        return wins + losses == 0 ? null : new TradePerformance(wins, losses,
            decimal.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture));
    }

    public async IAsyncEnumerable<StoredPaperTrade> ReadClosedTradesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, symbol, direction, quantity::text, entry_price::text, exit_price::text,
                   stop_loss::text, take_profit::text, net_pnl::text, exit_reason, source,
                   opened_at, closed_at, rsi_5m::text, rsi_15m::text, rsi_1h::text,
                   atr_5m::text, ema_fast_5m::text, ema_slow_5m::text,
                   volume_ratio_5m::text, body_ratio_5m::text, confidence::text,
                   hour_of_day_utc, day_of_week_utc
            FROM closed_trades ORDER BY id;
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
                reader.GetString(9), reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11),
                reader.GetFieldValue<DateTimeOffset>(12));
            yield return new StoredPaperTrade(reader.GetInt64(0), trade, features);
        }
    }

    public async Task<IReadOnlyList<PaperPosition>> LoadOpenPositionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT symbol, direction, quantity, entry_price, stop_loss, take_profit, risk_amount, entry_fee, opened_at, entry_features, initial_stop_loss, partial_taken, initial_quantity, realized_partial_pnl FROM open_positions;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var positions = new List<PaperPosition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var featuresJson = reader.IsDBNull(9) ? null : reader.GetString(9);
            var stopLoss = reader.GetDecimal(4);
            positions.Add(new PaperPosition(reader.GetString(0), Enum.Parse<TradeDirection>(reader.GetString(1)),
                reader.GetDecimal(2), reader.GetDecimal(3), stopLoss, reader.GetDecimal(5),
                reader.GetDecimal(6), reader.GetDecimal(7), reader.GetFieldValue<DateTimeOffset>(8),
                featuresJson is null ? null : JsonSerializer.Deserialize<TradeFeatures>(featuresJson),
                reader.IsDBNull(10) ? stopLoss : reader.GetDecimal(10), reader.GetBoolean(11),
                reader.IsDBNull(12) ? reader.GetDecimal(2) : reader.GetDecimal(12), reader.GetDecimal(13)));
        }
        return positions;
    }

    public async Task SaveOpenPositionsAsync(IReadOnlyCollection<PaperPosition> positions, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM open_positions;";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var position in positions)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO open_positions
                    (symbol, direction, quantity, entry_price, stop_loss, take_profit, risk_amount, entry_fee, opened_at, entry_features, initial_stop_loss, partial_taken, initial_quantity, realized_partial_pnl)
                VALUES (@symbol, @direction, @quantity, @entryPrice, @stopLoss, @takeProfit, @riskAmount, @entryFee, @openedAt, @entryFeatures, @initialStopLoss, @partialTaken, @initialQuantity, @realizedPartialPnl);
                """;
            insert.Parameters.AddWithValue("symbol", position.Symbol);
            insert.Parameters.AddWithValue("direction", position.Direction.ToString());
            insert.Parameters.AddWithValue("quantity", position.Quantity);
            insert.Parameters.AddWithValue("entryPrice", position.EntryPrice);
            insert.Parameters.AddWithValue("stopLoss", position.StopLoss);
            insert.Parameters.AddWithValue("takeProfit", position.TakeProfit);
            insert.Parameters.AddWithValue("riskAmount", position.RiskAmount);
            insert.Parameters.AddWithValue("entryFee", position.EntryFee);
            insert.Parameters.AddWithValue("openedAt", position.OpenedAt.UtcDateTime);
            insert.Parameters.AddWithValue("entryFeatures", (object?)JsonSerializer.Serialize(position.EntryFeatures) ?? DBNull.Value);
            insert.Parameters.AddWithValue("initialStopLoss", position.InitialStopLoss == 0m ? position.StopLoss : position.InitialStopLoss);
            insert.Parameters.AddWithValue("partialTaken", position.PartialTaken);
            insert.Parameters.AddWithValue("initialQuantity", position.InitialQuantity == 0m ? position.Quantity : position.InitialQuantity);
            insert.Parameters.AddWithValue("realizedPartialPnl", position.RealizedPartialPnl);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordClosedTradeAsync(ClosedTrade trade, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO closed_trades
                (symbol, direction, quantity, entry_price, exit_price, stop_loss, take_profit, net_pnl, exit_reason, source, opened_at, closed_at,
                 rsi_5m, rsi_15m, rsi_1h, atr_5m, ema_fast_5m, ema_slow_5m, volume_ratio_5m, body_ratio_5m, confidence, hour_of_day_utc, day_of_week_utc)
            VALUES
                (@symbol, @direction, @quantity, @entryPrice, @exitPrice, @stopLoss, @takeProfit, @netPnl, @exitReason, @source, @openedAt, @closedAt,
                 @rsi5m, @rsi15m, @rsi1h, @atr5m, @emaFast5m, @emaSlow5m, @volumeRatio5m, @bodyRatio5m, @confidence, @hourOfDayUtc, @dayOfWeekUtc);
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
        command.Parameters.AddWithValue("rsi5m", (object?)features?.Rsi5m ?? DBNull.Value);
        command.Parameters.AddWithValue("rsi15m", (object?)features?.Rsi15m ?? DBNull.Value);
        command.Parameters.AddWithValue("rsi1h", (object?)features?.Rsi1h ?? DBNull.Value);
        command.Parameters.AddWithValue("atr5m", (object?)features?.Atr5m ?? DBNull.Value);
        command.Parameters.AddWithValue("emaFast5m", (object?)features?.EmaFast5m ?? DBNull.Value);
        command.Parameters.AddWithValue("emaSlow5m", (object?)features?.EmaSlow5m ?? DBNull.Value);
        command.Parameters.AddWithValue("volumeRatio5m", (object?)features?.VolumeRatio5m ?? DBNull.Value);
        command.Parameters.AddWithValue("bodyRatio5m", (object?)features?.BodyRatio5m ?? DBNull.Value);
        command.Parameters.AddWithValue("confidence", (object?)features?.Confidence ?? DBNull.Value);
        command.Parameters.AddWithValue("hourOfDayUtc", (object?)features?.HourOfDayUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("dayOfWeekUtc", (object?)features?.DayOfWeekUtc ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
