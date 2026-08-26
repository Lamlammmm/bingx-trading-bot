using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.OpenAI;

public sealed class OpenAiAnalysisClient(HttpClient httpClient, IOptions<OpenAIOptions> options,
    ILogger<OpenAiAnalysisClient> logger) : IOpenAiAnalyzer
{
    private readonly OpenAIOptions _options = options.Value;

    public bool Enabled => _options.Enabled;

    public async Task<AiDecision?> AnalyzeAsync(AiAnalysisContext context, CancellationToken cancellationToken)
    {
        if (!Enabled) return null;
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            logger.LogError("OpenAI analysis is enabled but OPENAI_API_KEY is not configured");
            return null;
        }

        var requestBody = new
        {
            model = _options.Model,
            store = false,
            input = new object[]
            {
                new
                {
                    role = "developer",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "You are a crypto market analysis component. Analyze only the supplied closed candles and technical context. Do not invent prices, news, or indicators. If evidence is insufficient, contradictory, stale, or risky, return no_trade. Never suggest leverage above the configured risk policy. This output is only a proposal for a separate risk gate; you cannot place orders."
                        }
                    }
                },
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = JsonSerializer.Serialize(new
                            {
                                context.Symbol,
                                context.CandleTime,
                                technicalSignal = context.TechnicalSignal is null ? null : new
                                {
                                    direction = context.TechnicalSignal.Direction.ToString(),
                                    entry = context.TechnicalSignal.EntryPrice,
                                    atr = context.TechnicalSignal.Atr,
                                    context.TechnicalSignal.Reason
                                },
                                candles = new
                                {
                                    fifteenMinute = ToCompactCandles(context.FifteenMinuteCandles, _options.CandleCountPerTimeFrame),
                                    oneHour = ToCompactCandles(context.OneHourCandles, _options.CandleCountPerTimeFrame),
                                    fourHour = ToCompactCandles(context.FourHourCandles, _options.CandleCountPerTimeFrame)
                                }
                            })
                        }
                    }
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "trading_decision",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", @enum = new[] { "long", "short", "no_trade" } },
                            confidence = new { type = "number", minimum = 0, maximum = 1 },
                            entryPrice = new { type = "number" },
                            stopLoss = new { type = "number" },
                            takeProfit = new { type = "number" },
                            reason = new { type = "string" },
                            invalidation = new { type = "string" }
                        },
                        required = new[] { "action", "confidence", "entryPrice", "stopLoss", "takeProfit", "reason", "invalidation" },
                        additionalProperties = false
                    }
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("OpenAI analysis request failed with HTTP {StatusCode}", (int)response.StatusCode);
            return null;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var decisionJson = ExtractOutputText(responseBody);
        if (decisionJson is null)
        {
            logger.LogError("OpenAI response did not contain structured output text");
            return null;
        }

        try
        {
            var decision = JsonSerializer.Deserialize<AiDecision>(decisionJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (!IsValid(decision))
            {
                logger.LogWarning("OpenAI returned an invalid trading decision");
                return null;
            }

            logger.LogInformation("OpenAI decision {Action} with confidence {Confidence} for {Symbol}",
                decision!.Action, decision.Confidence, context.Symbol);
            return decision;
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Could not parse OpenAI structured trading decision");
            return null;
        }
    }

    private static object[] ToCompactCandles(IReadOnlyList<Candle> candles, int count) => candles
        .TakeLast(Math.Clamp(count, 20, 200))
        .Select(candle => new
        {
            t = candle.OpenTime.ToUnixTimeMilliseconds(),
            o = candle.Open,
            h = candle.High,
            l = candle.Low,
            c = candle.Close,
            v = candle.Volume
        })
        .ToArray();

    private static string? ExtractOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            return outputText.GetString();
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;

        foreach (var outputItem in output.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("type", out var type) && type.GetString() == "output_text" &&
                    contentItem.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString();
            }
        }

        return null;
    }

    private bool IsValid(AiDecision? decision)
    {
        if (decision is null || decision.Confidence < 0m || decision.Confidence > 1m) return false;
        if (decision.Action is not ("long" or "short" or "no_trade")) return false;
        if (decision.Action == "no_trade") return true;
        return decision.Confidence >= _options.MinimumConfidence && decision.EntryPrice > 0m &&
            decision.StopLoss > 0m && decision.TakeProfit > 0m &&
            decision.Reason.Length <= 2_000 && decision.Invalidation.Length <= 2_000;
    }
}
