using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Helpers;

public static class SymbolHelper
{
    private static readonly string[] QuoteAssets = ["USDT", "USDC", "BUSD"];

    public static string ToExchangeSymbol(string symbol, ExchangeType exchange)
    {
        symbol = symbol.Replace(" ", "").Trim().ToUpperInvariant();

        return exchange switch
        {
            ExchangeType.BingX => ConvertToBingX(symbol),
            ExchangeType.Dzengi => ConvertToDzengi(symbol),
            _ => symbol
        };
    }

    public static string FromDzengiSymbol(string dzengiSymbol)
    {
        // Dzengi crypto LEVERAGE pairs are quoted in USD (not USDT).
        // Map to canonical USDT-quoted form so it matches other exchanges:
        //   BTC/USD_LEVERAGE -> BTCUSDT
        var trimmed = dzengiSymbol.Replace("_LEVERAGE", "", StringComparison.OrdinalIgnoreCase);
        var compact = trimmed.Replace("/", "").ToUpperInvariant();
        if (compact.EndsWith("USD", StringComparison.Ordinal) && !compact.EndsWith("USDT", StringComparison.Ordinal))
            compact = compact[..^3] + "USDT";
        return compact;
    }

    private static string ConvertToBingX(string symbol)
    {
        foreach (var quote in QuoteAssets)
        {
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
            {
                var baseAsset = symbol[..^quote.Length];
                return $"{baseAsset}-{quote}";
            }
        }
        return symbol;
    }

    private static string ConvertToDzengi(string symbol)
    {
        // Canonical BTCUSDT -> Dzengi BTC/USD_LEVERAGE (crypto CFDs are USD-quoted, not USDT).
        if (symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase))
        {
            var baseAsset = symbol[..^4];
            return $"{baseAsset}/USD_LEVERAGE";
        }
        foreach (var quote in QuoteAssets)
        {
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
            {
                var baseAsset = symbol[..^quote.Length];
                return $"{baseAsset}/{quote}_LEVERAGE";
            }
        }
        return symbol;
    }

    public static TimeSpan GetTimeframeSpan(string timeframe)
    {
        return timeframe.ToLowerInvariant() switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "3m" => TimeSpan.FromMinutes(3),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "30m" => TimeSpan.FromMinutes(30),
            "1h" => TimeSpan.FromHours(1),
            "2h" => TimeSpan.FromHours(2),
            "4h" => TimeSpan.FromHours(4),
            "6h" => TimeSpan.FromHours(6),
            "12h" => TimeSpan.FromHours(12),
            "1d" => TimeSpan.FromDays(1),
            "1w" => TimeSpan.FromDays(7),
            _ => TimeSpan.FromHours(1)
        };
    }
}
