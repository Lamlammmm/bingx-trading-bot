using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.PaperTrading;

namespace TradingBot.Tests;

public sealed class OpenAiDecisionLoggingTests
{
    [Fact]
    public async Task LogsNoTradeAsAnExplicitAiDecision()
    {
        var logger = new ListLogger<PaperTradingEngine>();
        var decision = new AiDecision("no_trade", 0.72m, 0m, 0m, 0m,
            "No confirmed breakout", "Wait for a closed candle above resistance");
        var engine = CreateEngine(decision, null, logger);

        await engine.ProcessAsync(CreateClosedUpdate(), CancellationToken.None);

        var resultLog = Assert.Single(logger.Entries.Where(entry => entry.EventId.Name == "AiNoTrade"));
        Assert.Contains("AI RESULT: NO_TRADE", resultLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("AI RESULT: ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LogsTradeProposalAsAHighlightedAiDecision()
    {
        var logger = new ListLogger<PaperTradingEngine>();
        var decision = new AiDecision("long", 0.8m, 100m, 97m, 106m,
            "Confirmed breakout", "Close below support");
        var technicalSignal = new StrategySignal("BTC-USDT", TradeDirection.Long, DateTimeOffset.UtcNow,
            100m, 2m, "technical confirmation");
        var engine = CreateEngine(decision, technicalSignal, logger);

        await engine.ProcessAsync(CreateClosedUpdate(), CancellationToken.None);

        var resultLog = Assert.Single(logger.Entries.Where(entry => entry.EventId.Name == "AiTrade"));
        Assert.Equal(LogLevel.Warning, resultLog.Level);
        Assert.Contains("AI RESULT: TRADE LONG", resultLog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogsAnalyzerFailureAsUnavailableInsteadOfNoTrade()
    {
        var logger = new ListLogger<PaperTradingEngine>();
        var engine = CreateEngine(null, null, logger);

        await engine.ProcessAsync(CreateClosedUpdate(), CancellationToken.None);

        var resultLog = Assert.Single(logger.Entries.Where(entry => entry.EventId.Name == "AiError"));
        Assert.Equal(LogLevel.Error, resultLog.Level);
        Assert.Contains("AI RESULT: ERROR", resultLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("AI RESULT: NO_TRADE", StringComparison.Ordinal));
    }

    private static PaperTradingEngine CreateEngine(AiDecision? decision, StrategySignal? technicalSignal,
        ILogger<PaperTradingEngine> logger)
    {
        var store = new InMemoryCandleStore();
        return new PaperTradingEngine(store, new StubStrategy(technicalSignal),
            new RiskManager(Options.Create(new RiskOptions())), new StubAnalyzer(decision),
            Options.Create(new RiskOptions()), Options.Create(new OpenAIOptions
            {
                Enabled = true,
                MaximumEntryDeviationPercent = 0.25m
            }), Options.Create(new StrategyOptions()), logger);
    }

    private static MarketUpdate CreateClosedUpdate()
    {
        var openTime = DateTimeOffset.UtcNow.AddMinutes(-15);
        return new MarketUpdate("BTC-USDT", TimeFrame.FifteenMinutes,
            new Candle(openTime, openTime.AddMinutes(15), 99m, 101m, 98m, 100m, 10m, true));
    }

    private sealed class StubAnalyzer(AiDecision? decision) : IOpenAiAnalyzer
    {
        public bool Enabled => true;

        public Task<AiAnalysisResult> AnalyzeAsync(AiAnalysisContext context, CancellationToken cancellationToken) =>
            Task.FromResult(decision is null
                ? new AiAnalysisResult(null, "test_error")
                : new AiAnalysisResult(decision));
    }

    private sealed class StubStrategy(StrategySignal? signal) : ITradingStrategy
    {
        public StrategySignal? Evaluate(string symbol, IReadOnlyList<Candle> fifteenMinuteCandles,
            IReadOnlyList<Candle> oneHourCandles, IReadOnlyList<Candle> fourHourCandles) => signal;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = new();
        public IEnumerable<string> Messages => Entries.Select(entry => entry.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}
