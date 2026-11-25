using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.PaperTrading;

// This executable never creates a host, exchange executor, stream, or persistent trade store.
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (positional.Length < 2) throw new ArgumentException("Usage: TradingBot.Backtest <history-directory> <output.json> [compare|compare-current|research] [cost-multiplier] [--risk-trace] [--macd=true|false] [--strategy-config=<configuration.json>] [--funding-directory=<directory>] [--evaluate-from=<timestamp> --evaluate-until=<timestamp>]");
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var historyDirectory = Path.GetFullPath(positional[0]);
var variant = positional.Length > 2 ? positional[2] : "corrected-legacy";
var compare = variant == "compare";
var compareCurrent = variant == "compare-current";
var isComparison = compare || compareCurrent;
var traceRisk = args.Contains("--risk-trace");
var research = variant is "research" or "multi-only";
var dynamicManagement = variant is "research" or "management-only";
var costMultiplier = positional.Length > 3 ? decimal.Parse(positional[3], CultureInfo.InvariantCulture) : 1m;
var parameterMultiplier = positional.Length > 4 ? decimal.Parse(positional[4], CultureInfo.InvariantCulture) : 1m;
var spreadBasisPoints = positional.Length > 5 ? decimal.Parse(positional[5], CultureInfo.InvariantCulture) : 0m;
var fundingRate = positional.Length > 6 ? decimal.Parse(positional[6], CultureInfo.InvariantCulture) : 0m;
if (costMultiplier <= 0m) throw new ArgumentException("Cost multiplier must be positive.");
if (isComparison && positional.Length > 4)
    throw new ArgumentException("Compare uses the same configured strategy/risk settings on both sides. Only a common cost multiplier can be supplied.");
using var config = JsonDocument.Parse(File.ReadAllText("src/TradingBot.Worker/appsettings.json"));
var risk = config.RootElement.GetProperty("Risk").Deserialize<RiskOptions>(jsonOptions)!;
var strategyOptions = config.RootElement.GetProperty("Strategy").Deserialize<StrategyOptions>(jsonOptions)!;
var paperConfig = config.RootElement.GetProperty("PaperTrading");
var paper = paperConfig.Deserialize<PaperTradingOptions>(jsonOptions)!;
var experimentalOptions = config.RootElement.TryGetProperty(ExperimentalStrategyOptions.SectionName, out var experimentalConfig)
    ? experimentalConfig.Deserialize<ExperimentalStrategyOptions>(jsonOptions)! : new ExperimentalStrategyOptions();
var macd = (paperConfig.TryGetProperty("Macd", out var macdConfig) ? macdConfig.Deserialize<MacdOptions>(jsonOptions) : null)
    ?? new MacdOptions();
TradingConfiguration? registeredConfig = null;
var policyPath = args.FirstOrDefault(a => a.StartsWith("--strategy-config="))?.Split('=', 2)[1];
var macdOverride = args.FirstOrDefault(a => a.StartsWith("--macd=", StringComparison.Ordinal))?.Split('=', 2)[1];
var fundingDirectory = args.FirstOrDefault(a => a.StartsWith("--funding-directory=", StringComparison.Ordinal))?.Split('=', 2)[1];
var evaluationFrom = args.FirstOrDefault(a => a.StartsWith("--evaluate-from=", StringComparison.Ordinal))?.Split('=', 2)[1];
var evaluationUntil = args.FirstOrDefault(a => a.StartsWith("--evaluate-until=", StringComparison.Ordinal))?.Split('=', 2)[1];
if ((!compareCurrent && (fundingDirectory is not null || evaluationFrom is not null || evaluationUntil is not null)) ||
    (evaluationFrom is null) != (evaluationUntil is null))
    throw new ArgumentException("Use funding and a paired evaluation window only with compare-current.");
if (compareCurrent && policyPath is not null)
    throw new ArgumentException("compare-current requires the common appsettings paper policy.");
