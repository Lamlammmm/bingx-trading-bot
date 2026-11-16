using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.PaperTrading;

// This executable never creates a host, exchange executor, stream, or persistent trade store.
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (positional.Length < 2) throw new ArgumentException("Usage: TradingBot.Backtest <history-directory> <output.json> [research] [cost-multiplier] [--macd=true|false] [--strategy-config=<configuration.json>]");
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var historyDirectory = Path.GetFullPath(args[0]);
var variant = positional.Length > 2 ? positional[2] : "corrected-legacy";
var research = variant is "research" or "multi-only";
var dynamicManagement = variant is "research" or "management-only";
var costMultiplier = positional.Length > 3 ? decimal.Parse(positional[3], CultureInfo.InvariantCulture) : 1m;
var parameterMultiplier = positional.Length > 4 ? decimal.Parse(positional[4], CultureInfo.InvariantCulture) : 1m;
var spreadBasisPoints = positional.Length > 5 ? decimal.Parse(positional[5], CultureInfo.InvariantCulture) : 0m;
var fundingRate = positional.Length > 6 ? decimal.Parse(positional[6], CultureInfo.InvariantCulture) : 0m;
if (costMultiplier <= 0m) throw new ArgumentException("Cost multiplier must be positive.");
using var config = JsonDocument.Parse(File.ReadAllText("src/TradingBot.Worker/appsettings.json"));
var risk = config.RootElement.GetProperty("Risk").Deserialize<RiskOptions>(jsonOptions)!;
var strategyOptions = config.RootElement.GetProperty("Strategy").Deserialize<StrategyOptions>(jsonOptions)!;
var paperConfig = config.RootElement.GetProperty("PaperTrading");
var paper = paperConfig.Deserialize<PaperTradingOptions>(jsonOptions)!;
var macd = (paperConfig.TryGetProperty("Macd", out var macdConfig) ? macdConfig.Deserialize<MacdOptions>(jsonOptions) : null)
    ?? new MacdOptions();
