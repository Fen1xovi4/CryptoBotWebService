namespace CryptoBotWeb.Core.DTOs;

public class OrderResultDto
{
    public bool Success { get; set; }
    public string? OrderId { get; set; }
    public decimal? FilledPrice { get; set; }
    public decimal? FilledQuantity { get; set; }
    public string? ErrorMessage { get; set; }
}