if (policyPath is not null)
{
    registeredConfig = JsonSerializer.Deserialize<TradingConfiguration>(File.ReadAllText(policyPath), jsonOptions)
        ?? throw new InvalidDataException("Expected a complete TradingConfiguration JSON.");
    risk = registeredConfig.Risk; strategyOptions = registeredConfig.Strategy; paper = registeredConfig.Costs;
    if (compare) macd = paper.Macd;
    research = strategyOptions.UseMultiTimeframeAnalysis;
    dynamicManagement = false; // Keep the submitted configuration exactly as registered.
    if (positional.Length > 3)
        throw new ArgumentException("Registered config replay does not accept positional cost/parameter overrides.");
}
if (traceRisk && (!isComparison || registeredConfig is not null))
    throw new ArgumentException("--risk-trace requires a comparison with the appsettings paper policy.");
if (macdOverride is not null)
{
    if (!bool.TryParse(macdOverride, out var macdEnabled))
        throw new ArgumentException("--macd expects true or false.");
    macd.Enabled = macdEnabled;
}
if (!macd.IsValid) throw new ArgumentException("MACD requires Fast > 0, Slow > Fast, and Signal > 0.");
paper.Macd = macd;
// Same notional budget for both revisions. Remove the optional $1 payoff cap in BOTH runs
// to measure the strategy at configured risk. No runtime configuration is changed.
if (!isComparison && registeredConfig is null) risk.TargetProfitUsdt = 0m;
risk.FeeRate *= costMultiplier;
paper.SlippageBasisPoints *= costMultiplier;
paper.MaximumEntryAgeSeconds = 0; // Offline replay only.
if (isComparison)
{
    paper.SpreadBasisPoints *= costMultiplier;
    risk.ExecutionCostBasisPoints = paper.SlippageBasisPoints + paper.SpreadBasisPoints / 2m;
}
else if (registeredConfig is null)
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
var files = Directory.GetFiles(historyDirectory, "*-5m.csv").Order(StringComparer.Ordinal).ToArray();
if (files.Length == 0) throw new InvalidDataException("No 5m datasets were found.");
var datasets = new List<ReplayMarket>();
foreach (var file in files)
{
    var symbol = Path.GetFileName(file).Replace("-5m.csv", "");
    var bars = File.ReadLines(file).Skip(1).Select(line =>
    {
        var parts = line.Split(',').Select(part => part.Trim('"', '\uFEFF')).ToArray();
        var time = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[0], CultureInfo.InvariantCulture));
        var values = parts.Skip(1).Select(value => decimal.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        return new Candle(time, time.AddMinutes(5), values[0], values[1], values[2], values[3], values[4], true);
    }).ToArray();
    if (bars.Length < 4000) throw new InvalidDataException("At least 4000 bars required for warmup and chronological splits.");
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(historyDirectory, $"{symbol}-manifest.json")));
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    if (!string.Equals(hash, manifest.RootElement.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase) ||
        manifest.RootElement.GetProperty("candles").GetInt32() != bars.Length ||
        bars[0].OpenTime.ToUnixTimeMilliseconds() != manifest.RootElement.GetProperty("startInclusive").GetInt64() ||
        bars[^1].CloseTime.ToUnixTimeMilliseconds() != manifest.RootElement.GetProperty("endExclusive").GetInt64())
        throw new InvalidDataException($"Manifest does not match complete dataset: {symbol}");
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
    var funding = new Dictionary<DateTimeOffset, decimal>();
    string? fundingHash = null;
    if (fundingDirectory is not null)
    {
        var path = Path.Combine(fundingDirectory, $"{symbol}-funding.json");
        var source = File.ReadAllBytes(path);
        using var fundingJson = JsonDocument.Parse(source);
        var root = fundingJson.RootElement;
        var start = root.GetProperty("startInclusive").GetInt64();
        var end = root.GetProperty("endInclusive").GetInt64();
        if (start > bars[0].OpenTime.ToUnixTimeMilliseconds() || end < bars[^1].CloseTime.ToUnixTimeMilliseconds())
            throw new InvalidDataException($"Funding input does not cover all candles for {symbol}.");
        var previous = start;
        foreach (var row in root.GetProperty("data").EnumerateArray())
        {
            var time = row.GetProperty("fundingTime").GetInt64();
            var rate = decimal.Parse(row.GetProperty("fundingRate").ToString(), CultureInfo.InvariantCulture);
            var mark = decimal.Parse(row.GetProperty("markPrice").ToString(), CultureInfo.InvariantCulture);
            if (row.GetProperty("symbol").GetString() != symbol || time < previous || time > end || time - previous > 28_800_000L ||
                time % 300_000L != 0 || mark <= 0m || !funding.TryAdd(DateTimeOffset.FromUnixTimeMilliseconds(time), rate * mark))
                throw new InvalidDataException($"Invalid, gapped or duplicate funding event for {symbol} at {time}.");
            previous = time;
        }
        if (funding.Count == 0 || end - previous >= 28_800_000L)
            throw new InvalidDataException($"Missing funding data at end of coverage for {symbol}.");
        fundingHash = Convert.ToHexString(SHA256.HashData(source));
    }
    datasets.Add(new(symbol, bars, Aggregate(bars, 30), Aggregate(bars, 60), contract, hash,
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(historyDirectory, $"{symbol}-contract.json")))), funding, fundingHash));
}
if (compareCurrent && (!strategyOptions.UseMultiTimeframeAnalysis ||
    datasets.Any(m => m.Bars[0].OpenTime != datasets[0].Bars[0].OpenTime || m.Bars[^1].CloseTime != datasets[0].Bars[^1].CloseTime)))
    throw new InvalidDataException("compare-current requires Current MTF and complete, aligned symbol datasets.");

