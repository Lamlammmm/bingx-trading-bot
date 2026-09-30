using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;
using TradingBot.Worker.Infrastructure.BingX;

namespace TradingBot.Tests;

public sealed class BingXLiveExecutorTests
{
    [Theory]
    [InlineData("positionHistory")]
    [InlineData("list")]
    public async Task RetainsRawClosedPositionHistory(string field)
    {
        using var client = new HttpClient(new ResponseHandler("{\"" + field + "\":[{\"positionId\":\"12345678901234567890\",\"netProfit\":\"0.15\"}]}"))
            { BaseAddress = new Uri("https://example.test") };
        var executor = new BingXLiveExecutor(client, Options.Create(new BingXOptions { ApiKey = "test", ApiSecret = "test" }),
            NullLogger<BingXLiveExecutor>.Instance);
        var json = await executor.GetPositionHistoryAsync("SOL-USDT", DateTimeOffset.UtcNow.AddHours(-1), default);
        Assert.Contains("12345678901234567890", json);
        Assert.Contains("0.15", json);
    }

    [Fact]
    public async Task EmptyHistoryIsRecordedAsUnavailable()
    {
        using var client = new HttpClient(new ResponseHandler("{\"positionHistory\":[]}"))
            { BaseAddress = new Uri("https://example.test") };
        var executor = new BingXLiveExecutor(client, Options.Create(new BingXOptions { ApiKey = "test", ApiSecret = "test" }),
            NullLogger<BingXLiveExecutor>.Instance);
        var json = await executor.GetPositionHistoryAsync("SOL-USDT", DateTimeOffset.UtcNow.AddHours(-1), default);
        using var result = JsonDocument.Parse(json);
        Assert.False(result.RootElement.GetProperty("HistoryAvailable").GetBoolean());
        Assert.Empty(result.RootElement.GetProperty("Positions").EnumerateArray());
    }
    [Theory]
    [InlineData(TradeDirection.Long, 8)]
    [InlineData(TradeDirection.Short, 4)]
    public async Task ReadsLeverageLimitForRequestedDirection(TradeDirection direction, int expected)
    {
        using var client = new HttpClient(new ResponseHandler("{\"maxLongLeverage\":\"8\",\"maxShortLeverage\":4}"))
            { BaseAddress = new Uri("https://example.test") };
        var executor = new BingXLiveExecutor(client, Options.Create(new BingXOptions { ApiKey = "test", ApiSecret = "test" }),
            NullLogger<BingXLiveExecutor>.Instance);
        Assert.Equal(expected, await executor.GetMaxLeverageAsync("BTC-USDT", direction, default));
    }

    [Fact]
    public async Task MissingLeverageLimitFailsClosed()
    {
        using var client = new HttpClient(new ResponseHandler("{}")) { BaseAddress = new Uri("https://example.test") };
        var executor = new BingXLiveExecutor(client, Options.Create(new BingXOptions { ApiKey = "test", ApiSecret = "test" }),
            NullLogger<BingXLiveExecutor>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.GetMaxLeverageAsync("BTC-USDT", TradeDirection.Long, default));
    }
    [Theory]
    [InlineData("true")]
    [InlineData("\"true\"")]
    public async Task AcceptsBooleanAndStringHedgeMode(string mode)
    {
        using var client = new HttpClient(new ResponseHandler("{\"dualSidePosition\":" + mode + "}"))
            { BaseAddress = new Uri("https://example.test") };
        var executor = new BingXLiveExecutor(client, Options.Create(new BingXOptions { ApiKey = "test", ApiSecret = "test" }),
            NullLogger<BingXLiveExecutor>.Instance);
        await executor.ValidatePositionModeAsync(default);
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[{\"positionAmt\":\"0.02\"}]", 1)]
    [InlineData("[{\"positionAmt\":-0.02}]", 1)]
    public async Task CountsNumericAndStringPositionAmounts(string data, int expected)
    {
        using var client = new HttpClient(new ResponseHandler(data)) { BaseAddress = new Uri("https://example.test") };
        var executor = new BingXLiveExecutor(client, Options.Create(new BingXOptions { ApiKey = "test", ApiSecret = "test" }),
            NullLogger<BingXLiveExecutor>.Instance);
        Assert.Equal(expected, await executor.GetOpenPositionCountAsync(default));
    }

    [Fact]
    public async Task ProtectedEntrySerializesNumericTriggersAndCloseOmitsReduceOnly()
    {
        var handler = new ResponseHandler("{\"avgPrice\":100}");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var executor = new BingXLiveExecutor(client, Options.Create(new BingXOptions { ApiKey = "test", ApiSecret = "test" }),
            NullLogger<BingXLiveExecutor>.Instance);
        await executor.OpenMarketPositionAsync("SOL-USDT", TradeDirection.Long, 0.02m, 90m, 110m, "bot-test", default);
        var query = handler.LastUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        using var stop = JsonDocument.Parse(query["stopLoss"]);
        using var take = JsonDocument.Parse(query["takeProfit"]);
        Assert.Equal(JsonValueKind.Number, stop.RootElement.GetProperty("stopPrice").ValueKind);
        Assert.Equal(90m, stop.RootElement.GetProperty("stopPrice").GetDecimal());
        Assert.Equal(110m, take.RootElement.GetProperty("stopPrice").GetDecimal());
        Assert.Equal("bot-test", query["clientOrderId"]);
        var canonical = string.Join('&', query.Where(p => p.Key != "signature")
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        var expectedSignature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("test"), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        Assert.Equal(expectedSignature, query["signature"]);
        Assert.Contains("%7B", handler.LastUri.Query, StringComparison.OrdinalIgnoreCase);
        await executor.CloseMarketPositionAsync("SOL-USDT", TradeDirection.Long, 0.02m, default);
        Assert.DoesNotContain("reduceOnly", handler.LastUri!.Query);
        Assert.Contains("side=SELL", handler.LastUri.Query);
    }
    [Theory]
    [InlineData("[{\"asset\":\"USDC\",\"availableMargin\":\"100\"},{\"asset\":\"USDT\",\"availableMargin\":\"1.0145\"}]", "1.0145")]
    [InlineData("[{\"asset\":\"USDT\",\"balance\":\"100\",\"availableMargin\":0}]", "0")]
    public async Task ReadsOnlyAvailableUsdtMargin(string data, string expected)
    {
        using var client = new HttpClient(new ResponseHandler(data)) { BaseAddress = new Uri("https://example.test") };
        var executor = new BingXLiveExecutor(client,
            Options.Create(new BingXOptions { ApiKey = "test", ApiSecret = "test" }),
            NullLogger<BingXLiveExecutor>.Instance);

        var actual = await executor.GetAvailableBalanceUsdtAsync(CancellationToken.None);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), actual);
    }

    [Fact]
    public async Task MissingUsdtFailsInsteadOfSpendingAnotherAsset()
    {
        using var client = new HttpClient(new ResponseHandler("[{\"asset\":\"USDC\",\"availableMargin\":\"100\"}]"))
            { BaseAddress = new Uri("https://example.test") };
        var executor = new BingXLiveExecutor(client,
            Options.Create(new BingXOptions { ApiKey = "test", ApiSecret = "test" }),
            NullLogger<BingXLiveExecutor>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.GetAvailableBalanceUsdtAsync(CancellationToken.None));
    }

    private sealed class ResponseHandler(string data) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"code\":0,\"data\":" + data + "}")
            });
        }
    }
}
