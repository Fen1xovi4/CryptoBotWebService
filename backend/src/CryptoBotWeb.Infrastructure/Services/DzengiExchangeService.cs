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

public class DzengiExchangeService : IExchangeService, IDisposable
{
    private const string DefaultBaseUrl = "https://api-adapter.dzengi.com";

    private readonly HttpClient _http;
    private readonly HttpClientHandler _handler;
    private readonly string _apiSecret;
    private readonly string? _accountId;

    public DzengiExchangeService(string apiKey, string apiSecret, string? dzengiAccountId, ApiProxy? proxy = null)
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

    public async Task<(bool Success, string? Error)> TestConnectionAsync()
    {
        try
        {
            using var _ = await SignedGetAsync("/api/v2/account", null);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<List<BalanceDto>> GetBalancesAsync()
    {
        try
        {
            using var doc = await SignedGetAsync("/api/v2/account", null);
            var result = new List<BalanceDto>();

            if (doc.RootElement.TryGetProperty("balances", out var balances) && balances.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in balances.EnumerateArray())
                {
                    var asset = b.TryGetProperty("asset", out var a) ? a.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrEmpty(asset)) continue;
                    result.Add(new BalanceDto
                    {
                        Asset = asset,
                        Free = ParseDecimalNullable(b.TryGetProperty("free", out var f) ? f : default) ?? 0m,
                        Locked = ParseDecimalNullable(b.TryGetProperty("locked", out var l) ? l : default) ?? 0m
                    });
                }
            }
            return result;
        }
        catch
        {
            return new List<BalanceDto>();
        }
    }

    public async Task<decimal?> GetTickerPriceAsync(string symbol)
    {
        var dzengiSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Dzengi);
        try
        {
            var url = $"/api/v2/ticker/price?symbol={Uri.EscapeDataString(dzengiSymbol)}";
            using var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("price", out var p))
                return ParseDecimalNullable(p);
            return null;
        }
        catch
        {
            return null;
        }
    }

    public string? LastFetchAccountIdError { get; private set; }

    public async Task<string?> FetchPrimaryAccountIdAsync()
    {
        LastFetchAccountIdError = null;
        try
        {
            using var doc = await SignedGetAsync("/api/v2/account", null);
            var root = doc.RootElement;

            // Top-level "accountId" (rare).
            if (root.TryGetProperty("accountId", out var aid) && aid.ValueKind == JsonValueKind.String)
                return aid.GetString();

            // Actual Dzengi shape: {"balances":[{"accountId":"...","default":true,...}]}.
            // Prefer the balance flagged "default": true; fall back to the first balance.
            if (root.TryGetProperty("balances", out var balances) && balances.ValueKind == JsonValueKind.Array)
            {
                string? fallback = null;
                foreach (var b in balances.EnumerateArray())
                {
                    if (!b.TryGetProperty("accountId", out var ba) || ba.ValueKind != JsonValueKind.String) continue;
                    var id = ba.GetString();
                    if (string.IsNullOrEmpty(id)) continue;

                    var isDefault = b.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.True;
                    if (isDefault) return id;
                    fallback ??= id;
                }
                if (!string.IsNullOrEmpty(fallback)) return fallback;
            }

            LastFetchAccountIdError = "no accountId in response: " + root.GetRawText();
            return null;
        }
        catch (Exception ex)
        {
            LastFetchAccountIdError = ex.Message;
            return null;
        }
    }

    private async Task<JsonDocument> SignedGetAsync(string path, Dictionary<string, string>? query)
    {
        var parameters = query != null ? new Dictionary<string, string>(query) : new Dictionary<string, string>();
        parameters["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        parameters["recvWindow"] = "60000";

        var queryStr = BuildQueryString(parameters);
        var signature = Sign(queryStr);
        var url = $"{path}?{queryStr}&signature={signature}";

        using var resp = await _http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Dzengi {(int)resp.StatusCode}: {body}");
        return JsonDocument.Parse(body);
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

    private static decimal? ParseDecimalNullable(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Undefined || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}