var marketGroups = compareCurrent ? new[] { datasets.ToArray() } : datasets.Select(m => new[] { m });
var localOffset = TimeSpan.FromHours(7);
foreach (var markets in marketGroups)
{
    var bars = markets[0].Bars;
    var symbol = compareCurrent ? "PORTFOLIO" : markets[0].Symbol;
    var warmup = 10 * 288;
    var usable = bars.Length - warmup;
    // Frozen 60/20/20 chronology. No fitting on validation/test. Fresh state at each boundary.
    var boundaries = new[] { warmup, warmup + usable * 60 / 100, warmup + usable * 80 / 100, bars.Length };
    var windows = Enumerable.Range(0, 3).Select(i => (Name: new[] { "train", "validation", "test" }[i], First: boundaries[i], End: boundaries[i + 1])).ToList();
    // A frozen-parameter walk-forward audit: expanding prior history, next 20% evaluation,
    // no fitting. This is not evidence of a successful parameter optimization.
    windows.AddRange(Enumerable.Range(0, 3).Select(i => ($"walk-forward-{i + 1}", warmup + usable * (40 + i * 20) / 100, warmup + usable * (60 + i * 20) / 100)));
    if (compare && usable >= 7 * 288) windows.Add(("latest-seven-days", bars.Length - 7 * 288, bars.Length));
    if (compareCurrent)
    {
        // Group by the decision/fill timestamp, so a 00:00 local close belongs to the new date.
        var earliest = bars[warmup].CloseTime.ToOffset(localOffset);
        var latest = bars[^1].CloseTime.AddMinutes(5).ToOffset(localOffset);
        var firstMidnight = new DateTimeOffset(earliest.Date, localOffset).AddDays(earliest.TimeOfDay == TimeSpan.Zero ? 0 : 1);
        var lastMidnight = new DateTimeOffset(latest.Date, localOffset);
        var from = evaluationFrom is null ? firstMidnight : DateTimeOffset.Parse(evaluationFrom, CultureInfo.InvariantCulture).ToOffset(localOffset);
        var until = evaluationUntil is null ? lastMidnight : DateTimeOffset.Parse(evaluationUntil, CultureInfo.InvariantCulture).ToOffset(localOffset);
        if (from.TimeOfDay != TimeSpan.Zero || until.TimeOfDay != TimeSpan.Zero || from < firstMidnight || until > lastMidnight || from >= until)
            throw new ArgumentException("Requested window must consist of complete UTC+7 dates after ten days of warm-up.");
        var start = (int)(from - bars[0].CloseTime).TotalMinutes / 5;
        var end = (int)(until - bars[0].CloseTime).TotalMinutes / 5;
        var calendarDays = (until - from).Days;
        if (evaluationFrom is null && calendarDays < 5)
            throw new ArgumentException("At least five full UTC+7 dates are needed for 60/20/20 splits, or pass an explicit holdout window.");
        var validation = start + calendarDays * 60 / 100 * 288;
        var test = start + calendarDays * 80 / 100 * 288;
        windows = evaluationFrom is not null ? [("holdout", start, end)]
            : [("train", start, validation), ("validation", validation, test), ("test", test, end)];
    }
    var sides = compareCurrent ? new[] { "Current", "CurrentAdaptive" }
        : compare ? new[] { "Current", "Experimental" } : new[] { variant };
    foreach (var window in windows)
    foreach (var side in sides)
    {
        var first = window.First;
        var end = window.End;
        var store = new ReplayStore();
        var candles = new InMemoryCandleStore();
        var contracts = new InMemoryContractStore();
        foreach (var market in markets)
            contracts.Upsert(market.Symbol, market.Contract);
        var sideOptions = strategyOptions;
        if (compareCurrent)
        {
            sideOptions = JsonSerializer.Deserialize<StrategyOptions>(JsonSerializer.Serialize(strategyOptions), jsonOptions)!;
            sideOptions.UseAdaptiveCurrentEntries = side == "CurrentAdaptive";
        }
        var strategyConfig = Options.Create(sideOptions);
        ITradingStrategy strategy = side == "Experimental"
            ? new ExperimentalStrategy(strategyConfig, Options.Create(experimentalOptions))
            : (isComparison ? sideOptions.UseMultiTimeframeAnalysis : research)
                ? new MultiTimeframeStrategy(strategyConfig)
                : new BreakoutStrategy(strategyConfig);
        var replayLogger = new ReplayLogger();
        var tracedPlanner = traceRisk ? new ReplayRiskTrace(risk, jsonOptions) : null;
        IRiskManager planner = tracedPlanner as IRiskManager
            ?? (registeredConfig is null ? new RiskManager(Options.Create(risk)) : new RegisteredPlanner(registeredConfig));
        var fundingBySymbol = markets.ToDictionary(m => m.Symbol, m => m.Funding);
        var engine = new PaperTradingEngine(candles, strategy, planner,
            new NoAi(), store, contracts, Options.Create(risk), Options.Create(paper),
            Options.Create(new OpenAIOptions()), strategyConfig, replayLogger,
            enforcePortfolioLimits: isComparison, replayFunding: fundingDirectory is null ? null :
                (position, candle) => (position.Direction == TradeDirection.Long ? 1m : -1m) * position.Quantity *
                    fundingBySymbol[position.Symbol].GetValueOrDefault(candle.CloseTime));
        await engine.InitializeAsync(default);
        var mediumIndex = new int[markets.Length];
        var higherIndex = new int[markets.Length];
        var peak = risk.StartingBalance;
        var maxDrawdown = 0m;
        var equity = risk.StartingBalance;
        var equityCurve = new List<object>();
        var noTradeReasons = new Dictionary<string, int>();
        var regimeCounts = new Dictionary<MarketRegime, int>();
        var marksBySymbol = new Dictionary<string, decimal>();
        var maxOpenPositions = 0;
        var maxOpenNotional = 0m;
        var maxEntryNotionalPercent = 0m;
        var maxOpenRiskPercent = 0m;
        var maxDailyRealizedLossPercent = 0m;
        var filledSetups = new Dictionary<(string Symbol, DateTimeOffset Time), string>();
        var setupCounts = new Dictionary<string, int>();
        var dayClosingEquity = new Dictionary<DateOnly, decimal>();
        var exitCostRate = (paper.SlippageBasisPoints + paper.SpreadBasisPoints / 2m) / 10_000m;
        for (var index = first - warmup; index < end; index++)
        {
            for (var i = 0; i < markets.Length; i++)
            {
                var m = markets[i];
                var candle = m.Bars[index];
                marksBySymbol[m.Symbol] = candle.Close;
                while (mediumIndex[i] < m.Medium.Length && m.Medium[mediumIndex[i]].CloseTime <= candle.CloseTime)
                    candles.Upsert(m.Symbol, TimeFrame.ThirtyMinutes, m.Medium[mediumIndex[i]++]);
                while (higherIndex[i] < m.Higher.Length && m.Higher[higherIndex[i]].CloseTime <= candle.CloseTime)
                    candles.Upsert(m.Symbol, TimeFrame.OneHour, m.Higher[higherIndex[i]++]);
                var previous = strategy.LastEvaluation;
                var priorSignals = replayLogger.Counts.GetValueOrDefault("RuleSignal");
                await engine.ProcessAsync(new MarketUpdate(m.Symbol, TimeFrame.FiveMinutes, candle, index < first), default);
                if (index < first) continue;
                if (strategy.LastEvaluation is { } evaluation && evaluation.Time == candle.CloseTime && !ReferenceEquals(previous, evaluation))
                {
                    noTradeReasons[evaluation.Reason] = noTradeReasons.GetValueOrDefault(evaluation.Reason) + 1;
                    regimeCounts[evaluation.Regime] = regimeCounts.GetValueOrDefault(evaluation.Regime) + 1;
                    if (replayLogger.Counts.GetValueOrDefault("RuleSignal") > priorSignals)
                    {
                        var setup = evaluation.Setup ?? "breakout-retest";
                        setupCounts[setup] = setupCounts.GetValueOrDefault(setup) + 1;
                        if (store.Positions.Any(p => p.Symbol == m.Symbol && p.OpenedAt == candle.CloseTime))
                            filledSetups[(m.Symbol, candle.CloseTime)] = setup;
                    }
                }
                maxOpenPositions = Math.Max(maxOpenPositions, store.Positions.Count);
                var account = store.Account!;
                if (account.Balance > 0m)
                {
                    maxEntryNotionalPercent = Math.Max(maxEntryNotionalPercent,
                        store.Positions.Sum(p => p.EntryPrice * p.Quantity) / account.Balance * 100m);
                    maxOpenRiskPercent = Math.Max(maxOpenRiskPercent,
                        store.Positions.Sum(p => p.RiskAmount) / account.Balance * 100m);
                }
                if (account.RiskControls?.DayStartBalance is > 0m and var dayStartBalance)
                    maxDailyRealizedLossPercent = Math.Max(maxDailyRealizedLossPercent,
                        -account.RealizedPnlToday / dayStartBalance * 100m);
            }
            if (index < first) continue;
            equity = store.Account!.Balance;
            foreach (var position in store.Positions)
            {
                var sign = position.Direction == TradeDirection.Long ? 1m : -1m;
                var mark = marksBySymbol[position.Symbol] * (1m - sign * exitCostRate);
                equity += ((mark - position.EntryPrice) * sign - mark * risk.FeeRate) * position.Quantity;
            }
            maxOpenNotional = Math.Max(maxOpenNotional, store.Positions.Sum(p => marksBySymbol[p.Symbol] * p.Quantity));
            peak = Math.Max(peak, equity);
            maxDrawdown = Math.Max(maxDrawdown, (peak - equity) / peak * 100m);
            var time = bars[index].CloseTime;
            if (compareCurrent)
                dayClosingEquity[DateOnly.FromDateTime(time.ToOffset(localOffset).Date)] = equity;
            if (index % 288 == 0 || index == end - 1) equityCurve.Add(new { Time = time, Equity = equity });
        }
        var wins = store.Trades.Where(t => t.NetPnl > 0m).ToArray();
        var losses = store.Trades.Where(t => t.NetPnl < 0m).ToArray();
        var grossWins = wins.Sum(t => t.NetPnl);
        var grossLosses = -losses.Sum(t => t.NetPnl);
        var markedReturn = (equity - risk.StartingBalance) / risk.StartingBalance * 100m;
        var days = (decimal)(bars[end - 1].CloseTime - bars[first].OpenTime).TotalDays;
        var entries = store.Trades.Select(t => (t.Symbol, t.OpenedAt)).Concat(store.Positions.Select(p => (p.Symbol, p.OpenedAt))).ToArray();
        var dayOpeningEquity = risk.StartingBalance;
        var byDay = dayClosingEquity.Select(d =>
        {
            var filled = entries.Count(e => DateOnly.FromDateTime(e.OpenedAt.ToOffset(localOffset).Date) == d.Key);
            var markedPnl = d.Value - dayOpeningEquity;
            dayOpeningEquity = d.Value;
            return new { Date = d.Key, SimulatedEntries = filled, MarkedNetPnl = markedPnl };
        }).ToArray();
        var consecutive = 0;
        var maximumConsecutive = 0;
        foreach (var trade in store.Trades)
        {
            consecutive = trade.NetPnl < 0m ? consecutive + 1 : 0;
            maximumConsecutive = Math.Max(maximumConsecutive, consecutive);
        }
        results.Add(new
        {
            Strategy = side, StrategyVersion = side == "Experimental" ? ExperimentalStrategy.Version
                : side == "CurrentAdaptive" ? MultiTimeframeStrategy.AdaptiveVersion
                : isComparison ? (sideOptions.UseMultiTimeframeAnalysis ? "current-mtf-v1" : "current-breakout-v1") : variant,
            Symbol = symbol, Timeframe = "5m", Split = window.Name,
            From = compareCurrent ? bars[first].CloseTime : bars[first].OpenTime,
            Until = compareCurrent ? bars[end - 1].CloseTime.AddMinutes(5) : bars[end - 1].CloseTime, Bars = end - first,
            Sha256 = compareCurrent ? null : markets[0].Hash,
            Inputs = compareCurrent ? markets.Select(m => new { m.Symbol, m.Hash, m.ContractHash, m.FundingHash }).ToArray() : null,
            TradeCount = store.Trades.Count, WinRatePercent = store.Trades.Count == 0 ? (decimal?)null : wins.Length * 100m / store.Trades.Count,
            EvaluatedScans = replayLogger.Counts.GetValueOrDefault("RuleNoSignal"),
            PotentialSignals = replayLogger.Counts.GetValueOrDefault("RuleSignal"),
            SimulatedEntries = entries.Length,
            EntriesPerDay = entries.Length / days,
            TradesPerDay = store.Trades.Count / days,
            AverageWin = wins.Length == 0 ? (decimal?)null : grossWins / wins.Length,
            AverageLoss = losses.Length == 0 ? (decimal?)null : grossLosses / losses.Length,
            ProfitFactor = grossLosses == 0m ? (decimal?)null : grossWins / grossLosses,
            Expectancy = store.Trades.Count == 0 ? (decimal?)null : store.Trades.Average(t => t.NetPnl),
            ClosedNetPnl = store.Trades.Sum(t => t.NetPnl), CashBalance = store.Account!.Balance,
            MarkedEndingEquity = equity, MarkedReturnPercent = markedReturn,
            MaximumDrawdownPercent = maxDrawdown, MaximumConsecutiveLosses = maximumConsecutive,
            MaximumOpenPositions = maxOpenPositions, MaximumMarkedNotional = maxOpenNotional,
            MaximumEntryNotionalPercentOfCashBalance = maxEntryNotionalPercent,
            MaximumOpenRiskPercentOfCashBalance = maxOpenRiskPercent,
            MaximumDailyRealizedLossPercent = maxDailyRealizedLossPercent,
            ReturnDrawdownRatio = maxDrawdown > 0m ? markedReturn / maxDrawdown : (decimal?)null,
            AverageHoldingMinutes = store.Trades.Select(t => (decimal?)(t.ClosedAt - t.OpenedAt).TotalMinutes).Average(),
            AveragePlannedGrossRewardRisk = store.Trades.Select(t =>
                (decimal?)(Math.Abs(t.TakeProfit - t.EntryPrice) / Math.Abs(t.EntryPrice - t.StopLoss))).Average(),
            TotalFees = store.Trades.Sum(t => t.Metrics?.TotalFees ?? 0m),
            FundingPaid = store.Trades.Sum(t => t.Metrics?.FundingPaid ?? 0m),
            EstimatedSlippageCost = store.Trades.Sum(t =>
            {
                var sign = t.Direction == TradeDirection.Long ? 1m : -1m;
                var requestedNotional = t.EntryPrice / (1m + sign * exitCostRate) +
                    (t.Metrics?.AverageExitPrice ?? t.ExitPrice) / (1m - sign * exitCostRate);
                return requestedNotional * t.Quantity * paper.SlippageBasisPoints / 10_000m;
            }),
            AverageRealizedR = store.Trades.Where(t => t.Metrics?.InitialRiskAmount > 0m).Select(t => (decimal?)(t.NetPnl / t.Metrics!.InitialRiskAmount)).DefaultIfEmpty(null).Average(),
            ByDirection = store.Trades.GroupBy(t => t.Direction).Select(g => new { Direction = g.Key.ToString(), Trades = g.Count(), NetPnl = g.Sum(t => t.NetPnl) }),
            ByRegime = store.Trades.GroupBy(t => t.Metrics?.Regime.ToString() ?? "unknown").Select(g => new { Regime = g.Key, Trades = g.Count(), NetPnl = g.Sum(t => t.NetPnl) }),
            ByDay = compareCurrent ? byDay : null,
            BySymbol = compareCurrent ? markets.Select(m => new { m.Symbol, Entries = entries.Count(e => e.Symbol == m.Symbol),
                Trades = store.Trades.Count(t => t.Symbol == m.Symbol), NetPnl = store.Trades.Where(t => t.Symbol == m.Symbol).Sum(t => t.NetPnl) }).ToArray() : null,
            BySetup = compareCurrent ? setupCounts.Select(kv => new { Setup = kv.Key, SignalBars = kv.Value,
                Entries = filledSetups.Count(s => s.Value == kv.Key),
                ClosedNetPnl = store.Trades.Where(t => filledSetups.GetValueOrDefault((t.Symbol, t.OpenedAt)) == kv.Key).Sum(t => t.NetPnl) }).ToArray() : null,
            OpenPositionsAtEnd = store.Positions, EquityCurve = equityCurve,
            NoTradeReasons = noTradeReasons, RegimeCounts = regimeCounts, DecisionCounts = replayLogger.Counts,
            RiskTrace = tracedPlanner?.Records, Trades = store.Trades
        });
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(positional[1]))!);
await File.WriteAllTextAsync(positional[1], JsonSerializer.Serialize(new
{
    Revision = variant, GeneratedAt = DateTimeOffset.UtcNow,
    ConfigurationHash = policyPath is null ? null : TradingPolicyRuntime.Hash(JsonSerializer.Serialize(JsonSerializer.Deserialize<TradingConfiguration>(File.ReadAllText(policyPath), jsonOptions))),
    Assumptions = new { Costs = paper, Macd = macd, Risk = risk, Strategy = strategyOptions, Experimental = compare ? experimentalOptions : null, CostMultiplier = costMultiplier, ParameterMultiplier = parameterMultiplier,
        Execution = "Paper fill at completed 5m close with adverse spread/slippage and fees; SL-first on ambiguous bars; open positions marked at an adverse exit price and never counted as closed.",
        Friction = "Costs are reported on closed trades. Slippage excludes half-spread and is estimated before entry tick rounding, including partial exits.",
        Funding = fundingDirectory is null ? "Configured fixed-rate scenario, not historical funding." :
            "BingX settled rate times reported mark times remaining quantity, signed by side, before modeled exits on settlement-close bars; funding unchanged across execution-cost stresses.",
        Splits = compareCurrent ? "Complete UTC+7 calendar dates at candle-close decision times, ten-day warm-up, fresh accounts per split and no fitting. Default chronological 60/20/20 or one explicit holdout." :
            "60/20/20 chronological after 10-day warmup, fresh state each split, no parameter fitting. Walk-forward windows overlap the splits. Latest-seven-days is a separate market replay and can overlap other windows.",
        Scope = compareCurrent ? "One shared Paper account per strategy and window with portfolio limits; same-timestamp candles processed in ordinal symbol order. No Live executor, production overrides, historical bid/ask, liquidation, market impact or order-book replay." :
            "Independent single-symbol paper accounts. Comparison applies the same drawdown and exposure limits to both. No Live executor, exchange lifecycle, production overrides, persisted adaptive state, or order-book replay." },
    Results = results
}, jsonOptions));
Console.WriteLine($"Saved {results.Count} chronological runs to {positional[1]}");

