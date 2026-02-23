using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Helpers;

public static class SymbolHelper
{
    private static readonly string[] QuoteAssets = ["USDT", "USDC", "BUSD"];

    public static string ToExchangeSymbol(string symbol, ExchangeType exchange)
    {
        return exchange switch
        {
            ExchangeType.BingX => ConvertToBingX(symbol),
            _ => symbol
        };
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
