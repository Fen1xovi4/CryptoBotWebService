namespace CryptoBotWeb.Core.DTOs;

// A filled grid slot, tracked individually so its dedicated TP closes exactly the qty bought
// at that slot's fill_price (not pooled across slots — each batch is its own mini-trade).
public class GridFloatBatch
{
    // 0 = anchor (market entry), 1..N = DCA limit fills.
    public int LevelIdx { get; set; }
    public decimal FillPrice { get; set; }
    public decimal Qty { get; set; }
    public decimal TpPrice { get; set; }
    public string? TpOrderId { get; set; }
    public DateTime FilledAt { get; set; }
}

// A live DCA limit waiting on the book at the slot's grid level.
public class GridFloatDcaOrder
{
    public int LevelIdx { get; set; }
    public decimal Price { get; set; }
    public decimal Qty { get; set; }
    public string OrderId { get; set; } = string.Empty;
}

public class GridFloatState
{
    // Long: true; Short: false. Captured at anchor open from config.Direction.
    public bool IsLong { get; set; }

    // Anchor price of the current grid (price of the most recent market entry). 0 when no
    // active grid (waiting for next bar).
    public decimal AnchorPrice { get; set; }

    // Frozen range bounds — only meaningful when config.UseStaticRange = true.
    // For Long: StaticLowerBound is the price floor below which no DCA is placed.
    // For Short: StaticUpperBound is the price ceiling above which no DCA is placed.
    // Set on the very first anchor of the bot session, untouched on subsequent anchors,
    // cleared on bot stop/start.
    public decimal StaticLowerBound { get; set; }
    public decimal StaticUpperBound { get; set; }
    public bool StaticBoundsInitialized { get; set; }

    // Open batches with their dedicated TP orders. Empty when flat.
    public List<GridFloatBatch> Batches { get; set; } = new();

    // Live DCA limits resting on the exchange. Slot k is occupied by a DCA limit XOR by a
    // batch (whose TP fill will re-arm the DCA), never both at the same time. Slot 0 (anchor)
    // is never represented here — it's always a market fill.
    public List<GridFloatDcaOrder> DcaOrders { get; set; } = new();

    // Bar gate: anchor opens only on the first closed candle whose CloseTime > this instant.
    // Set by OnFullClose to "now" (the moment the last batch closed). Cleared on first new
    // anchor. Replaces the older SkipNextCandle flag, which could under-skip or over-skip
    // depending on whether the close-detection tick happened before or after the current bar
    // close was processed.
    public DateTime? OpenAfterTime { get; set; }

    public DateTime? LastProcessedCandleTime { get; set; }
    public decimal? LastPrice { get; set; }

    // Flip to true after the first successful ProcessAsync so restart-resync runs exactly
    // once per worker boot (mirrors SmaDcaState.StateInitialized).
    public bool StateInitialized { get; set; }

    // Cumulative realized PnL across all closed batches for reporting.
    public decimal RealizedPnlDollar { get; set; }

    // On placement failure (min-notional, transient net/exchange error) skip further DCA/TP
    // (re)placements until this UTC instant — prevents tight retry storms.
    public DateTime? PlacementCooldownUntil { get; set; }
}
