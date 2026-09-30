using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Worker.Application;
using TradingBot.Worker.Configuration;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.BingX;

/// <summary>
/// Signed BingX perpetual account queries and protected Hedge-mode orders.
/// Order writes are invoked only by the enabled live engine; preflight uses queries only.
/// </summary>
public sealed class BingXLiveExecutor(HttpClient httpClient, IOptions<BingXOptions> options,
    ILogger<BingXLiveExecutor> logger) : ILiveExecutor
{
    private readonly BingXOptions _options = options.Value;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _nextRequest;

    public async Task<string> GetPositionHistoryAsync(string symbol, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow;
        if (until - since > TimeSpan.FromDays(89))
            throw new InvalidOperationException("Position history window exceeds 89 days; manual history reconciliation required.");
        var rows = new List<JsonElement>();
        for (var page = 1; page <= 100; page++)
        {
            using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v1/trade/positionHistory", new()
            {
                ["symbol"] = symbol,
                ["startTs"] = since.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["endTs"] = until.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pageIndex"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pageSize"] = "100"
            }, cancellationToken);
            var data = document.RootElement.GetProperty("data");
            var list = data.TryGetProperty("positionHistory", out var history) ? history : data.GetProperty("list");
            foreach (var row in list.EnumerateArray()) rows.Add(row.Clone());
            if (list.GetArrayLength() < 100)
            {
                if (rows.Count == 0)
                    logger.LogWarning("No closed position history returned for {Symbol} since {Since}; recording empty exchange result", symbol, since);
                return JsonSerializer.Serialize(new { Symbol = symbol, From = since, Until = until,
                    HistoryAvailable = rows.Count > 0,
                    Attribution = "Raw exchange history for symbol and time window; empty Positions means no history was available at query time. Match position/order IDs before calculating trade PnL.",
                    Positions = rows });
            }
        }
        throw new InvalidOperationException("Position history pagination limit reached; refusing incomplete export.");
    }

    public async Task ValidatePositionModeAsync(CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v1/positionSide/dual", new(), cancellationToken);
        var mode = document.RootElement.GetProperty("data").GetProperty("dualSidePosition");
        var hedge = mode.ValueKind == JsonValueKind.True ||
            (mode.ValueKind == JsonValueKind.String && bool.TryParse(mode.GetString(), out var value) && value);
        if (!hedge)
            throw new InvalidOperationException("Live trading requires Hedge position mode. Change it in BingX before starting.");
    }

    public async Task<int> GetOpenOrderCountAsync(CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/trade/openOrders", new(), cancellationToken);
        var data = document.RootElement.GetProperty("data");
        var orders = data.ValueKind == JsonValueKind.Array ? data : data.GetProperty("orders");
        return orders.GetArrayLength();
    }

    public async Task ValidateProtectionAsync(string symbol, TradeDirection direction, decimal quantity, CancellationToken cancellationToken)
    {
        using var positions = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/user/positions",
            new() { ["symbol"] = symbol }, cancellationToken);
        var expectedSide = direction == TradeDirection.Long ? "LONG" : "SHORT";
        var matching = positions.RootElement.GetProperty("data").EnumerateArray()
            .Where(p => TryFindDecimal(p, "positionAmt", out var amount) && amount != 0m).ToArray();
        if (matching.Length != 1 || matching[0].GetProperty("positionSide").GetString() != expectedSide ||
            !TryFindDecimal(matching[0], "positionAmt", out var actual) || Math.Abs(actual) != quantity)
            throw new InvalidOperationException("Exchange position does not match saved live state.");

        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/trade/openOrders",
            new() { ["symbol"] = symbol }, cancellationToken);
        var data = document.RootElement.GetProperty("data");
        var orders = data.ValueKind == JsonValueKind.Array ? data : data.GetProperty("orders");
        var types = new HashSet<string>();
        foreach (var order in orders.EnumerateArray())
        {
            if (order.GetProperty("symbol").GetString() == symbol &&
                order.GetProperty("positionSide").GetString() == expectedSide &&
                order.GetProperty("side").GetString() == (direction == TradeDirection.Long ? "SELL" : "BUY") &&
                TryFindDecimal(order, "stopPrice", out var trigger) && trigger > 0m &&
                TryFindDecimal(order, "origQty", out var protectedQuantity) && protectedQuantity >= quantity)
                types.Add(order.GetProperty("type").GetString()!);
        }
        if (!types.Contains("STOP_MARKET") || !types.Contains("TAKE_PROFIT_MARKET"))
            throw new InvalidOperationException("Exchange position is missing full-quantity SL/TP protection. Check BingX immediately.");
    }

    public async Task<decimal> GetAvailableBalanceUsdtAsync(CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v3/user/balance", new Dictionary<string, string>(), cancellationToken);
        foreach (var balance in document.RootElement.GetProperty("data").EnumerateArray())
        {
            if (balance.GetProperty("asset").GetString() == "USDT" &&
                TryFindDecimal(balance, "availableMargin", out var available)) return available;
        }
        throw new InvalidOperationException("BingX response has no available USDT margin.");
    }

    public async Task<int> GetOpenPositionCountAsync(CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/user/positions", new Dictionary<string, string>(), cancellationToken);
        return CountOpenPositions(document.RootElement);
    }

    public async Task<bool> HasOpenPositionAsync(string symbol, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/user/positions",
            new Dictionary<string, string> { ["symbol"] = symbol }, cancellationToken);
        return CountOpenPositions(document.RootElement) > 0;
    }

    public async Task<decimal> GetMaxLeverageAsync(string symbol, TradeDirection direction, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/trade/leverage",
            new() { ["symbol"] = symbol }, cancellationToken);
        var field = direction == TradeDirection.Long ? "maxLongLeverage" : "maxShortLeverage";
        if (!TryFindDecimal(document.RootElement.GetProperty("data"), field, out var maximum) || maximum < 1m)
            throw new InvalidOperationException("Missing or invalid exchange leverage limit.");
        return decimal.Floor(maximum);
    }

    public async Task SetLeverageAsync(string symbol, TradeDirection direction, decimal leverage, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Post, "/openApi/swap/v2/trade/leverage", new Dictionary<string, string>
        {
            ["symbol"] = symbol,
            ["side"] = direction == TradeDirection.Long ? "LONG" : "SHORT",
            ["leverage"] = ((int)leverage).ToString()
        }, cancellationToken);
    }

    public async Task<LiveOrderReceipt> OpenMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity,
        decimal stopLoss, decimal takeProfit, string clientOrderId, CancellationToken cancellationToken)
    {
        if (quantity <= 0m || stopLoss <= 0m || takeProfit <= 0m ||
            (direction == TradeDirection.Long ? stopLoss >= takeProfit : stopLoss <= takeProfit))
            throw new ArgumentException("Invalid protected order quantity or trigger prices.");
        using var document = await SendAsync(HttpMethod.Post, "/openApi/swap/v2/trade/order", new Dictionary<string, string>
        {
            ["symbol"] = symbol,
            ["side"] = direction == TradeDirection.Long ? "BUY" : "SELL",
            ["positionSide"] = direction == TradeDirection.Long ? "LONG" : "SHORT",
            ["type"] = "MARKET",
            ["quantity"] = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["clientOrderId"] = clientOrderId,
            ["stopLoss"] = JsonSerializer.Serialize(new { type = "STOP_MARKET", stopPrice = stopLoss, workingType = "MARK_PRICE" }),
            ["takeProfit"] = JsonSerializer.Serialize(new { type = "TAKE_PROFIT_MARKET", stopPrice = takeProfit, workingType = "MARK_PRICE" })
        }, cancellationToken);
        return new LiveOrderReceipt(TryFindDecimal(document.RootElement, "avgPrice", out var avgPrice) && avgPrice > 0m ? avgPrice : null,
            document.RootElement.GetProperty("data").GetRawText());
    }

    public async Task<LiveOrderReceipt> CloseMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity, CancellationToken cancellationToken)
    {
        // In Hedge mode, the opposite side closes the selected position; reduceOnly is unsupported.
        using var document = await SendAsync(HttpMethod.Post, "/openApi/swap/v2/trade/order", new Dictionary<string, string>
        {
            ["symbol"] = symbol,
            ["side"] = direction == TradeDirection.Long ? "SELL" : "BUY",
            ["positionSide"] = direction == TradeDirection.Long ? "LONG" : "SHORT",
            ["type"] = "MARKET",
            ["quantity"] = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, cancellationToken);
        return new LiveOrderReceipt(TryFindDecimal(document.RootElement, "avgPrice", out var avgPrice) && avgPrice > 0m ? avgPrice : null,
            document.RootElement.GetProperty("data").GetRawText());
    }

    private static int CountOpenPositions(JsonElement root)
    {
        var array = root.GetProperty("data");
        if (array.ValueKind != JsonValueKind.Array) throw new JsonException("Expected positions array.");
        var count = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (!TryFindDecimal(item, "positionAmt", out var amount))
                throw new JsonException("Position amount is missing or invalid.");
            if (amount != 0m)
            {
                count++;
            }
        }
        return count;
    }

    private static bool TryFindDecimal(JsonElement element, string propertyName, out decimal value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out value)) return true;
                        if (property.Value.ValueKind == JsonValueKind.String &&
                            decimal.TryParse(property.Value.GetString(), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out value)) return true;
                    }
                    if (TryFindDecimal(property.Value, propertyName, out value)) return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (TryFindDecimal(item, propertyName, out value)) return true;
                break;
        }
        value = 0m;
        return false;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ApiSecret))
            throw new InvalidOperationException("BingX ApiKey/ApiSecret are required for live trading.");

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = _nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            _nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(600);
        }
        finally { _requestGate.Release(); }
        parameters["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        // BingX signs raw values; URL encoding applies only to transport, especially SL/TP JSON.
        var canonical = string.Join('&', parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));
        var sortedQuery = string.Join('&', parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
        var signature = Sign(canonical, _options.ApiSecret);
        var url = $"{path}?{sortedQuery}&signature={signature}";

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-BX-APIKEY", _options.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogInformation("LIVE API {Method} {Path} status={Status}", method, path, (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"BingX live API call failed: {(int)response.StatusCode}");

        var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("code", out var codeProperty) ||
            codeProperty.ValueKind != JsonValueKind.Number || !codeProperty.TryGetInt32(out var code) || code != 0)
        {
            var errorCode = codeProperty.ValueKind == JsonValueKind.Number ? codeProperty.ToString() : "invalid-code";
            document.Dispose();
            throw new InvalidOperationException($"BingX live API rejected {path}: code={errorCode}.");
        }
        return document;
    }

    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
