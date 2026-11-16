using System.Globalization;
using System.Text.Json;
using TradingBot.Worker.Domain;

namespace TradingBot.Worker.Infrastructure.BingX;

public sealed partial class BingXLiveExecutor
{
    public async Task<LiveAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v3/user/balance", new(), cancellationToken);
        var balances = document.RootElement.GetProperty("data").EnumerateArray().Where(b => b.GetProperty("asset").GetString() == "USDT").ToArray();
        if (balances.Length != 1) throw new InvalidOperationException("Expected exactly one USDT balance.");
        var wallet = RequiredDecimal(balances[0], "balance");
        var equity = RequiredDecimal(balances[0], "equity");
        var available = RequiredDecimal(balances[0], "availableMargin");
        var now = DateTimeOffset.UtcNow;
        var rows = await ReadIncomeAsync(null, new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero), now, cancellationToken);
        var allIncome = rows.Sum(r => RequiredDecimal(r, "income"));
        var tradingIncome = rows.Where(r => r.GetProperty("incomeType").GetString() is not ("TRANSFER" or "TRIAL_FUND"))
            .Sum(r => RequiredDecimal(r, "income"));
        using var commission = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/user/commissionRate", new(), cancellationToken);
        var fee = RequiredDecimal(commission.RootElement.GetProperty("data").GetProperty("commission"), "takerCommissionRate");
        if (fee < 0m || fee >= 1m) throw new InvalidOperationException("Invalid taker commission rate.");
        return new(wallet, equity, available, wallet - allIncome, tradingIncome, fee, allIncome - tradingIncome);
    }

    public async Task<LiveIncomeSnapshot> GetIncomeAsync(string symbol, DateTimeOffset since, CancellationToken cancellationToken)
        => await GetIncomeAsync(symbol, since, DateTimeOffset.UtcNow, cancellationToken);

    public async Task<LiveIncomeSnapshot> GetIncomeAsync(string symbol, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var rows = await ReadIncomeAsync(symbol, since, until, cancellationToken);
        var trading = rows.Where(r => r.GetProperty("incomeType").GetString() is not ("TRANSFER" or "TRIAL_FUND")).ToArray();
        return new(trading.Sum(r => RequiredDecimal(r, "income")),
            trading.Where(r => r.GetProperty("incomeType").GetString() == "FUNDING_FEE").Sum(r => RequiredDecimal(r, "income")),
            trading.Where(r => r.GetProperty("incomeType").GetString() == "TRADING_FEE").Sum(r => RequiredDecimal(r, "income")), trading.Length,
            trading.Any(r => r.GetProperty("incomeType").GetString() == "REALIZED_PNL"),
            trading.Any(r => r.GetProperty("incomeType").GetString() == "TRADING_FEE"),
            trading.Where(r => r.GetProperty("incomeType").GetString() == "REALIZED_PNL").Sum(r => RequiredDecimal(r, "income")));
    }

    private async Task<JsonElement[]> ReadIncomeAsync(string? symbol, DateTimeOffset since, DateTimeOffset until, CancellationToken token)
    {
        if (since > until || until - since > TimeSpan.FromDays(89)) throw new InvalidOperationException("Invalid income history window.");
        var parameters = new Dictionary<string, string>
        {
            ["startTime"] = since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["endTime"] = until.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ["limit"] = "1000"
        };
        if (symbol is not null) parameters["symbol"] = symbol;
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/user/income", parameters, token);
        var data = document.RootElement.GetProperty("data");
        if (data.GetArrayLength() >= 1000) throw new InvalidOperationException("Income response reached its limit; refusing an incomplete risk ledger.");
        var rows = data.EnumerateArray().Where(r => r.GetProperty("asset").GetString() == "USDT").Select(r => r.Clone()).ToArray();
        foreach (var row in rows)
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("time").GetInt64());
            if (time < since.AddMilliseconds(-1) || time > until ||
                (symbol is not null && row.GetProperty("symbol").GetString() != symbol) ||
                row.GetProperty("incomeType").GetString() is not ("TRANSFER" or "TRIAL_FUND" or "REALIZED_PNL" or "TRADING_FEE" or
                    "FUNDING_FEE" or "INSURANCE_CLEAR" or "ADL" or "SYSTEM_DEDUCTION" or "GTD_PRICE"))
                throw new InvalidOperationException("Income response contains an unexpected time, symbol or flow type.");
        }
        return rows;
    }

    public async Task<decimal> GetMarkPriceAsync(string symbol, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/quote/premiumIndex", new() { ["symbol"] = symbol }, cancellationToken);
        var data = document.RootElement.GetProperty("data");
        if (data.ValueKind == JsonValueKind.Array) data = data.EnumerateArray().Single(r => r.GetProperty("symbol").GetString() == symbol);
        if (data.GetProperty("symbol").GetString() != symbol) throw new InvalidOperationException("Mark price symbol mismatch.");
        var price = RequiredDecimal(data, "markPrice");
        var time = DateTimeOffset.FromUnixTimeMilliseconds(data.GetProperty("time").GetInt64());
        if (price <= 0m || DateTimeOffset.UtcNow - time > TimeSpan.FromSeconds(30) || time > DateTimeOffset.UtcNow.AddSeconds(10))
            throw new InvalidOperationException("Mark price is invalid or stale.");
        return price;
    }

    public async Task<LiveManagedPosition> GetManagedPositionAsync(string symbol, TradeDirection direction, decimal quantity,
        decimal stopLoss, decimal takeProfit, string? stopOrderId, string? takeOrderId, CancellationToken cancellationToken,
        bool allowOversizedProtection = false)
    {
        var position = await GetPositionAsync(symbol, cancellationToken);
        if (position.Direction != direction || position.Quantity != quantity)
            throw new InvalidOperationException("Live position direction or quantity mismatch.");
        var expectedSide = direction == TradeDirection.Long ? "LONG" : "SHORT";
        var entry = position.EntryPrice;
        var positionId = position.PositionId;
        using var orders = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/trade/openOrders", new() { ["symbol"] = symbol }, cancellationToken);
        var data = orders.RootElement.GetProperty("data");
        var list = (data.ValueKind == JsonValueKind.Array ? data : data.GetProperty("orders")).EnumerateArray().ToArray();
        // Do not adopt, cancel, or replace additional/manual orders on this symbol.
        if (list.Length != 2) throw new InvalidOperationException("Expected only the bot's two protective orders.");
        string? stopId = null, takeId = null;
        var stopClosesPosition = false;
        foreach (var order in list)
        {
            var id = order.GetProperty("orderId").ToString();
            if (string.IsNullOrWhiteSpace(id) || order.GetProperty("symbol").GetString() != symbol ||
                order.GetProperty("positionSide").GetString() != expectedSide ||
                order.GetProperty("side").GetString() != (direction == TradeDirection.Long ? "SELL" : "BUY") ||
                (allowOversizedProtection ? RequiredDecimal(order, "origQty") < quantity : RequiredDecimal(order, "origQty") != quantity) ||
                order.GetProperty("workingType").GetString() != "MARK_PRICE")
                throw new InvalidOperationException("Protective order identity, quantity or trigger source mismatch.");
            var type = order.GetProperty("type").GetString();
            var trigger = RequiredDecimal(order, "stopPrice");
            if (type == "STOP_MARKET" && trigger == stopLoss && (stopOrderId is null || id == stopOrderId))
            {
                stopId = id;
                stopClosesPosition = order.TryGetProperty("closePosition", out var closeAll) && closeAll.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (type == "TAKE_PROFIT_MARKET" && trigger == takeProfit && (takeOrderId is null || id == takeOrderId)) takeId = id;
            else throw new InvalidOperationException("Unrecognized protective order or changed trigger price.");
        }
        if (stopId is null || takeId is null || stopId == takeId) throw new InvalidOperationException("Missing uniquely identified SL/TP.");
        return new(positionId, symbol, direction, quantity, entry, stopId, takeId, stopClosesPosition);
    }

    public async Task<LivePositionSnapshot> GetPositionAsync(string symbol, CancellationToken cancellationToken)
    {
        using var positions = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/user/positions", new() { ["symbol"] = symbol }, cancellationToken);
        var rows = positions.RootElement.GetProperty("data").EnumerateArray()
            .Where(p => RequiredDecimal(p, "positionAmt") != 0m).ToArray();
        if (rows.Length != 1 || rows[0].GetProperty("symbol").GetString() != symbol ||
            rows[0].GetProperty("positionSide").GetString() is not ("LONG" or "SHORT"))
            throw new InvalidOperationException("Live position identity, direction or quantity mismatch.");
        var entry = RequiredDecimal(rows[0], "avgPrice");
        var positionId = rows[0].GetProperty("positionId").ToString();
        if (entry <= 0m || string.IsNullOrWhiteSpace(positionId)) throw new InvalidOperationException("Missing live entry/position ID.");
        return new(positionId, symbol, rows[0].GetProperty("positionSide").GetString() == "LONG" ? TradeDirection.Long : TradeDirection.Short,
            Math.Abs(RequiredDecimal(rows[0], "positionAmt")), entry);
    }

    public async Task<LiveOrderFill> GetOrderFillAsync(string symbol, string clientOrderId, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/trade/order",
            new() { ["symbol"] = symbol, ["clientOrderId"] = clientOrderId }, cancellationToken);
        var data = document.RootElement.GetProperty("data");
        if (data.TryGetProperty("order", out var nested)) data = nested;
        return new(data.GetProperty("orderId").ToString(), data.GetProperty("clientOrderId").GetString() ?? "",
            data.GetProperty("symbol").GetString() ?? "", data.GetProperty("side").GetString() ?? "",
            data.GetProperty("positionSide").GetString() ?? "", data.GetProperty("status").GetString() ?? "",
            RequiredDecimal(data, "executedQty"), RequiredDecimal(data, "avgPrice"));
    }

    public async Task<LiveOrderReceipt> CloseMarketPositionAsync(string symbol, TradeDirection direction, decimal quantity,
        string clientOrderId, CancellationToken cancellationToken)
    {
        if (quantity <= 0m || string.IsNullOrWhiteSpace(clientOrderId) || clientOrderId.Length > 40) throw new ArgumentException("Invalid partial close.");
        using var document = await SendAsync(HttpMethod.Post, "/openApi/swap/v2/trade/order", new()
        {
            ["symbol"] = symbol, ["side"] = direction == TradeDirection.Long ? "SELL" : "BUY",
            ["positionSide"] = direction == TradeDirection.Long ? "LONG" : "SHORT", ["type"] = "MARKET",
            ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture), ["clientOrderId"] = clientOrderId
        }, cancellationToken);
        return new(TryFindDecimal(document.RootElement, "avgPrice", out var average) && average > 0m ? average : null,
            document.RootElement.GetProperty("data").GetRawText());
    }

    public async Task<string> ReplaceProtectionAsync(string symbol, TradeDirection direction, string orderId, string orderType,
        decimal quantity, decimal triggerPrice, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(orderId) || orderType is not ("STOP_MARKET" or "TAKE_PROFIT_MARKET") || quantity <= 0m || triggerPrice <= 0m)
            throw new ArgumentException("Invalid protective replacement.");
        using var document = await SendAsync(HttpMethod.Post, "/openApi/swap/v1/trade/cancelReplace", new()
        {
            ["symbol"] = symbol, ["cancelOrderId"] = orderId,
            ["side"] = direction == TradeDirection.Long ? "SELL" : "BUY",
            ["positionSide"] = direction == TradeDirection.Long ? "LONG" : "SHORT", ["type"] = orderType,
            ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture), ["stopPrice"] = triggerPrice.ToString(CultureInfo.InvariantCulture),
            ["workingType"] = "MARK_PRICE", ["cancelReplaceMode"] = "STOP_ON_FAILURE", ["closePosition"] = "true"
        }, cancellationToken);
        var data = document.RootElement.GetProperty("data");
        if (orderType == "STOP_MARKET" && data.GetProperty("cancelResult").GetString() == "SUCCESS" &&
            data.GetProperty("newOrderResult").GetString() == "FAILED")
            throw new LiveProtectionReplacementException("Exchange confirmed cancellation of the old SL and rejection of its replacement.");
        if (data.GetProperty("cancelResult").GetString() != "SUCCESS" || data.GetProperty("newOrderResult").GetString() != "SUCCESS")
            throw new InvalidOperationException("Protective cancel/replace was not fully successful; reconcile protection immediately.");
        var id = data.GetProperty("newOrderId").ToString();
        if (string.IsNullOrWhiteSpace(id) || id == orderId) throw new InvalidOperationException("Invalid replacement order ID.");
        return id;
    }

    public async Task CancelKnownProtectionAsync(string symbol, IReadOnlyCollection<string> orderIds, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "/openApi/swap/v2/trade/openOrders", new() { ["symbol"] = symbol }, cancellationToken);
        var data = document.RootElement.GetProperty("data");
        var orders = (data.ValueKind == JsonValueKind.Array ? data : data.GetProperty("orders")).EnumerateArray().ToArray();
        if (orders.Any(o => o.GetProperty("symbol").GetString() != symbol || !orderIds.Contains(o.GetProperty("orderId").ToString()) ||
            o.GetProperty("type").GetString() is not ("STOP_MARKET" or "TAKE_PROFIT_MARKET")))
            throw new InvalidOperationException("Unknown order found during bot protection cleanup; no orders were cancelled.");
        foreach (var order in orders)
        {
            using var response = await SendAsync(HttpMethod.Delete, "/openApi/swap/v2/trade/order",
                new() { ["symbol"] = symbol, ["orderId"] = order.GetProperty("orderId").ToString() }, cancellationToken);
        }
    }

    private static decimal RequiredDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            !decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            throw new InvalidOperationException($"Missing or invalid exchange field: {property}.");
        return result;
    }
}
