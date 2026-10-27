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

    /// <summary>
    /// Total fee the exchange charged for this order so far, in the quote asset, as a positive
    /// number. Null when the exchange does not report it on the order — callers then estimate
    /// from the fee rate. Exchanges disagree on the sign convention (BingX/Bitget report fees as
    /// negative numbers, Bybit as positive), so implementations normalise to the absolute value.
    /// </summary>
    public decimal? Fee { get; set; }

    /// <summary>
    /// Asset the <see cref="Fee"/> is denominated in, when the exchange reports it. Spot clients
    /// fill this: a spot BUY is typically charged in the base coin (the wallet receives
    /// FilledQuantity − Fee), a SELL in the quote coin. Null = unknown / assume quote.
    /// </summary>
    public string? FeeAsset { get; set; }
}
