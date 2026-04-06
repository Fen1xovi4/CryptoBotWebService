namespace CryptoBotWeb.Core.DTOs;

public class PositionDto
{
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty; // "Long" or "Short"
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
}
