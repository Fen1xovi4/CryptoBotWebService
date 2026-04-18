namespace CryptoBotWeb.Core.DTOs;

public class FundingPaymentDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Amount { get; set; } // positive = received, negative = paid
    public decimal FundingRate { get; set; }
    public decimal PositionSize { get; set; }
    public DateTime Timestamp { get; set; }
}
