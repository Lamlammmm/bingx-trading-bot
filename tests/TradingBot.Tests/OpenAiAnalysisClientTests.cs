using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.OpenAI;

namespace TradingBot.Tests;

public sealed class OpenAiAnalysisClientTests
{
    [Fact]
    public async Task ParsesStructuredDecisionWithoutCallingRealOpenAi()
    {
        using var httpClient = new HttpClient(new StubHandler())
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
        var options = Options.Create(new OpenAIOptions
        {
            Enabled = true,
            ApiKey = "test-only-key",
            MinimumConfidence = 0.6m
        });
        var client = new OpenAiAnalysisClient(httpClient, options, NullLogger<OpenAiAnalysisClient>.Instance);
        var context = new AiAnalysisContext("BTC-USDT", DateTimeOffset.UtcNow,
            Array.Empty<Candle>(), Array.Empty<Candle>(), Array.Empty<Candle>(), null);

        var decision = await client.AnalyzeAsync(context, CancellationToken.None);

        Assert.NotNull(decision);
        Assert.Equal("long", decision.Action);
        Assert.Equal(0.8m, decision.Confidence);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-only-key", request.Headers.Authorization?.Parameter);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"action\\\":\\\"long\\\",\\\"confidence\\\":0.8,\\\"entryPrice\\\":100,\\\"stopLoss\\\":97,\\\"takeProfit\\\":106,\\\"reason\\\":\\\"breakout\\\",\\\"invalidation\\\":\\\"close below support\\\"}\"}]}]}", System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
