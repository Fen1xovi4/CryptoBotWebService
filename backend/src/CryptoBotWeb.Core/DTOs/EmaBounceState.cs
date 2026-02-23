namespace CryptoBotWeb.Core.DTOs;

public class EmaBounceState
{
    public int LongCounter { get; set; }
    public int ShortCounter { get; set; }
    public OpenPositionInfo? OpenLong { get; set; }
    public OpenPositionInfo? OpenShort { get; set; }
    public DateTime? LastProcessedCandleTime { get; set; }
    public bool WaitNextCandleAfterLongClose { get; set; }
    public bool WaitNextCandleAfterShortClose { get; set; }

    // Martingale state
    public int ConsecutiveLosses { get; set; }
    public decimal RunningPnlDollar { get; set; }
}

public class OpenPositionInfo
{
    public string Direction { get; set; } = string.Empty;
    public decimal EntryPrice { get; set; }
    public decimal Quantity { get; set; }
    public DateTime OpenedAt { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal StopLoss { get; set; }
    public string? ExchangeOrderId { get; set; }
    public decimal OrderSize { get; set; }
}
