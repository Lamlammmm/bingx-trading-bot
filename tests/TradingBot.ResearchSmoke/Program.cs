using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.Persistence;

// This program instantiates SQL repositories only. No host, worker, AI or exchange executor.
var connectionString = "Host=127.0.0.1;Port=55439;Database=trading_validation;Username=postgres;Password=local-validation-only;Timeout=10";
var target = "local PostgreSQL";
if (args.Contains("--supabase"))
{
    using var cfg = JsonDocument.Parse(await File.ReadAllTextAsync("src/TradingBot.Worker/appsettings.Development.json"));
    var text = cfg.RootElement.GetProperty("Persistence").GetProperty("ConnectionString").GetString()!;
    if (text.StartsWith("postgres://") || text.StartsWith("postgresql://"))
    {
        var uri = new Uri(text); var credentials = uri.UserInfo.Split(':', 2);
        connectionString = new NpgsqlConnectionStringBuilder { Host = uri.Host, Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'), Username = Uri.UnescapeDataString(credentials[0]),
            Password = Uri.UnescapeDataString(credentials[1]), SslMode = SslMode.Require, Timeout = 10 }.ConnectionString;
    }
    else connectionString = text;
    target = "configured Supabase PostgreSQL, isolated temporary schema";
}
var schema = "codex_validation_" + Guid.NewGuid().ToString("N");
var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Timeout = 10 };
var checks = 0;
try
{
    await using (var admin = new NpgsqlConnection(connectionString))
    {
        await admin.OpenAsync();
        await using var create = admin.CreateCommand(); create.CommandText = $"CREATE SCHEMA {schema}"; await create.ExecuteNonQueryAsync();
    }
    var store = new SqlTradingResearchStore(() => new NpgsqlConnection(builder.ConnectionString));
    var strategy = Options.Create(new StrategyOptions());
    var risk = Options.Create(new RiskOptions());
    var adaptive = Options.Create(new AdaptiveOptions());
    var governance = new TradingGovernance(store, strategy, risk, Options.Create(new PaperTradingOptions()), adaptive,
        Options.Create(new LiveOptions()), NullLogger<TradingGovernance>.Instance);
    foreach (var mode in new[] { TradingMode.PAPER, TradingMode.LIVE })
    {
        await governance.InitializeAsync(mode, default);
        var active = (await store.GetActiveAsync(mode, default))!;
        var pnl = mode == TradingMode.PAPER ? 20m : -10m;
        var outcome = new TradingOutcome(mode, "same-trade-id", "fixture-signal", active.Snapshot, "BTC-USDT", TimeFrame.FiveMinutes,
            MarketRegime.TrendingUp, TradeDirection.Long, 1m, 100m, 110m, 90m, 120m, 5m, 0.1m, 0.02m, pnl,
            pnl / 10m, 12m, 3m, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1), "synthetic fixture", "TP", 2m,
            null, true, "synthetic integration fixture; not backtest evidence", 1000m, 10m);
        var entry = TradingGovernance.OutcomeEvent(outcome);
        await store.AppendAsync(entry, default); await store.AppendAsync(entry, default);
        var rows = await governance.OutcomesAsync(mode, DateTimeOffset.UtcNow.AddDays(-1), default);
        if (rows.Count != 1 || rows[0].NetPnl != pnl || rows[0].Mode != mode) throw new Exception("Mode isolation/idempotency failed");
        var summary = (await governance.PerformanceAsync(mode, default))[0];
        if (summary.TradeCount != 1 || summary.NetPnl != pnl) throw new Exception("Performance projection failed");
        checks += 2;
        await store.SetHaltAsync(mode, true, "synthetic fixture", default);
        if (!await store.IsHaltedAsync(mode, default)) throw new Exception("Kill switch failed");
        await store.SetHaltAsync(mode, false, "synthetic fixture cleanup", default); checks++;
    }
    var current = (await store.GetActiveAsync(TradingMode.LIVE, default))!;
    var candidateConfig = JsonSerializer.Deserialize<TradingConfiguration>(current.Snapshot.ConfigurationJson)!;
    candidateConfig.Strategy.SignalCooldownBars += 3;
    var json = JsonSerializer.Serialize(candidateConfig); var hash = TradingPolicyRuntime.Hash(json);
    var candidate = new StrategySnapshot(TradingMode.LIVE, "synthetic-candidate", hash, DateTimeOffset.UtcNow, "CANDIDATE", json);
    await store.RegisterAsync(new(candidate, "CANDIDATE"), false, default);
    var refused = false;
    try { await store.PromoteAsync(TradingMode.LIVE, candidate.Version, current.Snapshot.Version, "fixture", "APPROVE", default); }
    catch (InvalidOperationException) { refused = true; }
    if (!refused) throw new Exception("Unvalidated candidate was activated"); checks++;
    var now = DateTimeOffset.UtcNow;
    var evidence = new ValidationEvidence(hash, new string('a', 64), now.AddDays(-50), now.AddDays(-49), now.AddDays(-40),
        now.AddDays(-39), now.AddDays(-30), now.AddDays(-29), now.AddDays(-1), 3, 30, 30, 1m, 1.5m, 2m, 0m, true, true);
    await governance.ValidateCandidateAsync(TradingMode.LIVE, candidate.Version, evidence, default);
    refused = false;
    try { await store.PromoteAsync(TradingMode.LIVE, candidate.Version, current.Snapshot.Version, "", "APPROVE", default); }
    catch (InvalidOperationException) { refused = true; }
    if (!refused) throw new Exception("Missing approval was accepted"); checks++;
    await store.PromoteAsync(TradingMode.LIVE, candidate.Version, current.Snapshot.Version, "synthetic integration fixture", "APPROVE", default);
    if ((await store.GetActiveAsync(TradingMode.LIVE, default))!.Snapshot.Version != candidate.Version) throw new Exception("Approved promotion failed"); checks++;
    await store.PromoteAsync(TradingMode.LIVE, current.Snapshot.Version, candidate.Version, "synthetic integration fixture", "ROLLBACK", default);
    if ((await store.GetActiveAsync(TradingMode.LIVE, default))!.Snapshot.Version != current.Snapshot.Version) throw new Exception("Rollback failed"); checks++;

    var scans = new SignalScanStore(() => new NpgsqlConnection(builder.ConnectionString));
    await scans.InitializeAsync(default);
    await scans.InitializeAsync(default);
    var update = new MarketUpdate("BTC-USDT", TimeFrame.FiveMinutes,
        new Candle(now.AddMinutes(-5), now, 100m, 102m, 98m, 100m, 1m, true));
    var scan = new SignalScan(update, "LIVE", "Current", current.Snapshot.Version);
    scan.Reject("NO_VALID_SETUP");
    await scans.AppendAsync([scan], default);
    await scans.AppendAsync([scan], default);
    var filter = new SignalScanFilter(now.AddHours(-1), now.AddHours(1), "BTC-USDT", "5m", "LIVE", "Current", null, null, null);
    var history = await scans.HistoryAsync(filter, 25, 0, default);
    if (history.Count != 1 || history[0].GetProperty("primaryReason").GetString() != "NO_VALID_SETUP")
        throw new Exception("Diagnostics append, filtering or duplicate handling failed.");
    checks++;
    var report = JsonSerializer.SerializeToElement(await scans.OverviewAsync(filter, default));
    if (report.GetProperty("Total").GetProperty("RejectedSignals").GetInt64() != 1)
        throw new Exception("Diagnostics aggregate did not match history.");
    checks++;
    var candidateSignal = new StrategySignal(update.Symbol, TradeDirection.Long, now, 100m, 1m, "fixture");
    var firstSetup = new SignalScan(update, "SHADOW", "Experimental", "fixture") { ConfigurationHash = "current-fixture" };
    var duplicateSetup = new SignalScan(update, "SHADOW", "Experimental", "fixture") { ConfigurationHash = "current-fixture" };
    var oldSetup = new SignalScan(update, "SHADOW", "Experimental", "fixture") { ConfigurationHash = "other-fixture" };
    foreach (var setup in new[] { firstSetup, duplicateSetup, oldSetup }) setup.Observe(candidateSignal, null);
    await scans.AppendAsync([firstSetup, duplicateSetup, oldSetup], default);
    var days = await scans.SetupDaysAsync(now.AddHours(-1), now.AddHours(1), "SHADOW", "current-fixture", default);
    if (days.Values.Sum(d => d.Scans) != 2 || days.Values.Sum(d => d.Setups) != 1)
        throw new Exception("Diagnostics period setup counts did not deduplicate or scope by configuration.");
    checks++;
    await scans.RetainAsync(now.AddHours(1), default);
    if ((await scans.HistoryAsync(filter, 25, 0, default)).Count != 0)
        throw new Exception("Diagnostics retention failed.");
    checks++;
    await using (var verify = new NpgsqlConnection(builder.ConnectionString))
    {
        await verify.OpenAsync();
        await using var command = verify.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=current_schema() AND c.relrowsecurity";
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 6) throw new Exception("RLS was not enabled on all six tables"); checks++;
    }
    Console.WriteLine($"PASS {target}: {checks} checks covering journal, approval, rollback, diagnostics and RLS. Synthetic fixtures only.");
}
catch (Exception error)
{
    // Never print credentials, connection strings, or provider exception text from configured accounts.
    Console.Error.WriteLine($"FAIL {target}: {error.GetType().Name}; SQLSTATE={(error as PostgresException)?.SqlState ?? "n/a"}");
    Environment.ExitCode = 1;
}
finally
{
    try
    {
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand(); drop.CommandText = $"DROP SCHEMA IF EXISTS {schema} CASCADE"; await drop.ExecuteNonQueryAsync();
        Console.WriteLine("Removed only this run's generated codex_validation schema.");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Cleanup failed: {error.GetType().Name}; schema={schema}; SQLSTATE={(error as PostgresException)?.SqlState ?? "n/a"}");
        Environment.ExitCode = 1;
    }
}
