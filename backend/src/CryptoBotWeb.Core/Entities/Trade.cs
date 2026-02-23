namespace CryptoBotWeb.Core.Entities;

public class Trade
{
    public Guid Id { get; set; }
    public Guid StrategyId { get; set; }
    public Guid AccountId { get; set; }
    public string ExchangeOrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

    public Strategy Strategy { get; set; } = null!;
    public ExchangeAccount Account { get; set; } = null!;
}