static Candle[] Aggregate(Candle[] source, int minutes) => source
    .GroupBy(c => c.OpenTime.ToUnixTimeMilliseconds() / (minutes * 60000L))
    .Where(g => g.Count() == minutes / 5 && g.First().OpenTime.ToUnixTimeMilliseconds() % (minutes * 60000L) == 0)
    .Select(g => new Candle(g.First().OpenTime, g.First().OpenTime.AddMinutes(minutes), g.First().Open,
        g.Max(c => c.High), g.Min(c => c.Low), g.Last().Close, g.Sum(c => c.Volume), true)).ToArray();

sealed record ReplayMarket(string Symbol, Candle[] Bars, Candle[] Medium, Candle[] Higher, ContractInfo Contract,
    string Hash, string ContractHash, IReadOnlyDictionary<DateTimeOffset, decimal> Funding, string? FundingHash);

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

sealed class ReplayRiskTrace(RiskOptions options, JsonSerializerOptions jsonOptions) : IRiskManager
{
    private readonly RiskManager _planner = new(Options.Create(options));
    public string? LastRejectionReason => _planner.LastRejectionReason;
    public List<object> Records { get; } = [];

    public TradePlan? CreatePlan(StrategySignal signal, decimal capital, PaperPosition? position, decimal today,
        decimal aggregateRisk, ContractInfo? contract)
    {
        var plan = _planner.CreatePlan(signal, capital, position, today, aggregateRisk, contract);
        var measured = plan;
        if (plan is null && LastRejectionReason is "reward/risk after fees is below minimum" or "fee-inclusive weighted reward/risk below minimum")
        {
            // Measure the ratio below its floor with a second, isolated planner. The engine only receives the original decision.
            var measurementOptions = JsonSerializer.Deserialize<RiskOptions>(JsonSerializer.Serialize(options), jsonOptions)!;
            measurementOptions.MinimumNetRewardRiskRatio = 0m;
            measured = new RiskManager(Options.Create(measurementOptions)).CreatePlan(signal, capital, position, today, aggregateRisk, contract);
        }
        Records.Add(new
        {
            signal.Symbol, signal.Time, Direction = signal.Direction.ToString(), Regime = signal.Evaluation?.Regime.ToString(),
            Setup = signal.Evaluation?.Setup,
            EntryBeforeRounding = signal.EntryPrice, signal.Atr, signal.InvalidationPrice,
            Entry = measured?.EntryPrice, Stop = measured?.StopLoss, Target = measured?.TakeProfit,
            NetRewardRisk = measured?.NetRewardRiskRatio, Minimum = options.MinimumNetRewardRiskRatio,
            AcceptedByPlanner = plan is not null, Rejection = LastRejectionReason
        });
        return plan;
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
