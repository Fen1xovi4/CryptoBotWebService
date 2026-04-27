using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

public class DzengiFuturesExchangeService : IFuturesExchangeService
{
    private const string DefaultBaseUrl = "https://api-adapter.dzengi.com";

    public bool UsesSoftTakeProfit => true;

    // source: https://dzengi.com/fees-charges — 0.075% per side, no maker/taker split, for non-BTC/ETH
    // leveraged crypto. BTC/ETH would be 0.06% but we don't currently trade those on Dzengi-Marjan, and
    // the strategy framework assumes one rate per exchange.
    public decimal TakerFeeRate => 0.00075m;
    public decimal MakerFeeRate => 0.00075m;

    private static readonly TimeSpan ExchangeInfoTtl = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly HttpClientHandler _handler;
    private readonly string _apiSecret;
    private string? _accountId;
    private readonly Dictionary<string, int> _leverageBySymbol = new();
    private (DateTime fetched, Dictionary<string, SymbolInfo> map)? _exchangeInfoCache;

    public DzengiFuturesExchangeService(string apiKey, string apiSecret, string? dzengiAccountId, ApiProxy? proxy = null)
    {
        _apiSecret = apiSecret;
        _accountId = dzengiAccountId;

        _handler = new HttpClientHandler();
        if (proxy != null)
        {
            var proxyUri = new Uri($"{proxy.Host}:{proxy.Port}");
            var webProxy = new WebProxy(proxyUri);
            if (!string.IsNullOrEmpty(proxy.Login) && !string.IsNullOrEmpty(proxy.Password))
                webProxy.Credentials = new NetworkCredential(proxy.Login, proxy.Password);
            _handler.Proxy = webProxy;
            _handler.UseProxy = true;
        }

        var baseUrl = Environment.GetEnvironmentVariable("DZENGI_BASE_URL") ?? DefaultBaseUrl;
        _http = new HttpClient(_handler) { BaseAddress = new Uri(baseUrl) };
        _http.DefaultRequestHeaders.Add("X-MBX-APIKEY", apiKey);
    }

    // ===================== Market data =====================

