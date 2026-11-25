using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.Persistence;

public sealed class SignalScanStore(Func<DbConnection> connect)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task InitializeAsync(CancellationToken token)
    {
        await using var connection = connect();
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS signal_scans (
                id TEXT PRIMARY KEY, scanned_at TEXT NOT NULL, symbol TEXT NOT NULL,
                timeframe TEXT NOT NULL, mode TEXT NOT NULL, strategy TEXT NOT NULL, version TEXT NOT NULL,
                regime TEXT NOT NULL, decision TEXT NOT NULL, reason TEXT, direction TEXT NOT NULL,
                execution TEXT NOT NULL, filled_at TEXT, error_kind TEXT, payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_signal_scans_time ON signal_scans(scanned_at, id);
            """;
        if (connection is Npgsql.NpgsqlConnection)
            command.CommandText += """

                ALTER TABLE signal_scans ENABLE ROW LEVEL SECURITY;
                REVOKE ALL ON TABLE signal_scans FROM PUBLIC;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
                        REVOKE ALL ON TABLE signal_scans FROM anon;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
                        REVOKE ALL ON TABLE signal_scans FROM authenticated;
                    END IF;
                END $$;
                """;
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    public async Task AppendAsync(IReadOnlyList<SignalScan> scans, CancellationToken token)
    {
        await using var connection = connect();
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        var values = new string[scans.Count];
        for (var i = 0; i < scans.Count; i++)
        {
            var scan = scans[i];
            object?[] fields = [scan.Id, Stamp(scan.Timestamp), scan.Symbol, scan.Timeframe, scan.Mode, scan.Strategy,
                scan.Version, scan.Regime.ToString(), scan.Decision, scan.PrimaryReason, scan.Direction,
                scan.ExecutionStatus, scan.FilledAt is { } filled ? Stamp(filled) : null, scan.ErrorDetails, JsonSerializer.Serialize(scan, Json)];
            var names = new string[fields.Length];
            for (var j = 0; j < fields.Length; j++)
            {
                var name = names[j] = $"@p{i}_{j}";
                Add(command, name, fields[j]);
            }
            values[i] = "(" + string.Join(", ", names) + ")";
        }
        command.CommandText = """
            INSERT INTO signal_scans(id, scanned_at, symbol, timeframe, mode, strategy, version, regime,
                decision, reason, direction, execution, filled_at, error_kind, payload)
            VALUES
            """ + " " + string.Join(", ", values) + " ON CONFLICT(id) DO NOTHING";
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task RetainAsync(DateTimeOffset since, CancellationToken token)
    {
        await using var connection = connect();
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            DELETE FROM signal_scans WHERE id IN
                (SELECT id FROM signal_scans WHERE scanned_at < @since ORDER BY scanned_at LIMIT 5000)
            """;
        Add(command, "@since", Stamp(since));
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<IReadOnlyList<JsonElement>> HistoryAsync(SignalScanFilter filter, int limit, int offset, CancellationToken token)
    {
        await using var connection = connect();
        await connection.OpenAsync(token);
        await using var command = Filter(connection, filter);
        command.CommandText = "SELECT payload FROM signal_scans" + command.CommandText +
            " ORDER BY scanned_at DESC, id DESC LIMIT @limit OFFSET @offset";
        Add(command, "@limit", limit);
        Add(command, "@offset", offset);
        var result = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        return result;
    }

    public async Task<object> OverviewAsync(SignalScanFilter filter, CancellationToken token)
    {
        await using var connection = connect();
        await connection.OpenAsync(token);
        var groups = new Dictionary<string, List<SignalScanCounts>>();
        var hour = filter.To - filter.From <= TimeSpan.FromDays(1);
        foreach (var (name, column) in new (string, string)[]
        {
            ("total", "'all'"), ("byReason", "reason"), ("bySymbol", "symbol"), ("byTimeframe", "timeframe"),
            ("byStrategy", "strategy || '/' || version || '/' || mode"), ("byRegime", "regime"),
            ("overTime", hour ? "SUBSTR(scanned_at, 1, 13) || ':00:00Z'" : "SUBSTR(scanned_at, 1, 10)")
        })
        {
            await using var command = Filter(connection, filter);
            command.CommandText = $"""
                SELECT {column} AS bucket, COUNT(*),
                    COUNT(CASE WHEN direction <> 'NONE' THEN 1 END),
                    COUNT(CASE WHEN decision = 'ACCEPTED' THEN 1 END),
                    COUNT(CASE WHEN decision = 'REJECTED' THEN 1 END),
                    COUNT(error_kind),
                    COUNT(CASE WHEN execution <> 'NOT_ATTEMPTED' THEN 1 END),
                    COUNT(CASE WHEN execution IN ('PLACED', 'FILLED') THEN 1 END),
                    COUNT(CASE WHEN execution = 'FILLED' THEN 1 END),
                    COUNT(CASE WHEN execution = 'REJECTED' THEN 1 END),
                    COUNT(CASE WHEN execution = 'PLACED' THEN 1 END),
                    COUNT(CASE WHEN execution = 'UNKNOWN' THEN 1 END),
                    MAX(filled_at)
                FROM signal_scans
                """ + command.CommandText + (name == "byReason" ? " AND decision = 'REJECTED' AND reason IS NOT NULL" : "") +
                (name == "total" ? "" : " GROUP BY 1 ORDER BY 1");
            var counts = groups[name] = [];
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                DateTimeOffset? lastFill = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture);
                counts.Add(new SignalScanCounts(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                    reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), lastFill));
            }
        }
        var total = groups["total"].Single();
        return new
        {
            filter.From, filter.To, Total = total,
            SecondsSinceLastFill = total.LastSuccessfulTrade is { } last ? (double?)(DateTimeOffset.UtcNow - last).TotalSeconds : null,
            TopRejection = groups["byReason"].OrderByDescending(x => x.RejectedSignals).FirstOrDefault()?.Bucket,
            TimeBucket = hour ? "hour" : "day",
            Groups = groups.Where(p => p.Key != "total").ToDictionary()
        };
    }

    public async Task<Dictionary<DateOnly, (long Scans, long Setups)>> SetupDaysAsync(
        DateTimeOffset from, DateTimeOffset to, string mode, string? configurationHash, CancellationToken token)
    {
        await using var connection = connect();
        await connection.OpenAsync(token);
        await using var command = Filter(connection, new SignalScanFilter(from, to, null, "5m", mode, null, null, null, null));
        var payload = connection is Npgsql.NpgsqlConnection ? "CAST(payload AS JSONB)" : "payload";
        command.CommandText = $"""
            SELECT SUBSTR(scanned_at, 1, 10), COUNT(*),
                COUNT(DISTINCT CASE WHEN direction <> 'NONE' THEN
                    symbol || '|' || timeframe || '|' || direction || '|' || version || '|' ||
                    COALESCE(({payload} ->> 'configurationHash'), '') || '|' || ({payload} ->> 'candleClose')
                END)
            FROM signal_scans
            """ + command.CommandText +
            (configurationHash is null ? "" : $" AND ({payload} ->> 'configurationHash') = @hash") +
            " GROUP BY 1 ORDER BY 1";
        if (configurationHash is not null) Add(command, "@hash", configurationHash);
        var days = new Dictionary<DateOnly, (long Scans, long Setups)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            days.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                (reader.GetInt64(1), reader.GetInt64(2)));
        return days;
    }

    internal async Task<IReadOnlyList<LiveScanActivity>> LiveActivityAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        await using var connection = connect();
        await connection.OpenAsync(token);
        await using var command = Filter(connection, new SignalScanFilter(from, to, null, "5m", "LIVE", null, null, null, null));
        var payload = connection is Npgsql.NpgsqlConnection ? "CAST(payload AS JSONB)" : "payload";
        command.CommandText = $"""
            SELECT {payload} ->> 'stage', reason, decision, execution, error_kind IS NOT NULL, COUNT(*)
            FROM signal_scans
            """ + command.CommandText + " GROUP BY 1, 2, 3, 4, 5 ORDER BY COUNT(*) DESC, 1, 2, 3, 4, 5";
        var activity = new List<LiveScanActivity>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            activity.Add(new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetInt64(5)));
        return activity;
    }

    private static DbCommand Filter(DbConnection connection, SignalScanFilter filter)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = " WHERE scanned_at >= @from AND scanned_at < @to";
        Add(command, "@from", Stamp(filter.From));
        Add(command, "@to", Stamp(filter.To));
        foreach (var (column, value) in new (string, string?)[]
        {
            ("symbol", filter.Symbol?.ToUpperInvariant()), ("timeframe", filter.Timeframe), ("mode", filter.Mode?.ToUpperInvariant()),
            ("strategy", filter.Strategy), ("version", filter.Version), ("regime", filter.Regime), ("decision", filter.Decision?.ToUpperInvariant())
        })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            command.CommandText += $" AND {column} = @{column}";
            Add(command, "@" + column, value);
        }
        return command;
    }

    private static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        if (value is null) parameter.DbType = System.Data.DbType.String;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

public sealed record SignalScanFilter(DateTimeOffset From, DateTimeOffset To, string? Symbol, string? Timeframe,
    string? Mode, string? Strategy, string? Version, string? Regime, string? Decision);

internal sealed record LiveScanActivity(string? Stage, string? Reason, string Decision, string Execution, bool HasError, long Count);

public sealed record SignalScanCounts(string Bucket, long TotalMarketScans, long PotentialSignals,
    long AcceptedSignals, long RejectedSignals, long Errors, long OrderAttempts, long PlacedOrders,
    long FilledOrders, long RejectedOrders, long PlacedAwaitingFillConfirmation, long UnknownOrderOutcomes,
    DateTimeOffset? LastSuccessfulTrade)
{
    public double RejectionRate => TotalMarketScans == 0 ? 0 : (double)RejectedSignals / TotalMarketScans;
}
