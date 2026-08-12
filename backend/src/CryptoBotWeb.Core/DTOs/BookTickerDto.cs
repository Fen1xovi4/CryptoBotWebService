namespace CryptoBotWeb.Core.DTOs;

// Best bid/ask snapshot (top of book) for a futures symbol. Quantities are in base units;
// 0 when the exchange endpoint doesn't return sizes.
public class BookTickerDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal BidPrice { get; set; }
    public decimal BidQuantity { get; set; }
    public decimal AskPrice { get; set; }
    public decimal AskQuantity { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