    public async Task<List<SymbolDto>> GetSymbolsAsync()
    {
        using var doc = await PublicGetAsync("/api/v2/exchangeInfo", null);
        if (!doc.RootElement.TryGetProperty("symbols", out var symbols))
            return new List<SymbolDto>();

        // Filter to crypto CFDs only (USD-quoted leverage instruments).
        // Future categories (EQUITY/COMMODITY/INDEX/CURRENCY) can be exposed via assetType.
        var list = new List<SymbolDto>();
        foreach (var sym in symbols.EnumerateArray())
        {
            var marketType = sym.TryGetProperty("marketType", out var mt) ? mt.GetString() : null;
            if (!string.Equals(marketType, "LEVERAGE", StringComparison.OrdinalIgnoreCase)) continue;

            var assetType = sym.TryGetProperty("assetType", out var at) ? at.GetString() : null;
            if (!string.Equals(assetType, "CRYPTOCURRENCY", StringComparison.OrdinalIgnoreCase)) continue;

            var quote = sym.TryGetProperty("quoteAsset", out var qa) ? qa.GetString() : null;
            if (!string.Equals(quote, "USD", StringComparison.OrdinalIgnoreCase)) continue;

            if (sym.TryGetProperty("status", out var st) && st.GetString() is string status &&
                !string.Equals(status, "TRADING", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = sym.TryGetProperty("symbol", out var sName) ? sName.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;

            list.Add(new SymbolDto { Symbol = SymbolHelper.FromDzengiSymbol(name) });
        }
        return list.OrderBy(s => s.Symbol).ToList();
    }

    public async Task<List<CandleDto>> GetKlinesAsync(string symbol, string timeframe, int limit)
    {
        var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
        var interval = MapInterval(timeframe);
        var span = SymbolHelper.GetTimeframeSpan(timeframe);

        var query = new Dictionary<string, string>
        {
            ["symbol"] = dzengiSymbol,
            ["interval"] = interval,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
        };

        using var doc = await PublicGetAsync("/api/v2/klines", query);
        var result = new List<CandleDto>();

        foreach (var k in doc.RootElement.EnumerateArray())
        {
            // Dzengi response: [openTime, open, high, low, close, volume]
            // (Binance has closeTime at index 6, but Dzengi omits it.)
            var arr = k.EnumerateArray().ToList();
            if (arr.Count < 6) continue;

            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(arr[0].GetInt64()).UtcDateTime;
            var closeTime = arr.Count >= 7 && arr[6].ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(arr[6].GetInt64()).UtcDateTime
                : openTime + span;

            result.Add(new CandleDto
            {
                OpenTime = openTime,
                Open = ParseDecimal(arr[1]),
                High = ParseDecimal(arr[2]),
                Low = ParseDecimal(arr[3]),
                Close = ParseDecimal(arr[4]),
                Volume = ParseDecimal(arr[5]),
                CloseTime = closeTime
            });
        }
        return result.OrderBy(c => c.OpenTime).ToList();
    }

    public async Task<decimal?> GetTickerPriceAsync(string symbol)
    {
        var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
        try
        {
            // Dzengi has no /ticker/price; use /ticker/24hr and read lastPrice.
            using var doc = await PublicGetAsync("/api/v2/ticker/24hr", new Dictionary<string, string>
            {
                ["symbol"] = dzengiSymbol
            });

            if (doc.RootElement.TryGetProperty("lastPrice", out var p))
                return ParseDecimalNullable(p);
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ===================== Orders =====================

    public async Task<OrderResultDto> OpenLongAsync(string symbol, decimal quoteAmount)
        => await PlaceMarketAsync(symbol, isBuy: true, quoteAmount);

    public async Task<OrderResultDto> OpenShortAsync(string symbol, decimal quoteAmount)
        => await PlaceMarketAsync(symbol, isBuy: false, quoteAmount);

    private async Task<OrderResultDto> PlaceMarketAsync(string symbol, bool isBuy, decimal quoteAmount)
    {
        try
        {
            var price = await GetTickerPriceAsync(symbol);
            if (price == null || price == 0)
                return new OrderResultDto { Success = false, ErrorMessage = "Failed to get ticker price" };

            var info = await GetSymbolInfoAsync(symbol);
            var quantity = FloorToStep(quoteAmount / price.Value, info.QtyStep);
            if (quantity < info.MinQty)
                return new OrderResultDto { Success = false, ErrorMessage = $"Qty {quantity} < min {info.MinQty} for {symbol}" };

            var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
            var leverage = _leverageBySymbol.GetValueOrDefault(symbol, 1);
            var accountId = await EnsureAccountIdAsync();

            var form = new Dictionary<string, string>
            {
                ["symbol"] = dzengiSymbol,
                ["side"] = isBuy ? "BUY" : "SELL",
                ["type"] = "MARKET",
                ["quantity"] = FormatDecimal(quantity),
                ["leverage"] = leverage.ToString(CultureInfo.InvariantCulture),
                ["newOrderRespType"] = "FULL"
            };
            if (!string.IsNullOrEmpty(accountId)) form["accountId"] = accountId;

            using var doc = await SignedRequestAsync(HttpMethod.Post, "/api/v2/order", form);
            var root = doc.RootElement;

            return new OrderResultDto
            {
                Success = true,
                OrderId = root.TryGetProperty("orderId", out var oid) ? oid.ToString() : null,
                FilledPrice = ParseDecimalNullable(root.TryGetProperty("avgPrice", out var ap) ? ap : default) ?? price,
                FilledQuantity = ParseDecimalNullable(root.TryGetProperty("executedQty", out var eq) ? eq : default) ?? quantity
            };
        }
        catch (Exception ex)
        {
            return new OrderResultDto { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<OrderResultDto> CloseLongAsync(string symbol, decimal quantity)
        => await ClosePositionAsync(symbol, "LONG", quantity);

    public async Task<OrderResultDto> CloseShortAsync(string symbol, decimal quantity)
        => await ClosePositionAsync(symbol, "SHORT", quantity);

    private async Task<OrderResultDto> ClosePositionAsync(string symbol, string direction, decimal quantity)
    {
        try
        {
            // Dzengi opens a separate trading position per market order; close every raw position
            // matching (symbol, direction) so the netted view returns to zero in one tick.
            var raws = await FindRawPositionsAsync(symbol, direction);
            if (raws.Count == 0)
                return new OrderResultDto { Success = false, ErrorMessage = $"No {direction} position for {symbol}" };

            var succeeded = new List<string>();
            var failed = new List<(string Id, string Error)>();
            decimal closedQty = 0m;

            foreach (var raw in raws)
            {
                try
                {
                    var form = new Dictionary<string, string> { ["positionId"] = raw.PositionId };
                    using var _ = await SignedRequestAsync(HttpMethod.Post, "/api/v2/closeTradingPosition", form);
                    succeeded.Add(raw.PositionId);
                    closedQty += raw.Quantity;
                }
                catch (Exception ex)
                {
                    failed.Add((raw.PositionId, ex.Message));
                }
            }

            if (failed.Count == 0)
            {
                return new OrderResultDto
                {
                    Success = true,
                    OrderId = succeeded.Count == 1
                        ? $"close-{succeeded[0]}"
                        : $"close-multi:{string.Join(",", succeeded)}",
                    FilledQuantity = closedQty
                };
            }

            var failDesc = string.Join("; ", failed.Select(f => $"{f.Id}: {f.Error}"));
            var okDesc = succeeded.Count > 0 ? $" succeeded: {string.Join(",", succeeded)}." : string.Empty;
            return new OrderResultDto
            {
                Success = false,
                OrderId = succeeded.Count > 0 ? $"close-multi:{string.Join(",", succeeded)}" : null,
                FilledQuantity = closedQty,
                ErrorMessage = $"Failed to close {failed.Count}/{raws.Count} positions ({failDesc}).{okDesc}"
            };
        }
        catch (Exception ex)
        {
            return new OrderResultDto { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<OrderResultDto> PlaceLimitOrderAsync(string symbol, string side, decimal price, decimal quantity, bool reduceOnly = false)
    {
        try
        {
            if (reduceOnly)
                return new OrderResultDto
                {
                    Success = false,
                    ErrorMessage = "Dzengi does not support reduce-only limit orders; use market close"
                };

            var info = await GetSymbolInfoAsync(symbol);
            var roundedQty = FloorToStep(quantity, info.QtyStep);
            var roundedPrice = FloorToStep(price, info.TickSize);

            if (roundedQty < info.MinQty)
                return new OrderResultDto { Success = false, ErrorMessage = $"Qty {roundedQty} < min {info.MinQty} for {symbol}" };

            var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
            var leverage = _leverageBySymbol.GetValueOrDefault(symbol, 1);
            var isBuy = side.Equals("Buy", StringComparison.OrdinalIgnoreCase);
            var accountId = await EnsureAccountIdAsync();

            var form = new Dictionary<string, string>
            {
                ["symbol"] = dzengiSymbol,
                ["side"] = isBuy ? "BUY" : "SELL",
                ["type"] = "LIMIT",
                ["timeInForce"] = "GTC",
                ["quantity"] = FormatDecimal(roundedQty),
                ["price"] = FormatDecimal(roundedPrice),
                ["leverage"] = leverage.ToString(CultureInfo.InvariantCulture),
                ["newOrderRespType"] = "FULL"
            };
            if (!string.IsNullOrEmpty(accountId)) form["accountId"] = accountId;

            using var doc = await SignedRequestAsync(HttpMethod.Post, "/api/v2/order", form);
            var root = doc.RootElement;

            return new OrderResultDto
            {
                Success = true,
                OrderId = root.TryGetProperty("orderId", out var oid) ? oid.ToString() : null,
                FilledPrice = roundedPrice,
                FilledQuantity = roundedQty
            };
        }
        catch (Exception ex)
        {
            return new OrderResultDto { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<bool> CancelOrderAsync(string symbol, string orderId)
    {
        try
        {
            var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
            var q = new Dictionary<string, string>
            {
                ["symbol"] = dzengiSymbol,
                ["orderId"] = orderId
            };
            using var __ = await SignedRequestAsync(HttpMethod.Delete, "/api/v2/order", q);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> CancelAllOrdersAsync(string symbol)
    {
        try
        {
            var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
            var q = new Dictionary<string, string>
            {
                ["symbol"] = dzengiSymbol
            };
            using var _ = await SignedRequestAsync(HttpMethod.Delete, "/api/v2/openOrders", q);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<OrderStatusDto?> GetOrderAsync(string symbol, string orderId)
    {
        try
        {
            var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
            var q = new Dictionary<string, string>
            {
                ["symbol"] = dzengiSymbol,
                ["orderId"] = orderId
            };

            using var doc = await SignedRequestAsync(HttpMethod.Get, "/api/v2/fetchOrder", q);
            var root = doc.RootElement;

            return new OrderStatusDto
            {
                OrderId = root.TryGetProperty("orderId", out var oid) ? oid.ToString() : orderId,
                Status = MapOrderStatus(root.TryGetProperty("status", out var s) ? s.GetString() : null),
                FilledQuantity = ParseDecimalNullable(root.TryGetProperty("executedQty", out var eq) ? eq : default) ?? 0m,
                AverageFilledPrice = ParseDecimalNullable(root.TryGetProperty("avgPrice", out var ap) ? ap : default) ?? 0m
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<LimitOrderDto>> GetOpenOrdersAsync(string symbol)
    {
        try
        {
            var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
            var q = new Dictionary<string, string>
            {
                ["symbol"] = dzengiSymbol
            };

            using var doc = await SignedRequestAsync(HttpMethod.Get, "/api/v2/openOrders", q);
            var list = new List<LimitOrderDto>();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                list.Add(new LimitOrderDto
                {
                    OrderId = o.TryGetProperty("orderId", out var oid) ? oid.ToString() : string.Empty,
                    Symbol = symbol,
                    Side = o.TryGetProperty("side", out var sd) ? sd.GetString() ?? string.Empty : string.Empty,
                    Price = ParseDecimalNullable(o.TryGetProperty("price", out var pr) ? pr : default) ?? 0m,
                    Quantity = ParseDecimalNullable(o.TryGetProperty("origQty", out var oq) ? oq : default) ?? 0m,
                    FilledQuantity = ParseDecimalNullable(o.TryGetProperty("executedQty", out var eq) ? eq : default) ?? 0m,
                    Status = o.TryGetProperty("status", out var st) ? st.GetString() ?? string.Empty : string.Empty
                });
            }
            return list;
        }
        catch
        {
            return new List<LimitOrderDto>();
        }
    }

    public async Task<PositionDto?> GetPositionAsync(string symbol, string side)
    {
        var direction = side.Equals("Long", StringComparison.OrdinalIgnoreCase) ? "LONG" : "SHORT";
        var position = await FindAggregatedPositionAsync(symbol, direction);
        if (position == null) return null;

        return new PositionDto
        {
            Symbol = symbol,
            Side = side,
            Quantity = position.Quantity,
            EntryPrice = position.EntryPrice,
            UnrealizedPnl = position.UnrealizedPnl
        };
    }

    public async Task<List<PositionDto>> GetOpenPositionsAsync()
    {
        // Let exceptions propagate — controller wraps as 502 with message so the UI can show the real cause.
        var raws = await GetPositionsRawAsync();
        var list = new List<PositionDto>();
        foreach (var agg in AggregatePositions(raws))
        {
            var side = agg.Direction.Equals("LONG", StringComparison.OrdinalIgnoreCase) ? "Long"
                     : agg.Direction.Equals("SHORT", StringComparison.OrdinalIgnoreCase) ? "Short"
                     : agg.Direction;

            list.Add(new PositionDto
            {
                Symbol = SymbolHelper.FromDzengiSymbol(agg.DzengiSymbol),
                Side = side,
                Quantity = agg.Quantity,
                EntryPrice = agg.EntryPrice,
                UnrealizedPnl = agg.UnrealizedPnl
            });
        }
        return list;
    }

    public Task<bool> SetLeverageAsync(string symbol, int leverage)
    {
        _leverageBySymbol[symbol] = leverage;
        return Task.FromResult(true);
    }

    // ===================== Funding (unsupported) =====================

    public Task<FundingRateDto?> GetFundingRateAsync(string symbol) =>
        throw new NotSupportedException("Dzengi has no funding rate (CFD-leverage)");

    public Task<List<FundingRateDto>> GetAllFundingRatesAsync() =>
        throw new NotSupportedException("Dzengi has no funding rate (CFD-leverage)");

    public Task<List<FundingPaymentDto>> GetFundingPaymentsAsync(string symbol, DateTime? startTime = null) =>
        throw new NotSupportedException("Dzengi has no funding rate (CFD-leverage)");

    private async Task<string?> EnsureAccountIdAsync()
    {
        if (!string.IsNullOrEmpty(_accountId)) return _accountId;
        try
        {
            using var doc = await SignedRequestAsync(HttpMethod.Get, "/api/v2/account", new Dictionary<string, string>());
            var root = doc.RootElement;

            if (root.TryGetProperty("accountId", out var aid) && aid.ValueKind == JsonValueKind.String)
                _accountId = aid.GetString();
            else if (root.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var acc in accounts.EnumerateArray())
                {
                    var type = acc.TryGetProperty("accountType", out var t) ? t.GetString() : null;
                    if (string.Equals(type, "LEVERAGE", StringComparison.OrdinalIgnoreCase) &&
                        acc.TryGetProperty("accountId", out var a) && a.ValueKind == JsonValueKind.String)
                    {
                        _accountId = a.GetString();
                        break;
                    }
                }
                if (string.IsNullOrEmpty(_accountId))
                {
                    var first = accounts.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object &&
                        first.TryGetProperty("accountId", out var fa) && fa.ValueKind == JsonValueKind.String)
                        _accountId = fa.GetString();
                }
            }
        }
        catch
        {
            // Best-effort; caller will get a clearer error from the actual order request.
        }
        return _accountId;
    }

    // ===================== HTTP helpers =====================

    private async Task<JsonDocument> PublicGetAsync(string path, Dictionary<string, string>? query)
    {
        var url = query == null || query.Count == 0 ? path : $"{path}?{BuildQueryString(query)}";
        using var resp = await _http.GetAsync(url);
        return await ReadJsonAsync(resp);
    }

    private async Task<JsonDocument> SignedRequestAsync(HttpMethod method, string path, Dictionary<string, string> parameters)
    {
        parameters["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        parameters["recvWindow"] = "60000";

        var query = BuildQueryString(parameters);
        var signature = Sign(query);
        var url = $"{path}?{query}&signature={signature}";

        using var req = new HttpRequestMessage(method, url);
        using var resp = await _http.SendAsync(req);
        return await ReadJsonAsync(resp);
    }

    private static string BuildQueryString(Dictionary<string, string> parameters)
    {
        var sb = new StringBuilder();
        foreach (var kv in parameters)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value ?? string.Empty));
        }
        return sb.ToString();
    }

    private string Sign(string totalParams)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(totalParams));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Dzengi {(int)resp.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    // ===================== Exchange info / helpers =====================

    private async Task<SymbolInfo> GetSymbolInfoAsync(string internalSymbol)
    {
        var map = await GetExchangeInfoMapAsync();
        return map.TryGetValue(internalSymbol, out var info)
            ? info
            : new SymbolInfo(0.001m, 0m, 0.01m);
    }

    private async Task<Dictionary<string, SymbolInfo>> GetExchangeInfoMapAsync()
    {
        if (_exchangeInfoCache.HasValue &&
            DateTime.UtcNow - _exchangeInfoCache.Value.fetched < ExchangeInfoTtl)
            return _exchangeInfoCache.Value.map;

        var map = new Dictionary<string, SymbolInfo>();
        using var doc = await PublicGetAsync("/api/v2/exchangeInfo", null);
        if (doc.RootElement.TryGetProperty("symbols", out var symbols))
        {
            foreach (var sym in symbols.EnumerateArray())
            {
                var marketType = sym.TryGetProperty("marketType", out var mt) ? mt.GetString() : null;
                if (!string.Equals(marketType, "LEVERAGE", StringComparison.OrdinalIgnoreCase)) continue;

                var assetType = sym.TryGetProperty("assetType", out var at) ? at.GetString() : null;
                if (!string.Equals(assetType, "CRYPTOCURRENCY", StringComparison.OrdinalIgnoreCase)) continue;

                var quote = sym.TryGetProperty("quoteAsset", out var qa) ? qa.GetString() : null;
                if (!string.Equals(quote, "USD", StringComparison.OrdinalIgnoreCase)) continue;

                var name = sym.TryGetProperty("symbol", out var sName) ? sName.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;

                decimal qtyStep = 0.001m, minQty = 0m, tickSize = 0.01m;
                if (sym.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in filters.EnumerateArray())
                    {
                        var ft = f.TryGetProperty("filterType", out var ftv) ? ftv.GetString() : null;
                        if (ft == "LOT_SIZE")
                        {
                            if (f.TryGetProperty("stepSize", out var ss)) qtyStep = ParseDecimal(ss);
                            if (f.TryGetProperty("minQty", out var mq)) minQty = ParseDecimal(mq);
                        }
                        else if (ft == "PRICE_FILTER")
                        {
                            if (f.TryGetProperty("tickSize", out var ts)) tickSize = ParseDecimal(ts);
                        }
                    }
                }

                map[SymbolHelper.FromDzengiSymbol(name)] = new SymbolInfo(qtyStep, minQty, tickSize);
            }
        }

        _exchangeInfoCache = (DateTime.UtcNow, map);
        return map;
    }

    // Synthetic netted view — PositionId is for logs/UI only, not for close routing.
    private record DzengiPosition(string PositionId, string DzengiSymbol, string Direction, decimal Quantity, decimal EntryPrice, decimal UnrealizedPnl);

    private async Task<DzengiPosition?> FindAggregatedPositionAsync(string symbol, string direction)
    {
        var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
        var raws = await GetPositionsRawAsync();
        foreach (var agg in AggregatePositions(raws))
        {
            if (!string.Equals(agg.DzengiSymbol, dzengiSymbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(agg.Direction, direction, StringComparison.OrdinalIgnoreCase)) continue;
            return agg;
        }
        return null;
    }

    private async Task<List<RawPosition>> FindRawPositionsAsync(string symbol, string direction)
    {
        var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
        var positions = await GetPositionsRawAsync();
        var list = new List<RawPosition>();
        foreach (var p in positions)
        {
            if (!string.Equals(p.Symbol, dzengiSymbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(p.Direction, direction, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Quantity == 0) continue;
            list.Add(p);
        }
        return list;
    }

    // Dzengi opens a separate trading position per market order; net them per (symbol, direction).
    private static IEnumerable<DzengiPosition> AggregatePositions(List<RawPosition> raws)
    {
        var groups = raws
            .Where(p => p.Quantity != 0 && !string.IsNullOrEmpty(p.Symbol))
            .GroupBy(p => (Symbol: p.Symbol, Direction: p.Direction.ToUpperInvariant()));

        foreach (var g in groups)
        {
            var totalQty = g.Sum(p => p.Quantity);
            if (totalQty == 0) continue;

            var weightedEntry = g.Sum(p => p.Quantity * p.EntryPrice) / totalQty;
            var totalPnl = g.Sum(p => p.UnrealizedPnl);

            var ids = g.Select(p => p.PositionId).ToList();
            var displayId = ids.Count == 1 ? ids[0] : $"{ids[0]}+{ids.Count - 1}";

            yield return new DzengiPosition(displayId, g.Key.Symbol, g.Key.Direction, totalQty, weightedEntry, totalPnl);
        }
    }

    private record RawPosition(string PositionId, string Symbol, string Direction, decimal Quantity, decimal EntryPrice, decimal UnrealizedPnl);

    private async Task<List<RawPosition>> GetPositionsRawAsync()
    {
        // Per Dzengi swagger: GET /api/v2/tradingPositions takes no accountId.
        using var doc = await SignedRequestAsync(HttpMethod.Get, "/api/v2/tradingPositions", new Dictionary<string, string>());
        var list = new List<RawPosition>();

        // Spec response: TradingPositionListResponse { positions: PositionDto[] }
        var root = doc.RootElement;
        JsonElement arrEl;
        if (root.ValueKind == JsonValueKind.Array) arrEl = root;
        else if (root.TryGetProperty("positions", out var p)) arrEl = p;
        else return list;

        foreach (var item in arrEl.EnumerateArray())
        {
            // PositionDto fields: id, symbol, openQuantity, openPrice, upl, type ("BUY"/"SELL"),
            // state, closeTimestamp.
            var posId = item.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            if (string.IsNullOrEmpty(posId)) continue;

            // Skip closed/inactive positions (state should be "OPEN" / "ACTIVE" for live ones).
            if (item.TryGetProperty("state", out var stEl) && stEl.GetString() is string state &&
                state.Length > 0 &&
                !state.Equals("OPEN", StringComparison.OrdinalIgnoreCase) &&
                !state.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (item.TryGetProperty("closeTimestamp", out var cts) &&
                cts.ValueKind == JsonValueKind.Number && cts.TryGetInt64(out var ctsVal) && ctsVal > 0)
                continue;

            var sym = item.TryGetProperty("symbol", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            var qty = ParseDecimalNullable(item.TryGetProperty("openQuantity", out var oq) ? oq : default) ?? 0m;
            var entry = ParseDecimalNullable(item.TryGetProperty("openPrice", out var op) ? op : default) ?? 0m;
            var pnl = ParseDecimalNullable(item.TryGetProperty("upl", out var up) ? up : default) ?? 0m;

            // Direction is in `type` (BUY/SELL); fall back to qty sign.
            var typeStr = item.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var direction = typeStr.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "LONG"
                          : typeStr.Equals("SELL", StringComparison.OrdinalIgnoreCase) ? "SHORT"
                          : qty >= 0 ? "LONG" : "SHORT";

            list.Add(new RawPosition(posId, sym, direction, Math.Abs(qty), entry, pnl));
        }
        return list;
    }

    // ===================== Utilities =====================

    private static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Floor(value / step) * step;
    }

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.##############", CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return 0m;
    }

    private static decimal ParseDecimal(string s)
    {
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        return 0m;
    }

    private static decimal? ParseDecimalNullable(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Undefined || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return null;
    }

    private static string MapInterval(string timeframe) => timeframe.ToLowerInvariant() switch
    {
        "1m" => "1m",
        "3m" => "3m",
        "5m" => "5m",
        "15m" => "15m",
        "30m" => "30m",
        "1h" => "1h",
        "2h" => "2h",
        "4h" => "4h",
        "6h" => "6h",
        "12h" => "12h",
        "1d" => "1d",
        "1w" => "1w",
        _ => "1h"
    };

    private static OrderLifecycleStatus MapOrderStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "NEW" => OrderLifecycleStatus.Open,
        "PARTIALLY_FILLED" => OrderLifecycleStatus.PartiallyFilled,
        "FILLED" => OrderLifecycleStatus.Filled,
        "CANCELED" => OrderLifecycleStatus.Cancelled,
        "CANCELLED" => OrderLifecycleStatus.Cancelled,
        "REJECTED" => OrderLifecycleStatus.Rejected,
        "EXPIRED" => OrderLifecycleStatus.Cancelled,
        _ => OrderLifecycleStatus.Unknown
    };

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    private record SymbolInfo(decimal QtyStep, decimal MinQty, decimal TickSize);
}
