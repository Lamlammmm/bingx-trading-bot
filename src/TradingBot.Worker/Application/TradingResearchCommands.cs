using System.Text.Json;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Application;

public static class TradingResearchCommands
{
    public static async Task<bool> RunAsync(TradingGovernance governance, string[] args)
    {
        var command = args.FirstOrDefault(a => a.StartsWith("--trading-", StringComparison.Ordinal) || a.StartsWith("--strategy-", StringComparison.Ordinal));
        if (command is null) return false;
        var split = command.Split('=', 2);
        var name = split[0];
        var value = split.Length == 2 ? split[1] : "";
        var modeArg = args.FirstOrDefault(a => a.StartsWith("--mode=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "PAPER";
        if (!Enum.TryParse<TradingMode>(modeArg, true, out var mode) || !Enum.IsDefined(mode)) throw new ArgumentException("Use --mode=PAPER or --mode=LIVE.");
        await governance.InitializeAsync(mode, default);
        var active = await governance.Store.GetActiveAsync(mode, default) ?? throw new InvalidOperationException("No active policy.");
        switch (name)
        {
            case "--trading-compare":
                await governance.InitializeAsync(TradingMode.PAPER, default);
                await governance.InitializeAsync(TradingMode.LIVE, default);
                var paper = await governance.PerformanceAsync(TradingMode.PAPER, default);
                var live = await governance.PerformanceAsync(TradingMode.LIVE, default);
                Console.WriteLine(JsonSerializer.Serialize(new { Paper = paper, Live = live,
                    NetExpectancyDifferenceLiveMinusPaper = live[0].NetExpectancy - paper[0].NetExpectancy,
                    EntrySlippageDifferenceBasisPoints = live[0].AverageSlippageBasisPoints - paper[0].AverageSlippageBasisPoints,
                    LiveEntryLatencyMilliseconds = live[0].AverageEntryLatencyMilliseconds,
                    Interpretation = "Separate samples; investigate measured execution costs, latency and journal events before attributing a cause. Paper does not validate Live." },
                    new JsonSerializerOptions { WriteIndented = true }));
                break;
            case "--trading-report":
                Console.WriteLine(JsonSerializer.Serialize(new { Mode = mode, Performance = await governance.PerformanceAsync(mode, default) }, new JsonSerializerOptions { WriteIndented = true }));
                break;
            case "--trading-export":
                var rows = await governance.OutcomesAsync(mode, DateTimeOffset.MinValue, default);
                await File.WriteAllLinesAsync(value, rows.Select(row => JsonSerializer.Serialize(row)));
                Console.WriteLine($"EXPORTED {mode} {rows.Count} outcomes to {Path.GetFullPath(value)}; check Reconciled before analysis.");
                break;
            case "--strategy-show":
                Console.WriteLine(JsonSerializer.Serialize(active, new JsonSerializerOptions { WriteIndented = true }));
                break;
            case "--strategy-candidate":
                var candidateConfig = JsonSerializer.Deserialize<TradingConfiguration>(await File.ReadAllTextAsync(value))!;
                var json = JsonSerializer.Serialize(candidateConfig); var hash = TradingPolicyRuntime.Hash(json);
                var candidate = new StrategySnapshot(mode, "candidate-" + hash[..16], hash, DateTimeOffset.UtcNow, "CANDIDATE", json);
                await governance.Store.RegisterAsync(new(candidate, "CANDIDATE", PreviousVersion: active.Snapshot.Version), false, default);
                Console.WriteLine($"CANDIDATE {mode} {candidate.Version} {hash}");
                break;
            case "--strategy-validate":
                using (var input = JsonDocument.Parse(await File.ReadAllTextAsync(value)))
                {
                    var version = input.RootElement.GetProperty("Version").GetString()!;
                    var evidence = input.RootElement.GetProperty("Evidence").Deserialize<ValidationEvidence>()!;
                    // Evidence is a measured-run attestation for review, never generated from recent PnL alone.
                    var artifactPath = input.RootElement.GetProperty("ArtifactPath").GetString()!;
                    var artifactHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(artifactPath))).ToLowerInvariant();
                    if (!artifactHash.Equals(evidence.ArtifactHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Validation artifact checksum mismatch.");
                    await governance.ValidateCandidateAsync(mode, version, evidence, default);
                    Console.WriteLine($"VALIDATED {mode} {version}; explicit promotion approval is still required.");
                }
                break;
            case "--strategy-promote":
            case "--strategy-rollback":
                var approval = args.FirstOrDefault(a => a.StartsWith("--approved-by=", StringComparison.Ordinal))?.Split('=', 2)[1]
                    ?? throw new ArgumentException("Explicit --approved-by=<reviewer> is required.");
                var expected = args.FirstOrDefault(a => a.StartsWith("--expected-active=", StringComparison.Ordinal))?.Split('=', 2)[1]
                    ?? throw new ArgumentException("Explicit --expected-active=<version> is required.");
                await governance.Store.PromoteAsync(mode, value, expected, approval, name == "--strategy-promote" ? "APPROVE" : "ROLLBACK", default);
                Console.WriteLine($"ACTIVE {mode} {value}");
                break;
            case "--trading-halt":
                var reason = args.FirstOrDefault(a => a.StartsWith("--reason=", StringComparison.Ordinal))?.Split('=', 2)[1]
                    ?? throw new ArgumentException("--reason is required.");
                if (!bool.TryParse(value, out var halted)) throw new ArgumentException("Use --trading-halt=true or false.");
                await governance.Store.SetHaltAsync(mode, halted, reason, default);
                Console.WriteLine($"HALT {mode} {halted}");
                break;
            default: throw new ArgumentException("Unknown trading research command.");
        }
        return true; // No HTTP host or trading worker is started by any of these commands.
    }
}