TradingConfiguration? registeredConfig = null;
var policyPath = args.FirstOrDefault(a => a.StartsWith("--strategy-config="))?.Split('=', 2)[1];
var macdOverride = args.FirstOrDefault(a => a.StartsWith("--macd=", StringComparison.Ordinal))?.Split('=', 2)[1];
if (macdOverride is not null)
{
    if (!bool.TryParse(macdOverride, out var macdEnabled))
        throw new ArgumentException("--macd expects true or false.");
    macd.Enabled = macdEnabled;
}
if (policyPath is not null)
{
    registeredConfig = JsonSerializer.Deserialize<TradingConfiguration>(File.ReadAllText(policyPath), jsonOptions)
        ?? throw new InvalidDataException("Expected a complete TradingConfiguration JSON.");
    risk = registeredConfig.Risk; strategyOptions = registeredConfig.Strategy; paper = registeredConfig.Costs;
    research = strategyOptions.UseMultiTimeframeAnalysis;
    dynamicManagement = false; // Keep the submitted configuration exactly as registered.
    if (positional.Length > 3)
        throw new ArgumentException("Registered config replay does not accept positional cost/parameter overrides.");
}
if (!macd.IsValid) throw new ArgumentException("MACD requires Fast > 0, Slow > Fast, and Signal > 0.");
paper.Macd = macd;
// Same notional budget for both revisions. Remove the optional $1 payoff cap in BOTH runs
// to measure the strategy at configured risk. No runtime configuration is changed.
if (registeredConfig is null) risk.TargetProfitUsdt = 0m;
risk.FeeRate *= costMultiplier;
paper.SlippageBasisPoints *= costMultiplier;
paper.MaximumEntryAgeSeconds = 0; // Offline replay only.
if (registeredConfig is null)
{
    paper.SpreadBasisPoints = spreadBasisPoints;
    paper.FundingRatePerEightHours = fundingRate;
}
if (dynamicManagement)
{
    risk.UseDynamicPositionManagement = true;
    risk.TakeProfitRiskMultiple = 3m;
    risk.StructureBufferAtr = 0.2m;
    risk.ExecutionCostBasisPoints = paper.SlippageBasisPoints + paper.SpreadBasisPoints / 2m;
    risk.MaxOpenPositions = 3;
    risk.ExitCooldownBars = 6;
    risk.MaximumConsecutiveLosses = 3;
}
if (research) strategyOptions.MinimumAdx *= parameterMultiplier;
if (dynamicManagement) risk.TrailingAtrMultiplier *= parameterMultiplier;
var results = new List<object>();
foreach (var file in Directory.GetFiles(historyDirectory, "*-5m.csv").Order())
{
    var symbol = Path.GetFileName(file).Replace("-5m.csv", "");
    var bars = File.ReadLines(file).Skip(1).Select(line =>
    {
        var parts = line.Split(',').Select(part => part.Trim('"', '\uFEFF')).ToArray();
        var time = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[0], CultureInfo.InvariantCulture));
        var values = parts.Skip(1).Select(value => decimal.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        return new Candle(time, time.AddMinutes(5), values[0], values[1], values[2], values[3], values[4], true);
    }).ToArray();
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(historyDirectory, $"{symbol}-manifest.json")));
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    if (!string.Equals(hash, manifest.RootElement.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
        manifest.RootElement.GetProperty("candles").GetInt32() != bars.Length ||
        bars[0].OpenTime.ToUnixTimeMilliseconds() != manifest.RootElement.GetProperty("startInclusive").GetInt64() ||
        bars[^1].CloseTime.ToUnixTimeMilliseconds() != manifest.RootElement.GetProperty("endExclusive").GetInt64())
        throw new InvalidDataException($"Manifest does not match complete dataset: {symbol}");
    if (bars.Length < 4000) throw new InvalidDataException("At least 4000 bars required for warmup and chronological splits.");
    for (var i = 0; i < bars.Length; i++)
    {
        var c = bars[i];
        if (c.Open <= 0m || c.Close <= 0m || c.Low <= 0m || c.Volume < 0m ||
            c.High < Math.Max(c.Open, c.Close) || c.Low > Math.Min(c.Open, c.Close) ||
            c.High < c.Low || c.OpenTime.ToUnixTimeMilliseconds() % 300000 != 0 ||
            (i > 0 && c.OpenTime != bars[i - 1].CloseTime))
            throw new InvalidDataException($"Invalid/duplicate/gapped OHLCV at {symbol} {c.OpenTime:O}");
    }
    using var contractJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(historyDirectory, $"{symbol}-contract.json")));
    var contractRow = contractJson.RootElement.GetProperty("data").EnumerateArray().Single(c => c.GetProperty("symbol").GetString() == symbol);
    decimal Read(string name) => decimal.Parse(contractRow.GetProperty(name).ToString(), CultureInfo.InvariantCulture);
    var contract = new ContractInfo(symbol, Read("quantityPrecision"), Read("pricePrecision"), Read("tradeMinQuantity"), Read("tradeMinUSDT"), true);
    var medium = Aggregate(bars, 30);
    var higher = Aggregate(bars, 60);
    var warmup = 10 * 288;
    var usable = bars.Length - warmup;
    // Frozen 60/20/20 chronology. No fitting on validation/test. Fresh state at each boundary.
    var boundaries = new[] { warmup, warmup + usable * 60 / 100, warmup + usable * 80 / 100, bars.Length };
    var windows = Enumerable.Range(0, 3).Select(i => (Name: new[] { "train", "validation", "test" }[i], First: boundaries[i], End: boundaries[i + 1])).ToList();
    // A frozen-parameter walk-forward audit: expanding prior history, next 20% evaluation,
    // no fitting. This is not evidence of a successful parameter optimization.
    windows.AddRange(Enumerable.Range(0, 3).Select(i => ($"walk-forward-{i + 1}", warmup + usable * (40 + i * 20) / 100, warmup + usable * (60 + i * 20) / 100)));
    foreach (var window in windows)
    {
        var first = window.First;
        var end = window.End;
        var store = new ReplayStore();
        var candles = new InMemoryCandleStore();
        var contracts = new InMemoryContractStore();
        contracts.Upsert(symbol, contract);
        ITradingStrategy strategy = new BreakoutStrategy(Options.Create(strategyOptions));
        if (research)
        {
            var type = typeof(BreakoutStrategy).Assembly.GetType("TradingBot.Worker.Application.MultiTimeframeStrategy")
                ?? throw new InvalidOperationException("Research strategy is unavailable in this revision.");
            strategy = (ITradingStrategy)Activator.CreateInstance(type, Options.Create(strategyOptions))!;
        }
        var replayLogger = new ReplayLogger();
        IRiskManager planner = registeredConfig is null ? new RiskManager(Options.Create(risk)) : new RegisteredPlanner(registeredConfig);
        var engine = new PaperTradingEngine(candles, strategy, planner,
            new NoAi(), store, contracts, Options.Create(risk), Options.Create(paper),
            Options.Create(new OpenAIOptions()), Options.Create(strategyOptions), replayLogger);
        await engine.InitializeAsync(default);
        var mediumIndex = 0;
        var higherIndex = 0;
        var peak = risk.StartingBalance;
        var maxDrawdown = 0m;
        var equityCurve = new List<object>();
        var noTradeReasons = new Dictionary<string, int>();
        for (var index = first - warmup; index < end; index++)
        {
            var candle = bars[index];
            while (mediumIndex < medium.Length && medium[mediumIndex].CloseTime <= candle.CloseTime)
                candles.Upsert(symbol, TimeFrame.ThirtyMinutes, medium[mediumIndex++]);
            while (higherIndex < higher.Length && higher[higherIndex].CloseTime <= candle.CloseTime)
                candles.Upsert(symbol, TimeFrame.OneHour, higher[higherIndex++]);
            await engine.ProcessAsync(new MarketUpdate(symbol, TimeFrame.FiveMinutes, candle, index < first), default);
            if (index < first) continue;
            var evaluation = strategy.GetType().GetProperty("LastEvaluation")?.GetValue(strategy);
            var reason = evaluation?.GetType().GetProperty("Reason")?.GetValue(evaluation)?.ToString();
            if (reason is not null) noTradeReasons[reason] = noTradeReasons.GetValueOrDefault(reason) + 1;
            var equity = store.Account!.Balance + store.Positions.Sum(p =>
                (candle.Close - p.EntryPrice) * p.Quantity * (p.Direction == TradeDirection.Long ? 1m : -1m)
                - candle.Close * p.Quantity * risk.FeeRate);
            peak = Math.Max(peak, equity);
            maxDrawdown = Math.Max(maxDrawdown, (peak - equity) / peak * 100m);
            if (index % 288 == 0 || index == end - 1) equityCurve.Add(new { Time = candle.CloseTime, Equity = equity });
        }
        var wins = store.Trades.Where(t => t.NetPnl > 0m).ToArray();
        var losses = store.Trades.Where(t => t.NetPnl < 0m).ToArray();
        var grossWins = wins.Sum(t => t.NetPnl);
        var grossLosses = -losses.Sum(t => t.NetPnl);
        var consecutive = 0;
        var maximumConsecutive = 0;
        foreach (var trade in store.Trades)
        {
            consecutive = trade.NetPnl < 0m ? consecutive + 1 : 0;
            maximumConsecutive = Math.Max(maximumConsecutive, consecutive);
        }
        results.Add(new
        {
            Symbol = symbol, Split = window.Name,
            From = bars[first].OpenTime, Until = bars[end - 1].CloseTime, Bars = end - first,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
            TradeCount = store.Trades.Count, WinRatePercent = store.Trades.Count == 0 ? (decimal?)null : wins.Length * 100m / store.Trades.Count,
            AverageWin = wins.Length == 0 ? (decimal?)null : grossWins / wins.Length,
            AverageLoss = losses.Length == 0 ? (decimal?)null : grossLosses / losses.Length,
            ProfitFactor = grossLosses == 0m ? (decimal?)null : grossWins / grossLosses,
            Expectancy = store.Trades.Count == 0 ? (decimal?)null : store.Trades.Average(t => t.NetPnl),
            ClosedNetPnl = store.Trades.Sum(t => t.NetPnl), CashBalance = store.Account!.Balance,
            MaximumDrawdownPercent = maxDrawdown, MaximumConsecutiveLosses = maximumConsecutive,
            AverageRealizedR = store.Trades.Where(t => t.Metrics?.InitialRiskAmount > 0m).Select(t => (decimal?)(t.NetPnl / t.Metrics!.InitialRiskAmount)).DefaultIfEmpty(null).Average(),
            ByRegime = store.Trades.GroupBy(t => t.Metrics?.Regime.ToString() ?? "unknown").Select(g => new { Regime = g.Key, Trades = g.Count(), NetPnl = g.Sum(t => t.NetPnl) }),
            OpenPositionsAtEnd = store.Positions, EquityCurve = equityCurve,
            NoTradeReasons = noTradeReasons, DecisionCounts = replayLogger.Counts, Trades = store.Trades
        });
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
{
    Revision = variant, GeneratedAt = DateTimeOffset.UtcNow,
    ConfigurationHash = policyPath is null ? null : TradingPolicyRuntime.Hash(JsonSerializer.Serialize(JsonSerializer.Deserialize<TradingConfiguration>(File.ReadAllText(policyPath), jsonOptions))),
    Assumptions = new { Costs = paper, Macd = macd, Risk = risk, Strategy = strategyOptions, CostMultiplier = costMultiplier, ParameterMultiplier = parameterMultiplier,
        Execution = "Signal at closed 5m close with adverse cost scenario; SL-first for ambiguous bars; open positions marked, never forcibly labelled closed.",
        Funding = "Configured fixed-rate scenario, not historical funding. No historical bid/ask, liquidation, queue, or market-impact model.",
        Splits = "60/20/20 chronological after 10-day warmup, fresh state each split; no optimization. Single-symbol accounts, not a portfolio backtest." },
    Results = results
}, jsonOptions));
Console.WriteLine($"Saved {results.Count} chronological runs to {args[1]}");

