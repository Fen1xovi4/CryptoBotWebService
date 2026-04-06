namespace CryptoBotWeb.Core.DTOs;

public class LimitOrderDto
{
    public string OrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public string Status { get; set; } = string.Empty; // New, PartiallyFilled, Filled, Cancelled
}
