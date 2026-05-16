namespace CryptoBotWeb.Core.DTOs;

public class SmaDcaState
{
    public bool InPosition { get; set; }
    public bool IsLong { get; set; }

    public decimal TotalQuantity { get; set; }
    public decimal TotalCost { get; set; }             // invariant: TotalCost ≈ TotalQuantity * AverageEntryPrice
    public decimal AverageEntryPrice { get; set; }
    public decimal CurrentTakeProfit { get; set; }

    // Number of DCA fills done after the first entry. 0 right after Entry, increments per ScaleIn.
    public int DcaLevel { get; set; }

    public decimal LastDcaPrice { get; set; }

    // Prevents instant re-entry on the same bar after Exit (spec §2.6 / §11).
    public bool SkipNextCandle { get; set; }

    public DateTime? LastProcessedCandleTime { get; set; }
    public DateTime? PositionOpenedAt { get; set; }

    // On DCA failure (min-notional, temporary net/exchange error) skip further DCA attempts until this time.
    public DateTime? DcaCooldownUntil { get; set; }

    // Last seen market price — surfaced to the UI for progress display.
    public decimal? LastPrice { get; set; }

    // Flip to true after the first successful ProcessAsync so restart-resync runs exactly once per worker boot.
    public bool StateInitialized { get; set; }

    // Total realized PnL across all closed cycles for this bot (reporting convenience).
    public decimal RealizedPnlDollar { get; set; }

    // Id of the live reduce-only limit TP order on the exchange. Null when no active TP
    // (no position, or TP not yet placed / just cancelled during DCA replacement).
    public string? TakeProfitOrderId { get; set; }

    // Id of the pending ENTRY limit order while it awaits fill. Null when no pending entry.
    public string? EntryOrderId { get; set; }

    // Price the pending ENTRY limit was placed at (needed to compute fill-based PnL if exchange
    // returns no avgFillPrice). Also remembered so the handler can re-ask the exchange later.
    public decimal EntryOrderLimitPrice { get; set; }

    // Quantity the pending ENTRY limit asked for (rounded to step). Used to detect partial fills.
    public decimal EntryOrderQuantity { get; set; }

    // Bar at which the ENTRY limit was placed. On every new closed candle we increment the bar
    // counter implicitly through LastProcessedCandleTime; once EntryLimitTimeoutBars have passed
    // without fill, the limit is cancelled.
    public DateTime? EntryOrderPlacedAtCandleTime { get; set; }

    // Id of the pending DCA limit order while it awaits fill. Null when no pending DCA.
    public string? DcaOrderId { get; set; }

    // Price and qty the pending DCA limit was placed at.
    public decimal DcaOrderLimitPrice { get; set; }
    public decimal DcaOrderQuantity { get; set; }

    // First moment we observed the market price had crossed the TP target while the position
    // was still open. Used as a safety net for exchanges (e.g. Dzengi) where the TP is attached
    // to the position rather than placed as a reduce-only limit, and the exchange may fail to
    // trigger it. Cleared when the price falls back behind the TP or the position closes.
    public DateTime? TpCrossedAt { get; set; }

    // Last time ReconcileOrphanOrders ran (throttle — no need to poll open orders every 5s tick).
    public DateTime? LastReconcileAt { get; set; }
}