static Candle[] Aggregate(Candle[] source, int minutes) => source
    .GroupBy(c => c.OpenTime.ToUnixTimeMilliseconds() / (minutes * 60000L))
    .Where(g => g.Count() == minutes / 5 && g.First().OpenTime.ToUnixTimeMilliseconds() % (minutes * 60000L) == 0)
    .Select(g => new Candle(g.First().OpenTime, g.First().OpenTime.AddMinutes(minutes), g.First().Open,
        g.Max(c => c.High), g.Min(c => c.Low), g.Last().Close, g.Sum(c => c.Volume), true)).ToArray();

sealed class NoAi : IOpenAiAnalyzer
{
    public bool Enabled => false;
    public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisContext context, CancellationToken cancellationToken) => throw new InvalidOperationException();
}

sealed class RegisteredPlanner : IRiskManager
{
    private readonly TradingPolicyRuntime _policy;
    public RegisteredPlanner(TradingConfiguration configuration)
    {
        var json = JsonSerializer.Serialize(configuration);
        _policy = new(new(TradingMode.PAPER, "offline-validation", TradingPolicyRuntime.Hash(json), DateTimeOffset.UtcNow, "REPLAY", json));
    }
    public TradePlan? CreatePlan(StrategySignal signal, decimal capital, PaperPosition? position, decimal today, decimal aggregateRisk, ContractInfo? contract) =>
        contract is null ? null : _policy.Plan(signal, capital, capital, aggregateRisk, 0m, contract, _policy.Configuration.Risk.MaxLeverage, 1m);
}

