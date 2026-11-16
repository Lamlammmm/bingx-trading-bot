using System.Data.Common;
using System.Text.Json;
using TradingBot.Worker.Application;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.Persistence;

// The same additive schema and parameterized queries work on PostgreSQL/Supabase and SQLite.
public sealed class SqlTradingResearchStore(Func<DbConnection> openConnection) : ITradingResearchStore
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS trading_journal (
            trading_mode TEXT NOT NULL CHECK (trading_mode IN ('PAPER','LIVE')),
            event_id TEXT NOT NULL, trade_id TEXT NOT NULL, signal_id TEXT NULL,
            strategy_version TEXT NOT NULL, configuration_hash TEXT NOT NULL,
            symbol TEXT NOT NULL, kind TEXT NOT NULL, occurred_at TEXT NOT NULL, payload_json TEXT NOT NULL,
            PRIMARY KEY(trading_mode,event_id)
        );
        CREATE INDEX IF NOT EXISTS ix_trading_journal_mode_time ON trading_journal(trading_mode,occurred_at);
        CREATE INDEX IF NOT EXISTS ix_trading_journal_trade ON trading_journal(trading_mode,trade_id);
        CREATE TABLE IF NOT EXISTS strategy_versions (
            trading_mode TEXT NOT NULL CHECK (trading_mode IN ('PAPER','LIVE')), version TEXT NOT NULL,
            configuration_hash TEXT NOT NULL, registration_json TEXT NOT NULL, PRIMARY KEY(trading_mode,version)
        );
        CREATE TABLE IF NOT EXISTS strategy_active (
            trading_mode TEXT PRIMARY KEY CHECK (trading_mode IN ('PAPER','LIVE')), version TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS strategy_changes (
            change_id TEXT PRIMARY KEY, trading_mode TEXT NOT NULL, from_version TEXT NOT NULL,
            to_version TEXT NOT NULL, action TEXT NOT NULL, approved_by TEXT NOT NULL, occurred_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS trading_controls (
            trading_mode TEXT PRIMARY KEY CHECK (trading_mode IN ('PAPER','LIVE')),
            halted INTEGER NOT NULL, reason TEXT NOT NULL, updated_at TEXT NOT NULL
        );
        """;

    public async Task InitializeAsync(CancellationToken token)
    {
        await using var connection = openConnection();
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync(token);
        if (connection is Npgsql.NpgsqlConnection)
        {
            command.CommandText = """
                ALTER TABLE trading_journal ENABLE ROW LEVEL SECURITY;
                ALTER TABLE strategy_versions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE strategy_active ENABLE ROW LEVEL SECURITY;
                ALTER TABLE strategy_changes ENABLE ROW LEVEL SECURITY;
                ALTER TABLE trading_controls ENABLE ROW LEVEL SECURITY;
                DO $$ DECLARE r text; t text; BEGIN
                  FOREACH r IN ARRAY ARRAY['anon','authenticated'] LOOP
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname=r) THEN
                      FOREACH t IN ARRAY ARRAY['trading_journal','strategy_versions','strategy_active','strategy_changes','trading_controls'] LOOP
                        EXECUTE format('REVOKE ALL ON TABLE %I FROM %I',t,r);
                      END LOOP;
                    END IF;
                  END LOOP;
                END $$;
                """;
            await command.ExecuteNonQueryAsync(token);
        }
    }

    public async Task AppendAsync(TradingJournalEvent entry, CancellationToken token)
    {
        await using var connection = openConnection();
        await connection.OpenAsync(token);
        await AppendInTransactionAsync(connection, null, entry, token);
    }

    internal static async Task AppendInTransactionAsync(DbConnection connection, DbTransaction? transaction, TradingJournalEvent entry, CancellationToken token)
    {
        using var payload = JsonDocument.Parse(entry.PayloadJson);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO trading_journal(trading_mode,event_id,trade_id,signal_id,strategy_version,configuration_hash,symbol,kind,occurred_at,payload_json)
            VALUES(@mode,@event,@trade,@signal,@version,@hash,@symbol,@kind,@time,@payload)
            ON CONFLICT(trading_mode,event_id) DO NOTHING;
            """;
        Add(command, "mode", entry.Mode.ToString()); Add(command, "event", entry.EventId); Add(command, "trade", entry.TradeId);
        Add(command, "signal", entry.SignalId); Add(command, "version", entry.StrategyVersion); Add(command, "hash", entry.ConfigurationHash);
        Add(command, "symbol", entry.Symbol); Add(command, "kind", entry.Kind); Add(command, "time", entry.OccurredAt.UtcDateTime.ToString("O"));
        Add(command, "payload", entry.PayloadJson);
        await command.ExecuteNonQueryAsync(token);
    }

    public async IAsyncEnumerable<TradingJournalEvent> ReadAsync(TradingMode mode, DateTimeOffset since,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token, string? kind = null)
    {
        await using var connection = openConnection();
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id,trade_id,signal_id,strategy_version,configuration_hash,symbol,kind,occurred_at,payload_json FROM trading_journal WHERE trading_mode=@mode AND occurred_at>=@since" +
            (kind is null ? "" : " AND kind=@kind") + " ORDER BY occurred_at,event_id";
        Add(command, "mode", mode.ToString()); Add(command, "since", since.UtcDateTime.ToString("O"));
        if (kind is not null) Add(command, "kind", kind);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            yield return new(mode, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)), reader.GetString(8));
    }

    public async Task<StrategyRegistration?> GetActiveAsync(TradingMode mode, CancellationToken token)
    {
        await using var connection = openConnection(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT v.registration_json FROM strategy_versions v JOIN strategy_active a ON v.trading_mode=a.trading_mode AND v.version=a.version WHERE a.trading_mode=@mode";
        Add(command, "mode", mode.ToString());
        var json = await command.ExecuteScalarAsync(token) as string;
        return json is null ? null : JsonSerializer.Deserialize<StrategyRegistration>(json);
    }

    public async Task<StrategyRegistration?> GetVersionAsync(TradingMode mode, string version, CancellationToken token)
    {
        await using var connection = openConnection(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT registration_json FROM strategy_versions WHERE trading_mode=@mode AND version=@version";
        Add(command, "mode", mode.ToString()); Add(command, "version", version);
        var json = await command.ExecuteScalarAsync(token) as string;
        return json is null ? null : JsonSerializer.Deserialize<StrategyRegistration>(json);
    }

    public async Task RegisterAsync(StrategyRegistration registration, bool bootstrap, CancellationToken token)
    {
        var snapshot = registration.Snapshot;
        if (!Enum.IsDefined(snapshot.Mode) || string.IsNullOrWhiteSpace(snapshot.Version) ||
            snapshot.ConfigurationHash != TradingPolicyRuntime.Hash(snapshot.ConfigurationJson))
            throw new InvalidOperationException("Invalid strategy identity or configuration checksum.");
        if (bootstrap && (registration.Status != "APPROVED" || string.IsNullOrWhiteSpace(registration.ApprovedBy)))
            throw new InvalidOperationException("Bootstrap requires explicit configuration-owner approval metadata.");
        if (!bootstrap && registration.Status is not ("CANDIDATE" or "VALIDATED")) throw new InvalidOperationException("Cannot self-approve a candidate.");
        if (registration.Status == "VALIDATED") TradingPolicyRuntime.ValidateEvidence(registration);
        await using var connection = openConnection(); await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        if (!bootstrap)
        {
            command.CommandText = "SELECT v.registration_json FROM strategy_versions v JOIN strategy_active a ON v.trading_mode=a.trading_mode AND v.version=a.version WHERE a.trading_mode=@mode";
            Add(command, "mode", snapshot.Mode.ToString());
            var active = JsonSerializer.Deserialize<StrategyRegistration>((string?)await command.ExecuteScalarAsync(token)
                ?? throw new InvalidOperationException("Bootstrap an approved strategy before registering candidates."))!;
            TradingPolicyRuntime.ValidateCandidate(active.Snapshot, snapshot);
            command.Parameters.Clear();
        }
        command.CommandText = """
            INSERT INTO strategy_versions(trading_mode,version,configuration_hash,registration_json) VALUES(@mode,@version,@hash,@json)
            ON CONFLICT(trading_mode,version) DO NOTHING;
            """;
        Add(command, "mode", snapshot.Mode.ToString()); Add(command, "version", snapshot.Version);
        Add(command, "hash", snapshot.ConfigurationHash); Add(command, "json", JsonSerializer.Serialize(registration));
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT configuration_hash FROM strategy_versions WHERE trading_mode=@mode AND version=@version";
        if ((string?)await command.ExecuteScalarAsync(token) != snapshot.ConfigurationHash)
            throw new InvalidOperationException("A strategy version cannot be overwritten with a different configuration.");
        if (bootstrap)
        {
            command.CommandText = "INSERT INTO strategy_active(trading_mode,version) VALUES(@mode,@version) ON CONFLICT(trading_mode) DO NOTHING";
            if (await command.ExecuteNonQueryAsync(token) == 1)
            {
                command.CommandText = "INSERT INTO strategy_changes(change_id,trading_mode,from_version,to_version,action,approved_by,occurred_at) VALUES(@change,@mode,'',@version,'BOOTSTRAP',@by,@time)";
                Add(command, "change", Guid.NewGuid().ToString("N")); Add(command, "by", registration.ApprovedBy);
                Add(command, "time", snapshot.EffectiveAt.UtcDateTime.ToString("O"));
                await command.ExecuteNonQueryAsync(token);
            }
        }
        else if (registration.Status == "VALIDATED")
        {
            command.CommandText = "UPDATE strategy_versions SET registration_json=@json WHERE trading_mode=@mode AND version=@version";
            await command.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    public async Task PromoteAsync(TradingMode mode, string version, string expectedActiveVersion, string approvedBy, string action, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(approvedBy) || action is not ("APPROVE" or "ROLLBACK")) throw new InvalidOperationException("An explicit human approval is required.");
        await using var connection = openConnection(); await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT registration_json FROM strategy_versions WHERE trading_mode=@mode AND version=@version";
        Add(command, "mode", mode.ToString()); Add(command, "version", version);
        var registration = JsonSerializer.Deserialize<StrategyRegistration>((string?)await command.ExecuteScalarAsync(token)
            ?? throw new InvalidOperationException("Unknown strategy version."))!;
        if ((action == "APPROVE" && registration.Status != "VALIDATED") ||
            (action == "ROLLBACK" && registration.Status != "APPROVED"))
            throw new InvalidOperationException("Promotion needs validation; rollback needs a previously approved version.");
        if (action == "APPROVE") TradingPolicyRuntime.ValidateEvidence(registration);
        var approved = registration with { Status = "APPROVED", ApprovedBy = approvedBy, PreviousVersion = expectedActiveVersion,
            Snapshot = registration.Snapshot with { ApprovalStatus = "APPROVED", EffectiveAt = DateTimeOffset.UtcNow } };
        command.CommandText = "UPDATE strategy_active SET version=@version WHERE trading_mode=@mode AND version=@expected";
        Add(command, "expected", expectedActiveVersion);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("Active strategy changed; reload before approval.");
        command.CommandText = "UPDATE strategy_versions SET registration_json=@json WHERE trading_mode=@mode AND version=@version";
        Add(command, "json", JsonSerializer.Serialize(approved)); await command.ExecuteNonQueryAsync(token);
        command.CommandText = "INSERT INTO strategy_changes(change_id,trading_mode,from_version,to_version,action,approved_by,occurred_at) VALUES(@change,@mode,@expected,@version,@action,@by,@time)";
        Add(command, "change", Guid.NewGuid().ToString("N")); Add(command, "action", action); Add(command, "by", approvedBy); Add(command, "time", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task<bool> IsHaltedAsync(TradingMode mode, CancellationToken token)
    {
        await using var connection = openConnection(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT halted FROM trading_controls WHERE trading_mode=@mode";
        Add(command, "mode", mode.ToString()); var value = await command.ExecuteScalarAsync(token);
        return value is not null && value is not DBNull && Convert.ToInt32(value) != 0;
    }

    public async Task SetHaltAsync(TradingMode mode, bool halted, string reason, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A halt/resume reason is required.");
        await using var connection = openConnection(); await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO trading_controls(trading_mode,halted,reason,updated_at) VALUES(@mode,@halt,@reason,@time) ON CONFLICT(trading_mode) DO UPDATE SET halted=excluded.halted,reason=excluded.reason,updated_at=excluded.updated_at";
        Add(command, "mode", mode.ToString()); Add(command, "halt", halted ? 1 : 0); Add(command, "reason", reason); Add(command, "time", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "INSERT INTO trading_journal(trading_mode,event_id,trade_id,signal_id,strategy_version,configuration_hash,symbol,kind,occurred_at,payload_json) VALUES(@mode,@event,'control',NULL,'control','','','kill_switch',@time,@payload)";
        Add(command, "event", Guid.NewGuid().ToString("N")); Add(command, "payload", JsonSerializer.Serialize(new { Halted = halted, Reason = reason }));
        await command.ExecuteNonQueryAsync(token); await transaction.CommitAsync(token);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
}
