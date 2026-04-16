namespace CryptoBotWeb.Core.DTOs;

public enum OrderLifecycleStatus
{
    Unknown,
    Open,           // New, NotTriggered, Live
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected
}

public class OrderStatusDto
{
    public string OrderId { get; set; } = string.Empty;
    public OrderLifecycleStatus Status { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal AverageFilledPrice { get; set; }
}
