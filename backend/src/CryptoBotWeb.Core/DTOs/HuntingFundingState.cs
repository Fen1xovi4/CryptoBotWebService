namespace CryptoBotWeb.Core.DTOs;

public enum HuntingFundingPhase
{
    WaitingForFunding = 0,
    OrdersPlaced = 1,
    InPosition = 2,
    Cooldown = 3
}

public class HuntingFundingState
{
    public HuntingFundingPhase Phase { get; set; } = HuntingFundingPhase.WaitingForFunding;
    public string? Direction { get; set; } // "Long" or "Short"
    public decimal? CurrentFundingRate { get; set; }
    public DateTime? NextFundingTime { get; set; }
    public DateTime? OrdersPlacedAt { get; set; }
    public List<PlacedOrderInfo> PlacedOrders { get; set; } = new();
    public decimal? AvgEntryPrice { get; set; }
    public decimal? TotalFilledQuantity { get; set; }
    public decimal? TotalFilledUsdt { get; set; }
    public decimal? TakeProfit { get; set; }
    public decimal? StopLoss { get; set; }
    public DateTime? PositionOpenedAt { get; set; }
    public int CycleCount { get; set; }
    public decimal CycleTotalPnl { get; set; }
    public decimal? LastPrice { get; set; }
    public DateTime? CooldownUntil { get; set; }
    public bool RemainingOrdersCancelled { get; set; }
    public DateTime? LastSkipLogAt { get; set; } // throttle for pre-funding skip diagnostics
}

public class PlacedOrderInfo
{
    public string OrderId { get; set; } = string.Empty;
    public int LevelIndex { get; set; }
    public string Side { get; set; } = string.Empty; // "Buy" or "Sell"
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public bool IsFilled { get; set; }
}