sealed class ReplayLogger : ILogger<PaperTradingEngine>
{
    public Dictionary<string, int> Counts { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (eventId.Name is { } name) Counts[name] = Counts.GetValueOrDefault(name) + 1;
        if (eventId.Name == "RuleRejected" && state is IEnumerable<KeyValuePair<string, object?>> fields)
        {
            var reason = fields.FirstOrDefault(pair => pair.Key == "Reason").Value?.ToString() ?? "unknown";
            Counts["Rejected: " + reason] = Counts.GetValueOrDefault("Rejected: " + reason) + 1;
        }
        if (eventId.Name == "MacdEntryFilter" && state is IEnumerable<KeyValuePair<string, object?>> macdFields)
        {
            var result = macdFields.FirstOrDefault(pair => pair.Key == "Result").Value;
            var reason = macdFields.FirstOrDefault(pair => pair.Key == "Reason").Value;
            var key = $"MACD {result}: {reason}";
            Counts[key] = Counts.GetValueOrDefault(key) + 1;
        }
    }
}

sealed class ReplayStore : ITradeStore
{
    public AccountState? Account { get; private set; }
    public IReadOnlyCollection<PaperPosition> Positions { get; private set; } = [];
    public List<ClosedTrade> Trades { get; } = [];
    public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
    public Task<AccountState?> LoadAccountStateAsync(CancellationToken token) => Task.FromResult<AccountState?>(null);
    public Task<TradePerformance?> LoadTradePerformanceAsync(CancellationToken token) => Task.FromResult<TradePerformance?>(null);
    public Task<IReadOnlyList<PaperPosition>> LoadOpenPositionsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<PaperPosition>>([]);
    public Task SaveAccountStateAsync(AccountState state, CancellationToken token) { Account = state; return Task.CompletedTask; }
    public Task SaveOpenPositionsAsync(IReadOnlyCollection<PaperPosition> positions, CancellationToken token) { Positions = positions; return Task.CompletedTask; }
    public Task RecordClosedTradeAsync(ClosedTrade trade, CancellationToken token) { Trades.Add(trade); return Task.CompletedTask; }
    public async IAsyncEnumerable<StoredPaperTrade> ReadClosedTradesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) { await Task.CompletedTask; yield break; }
}
