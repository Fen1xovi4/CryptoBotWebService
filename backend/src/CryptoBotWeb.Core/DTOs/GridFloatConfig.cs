namespace CryptoBotWeb.Core.DTOs;

// Floating grid strategy. After a first market entry, the bot lays a ladder of DCA limits
// spaced DcaStepPercent apart on the losing side of the anchor (and never beyond RangePercent
// in total). Each fill becomes its own batch with its own reduce-only TP limit at
// fill_price ± TpStepPercent. A batch's DCA slot is re-armed when its TP fills, so the grid
// keeps capturing oscillations until *all* batches are gone — at which point we wait one bar
// and seed a brand-new anchor.
public class GridFloatConfig
{
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = "1h";

    // "Long" or "Short". One direction per bot — hedge mode not supported.
    public string Direction { get; set; } = "Long";

    // Notional sent to the exchange for the first entry AND every DCA fill (so a 10-slot grid
    // can demand up to 11× this value in total notional, depending on fills).
    public decimal BaseSizeUsdt { get; set; } = 100m;

    // Total grid width as a % of the anchor price. Caps the number of DCA slots to
    // floor(RangePercent / DcaStepPercent). Beyond this distance, no further DCAs are placed.
    public decimal RangePercent { get; set; } = 10m;

    // Distance between adjacent DCA levels, as a % of the anchor (long: anchor·(1−k·step)).
    public decimal DcaStepPercent { get; set; } = 1m;

    // Per-batch TP offset from THAT batch's individual fill price (NOT the average). Each
    // batch has its own reduce-only limit at fill_price ± TpStepPercent.
    public decimal TpStepPercent { get; set; } = 1m;

    // false → dynamic range: each new anchor recenters the grid (always N = floor(Range/Step) slots).
    // true  → static range: lower/upper bound is frozen at the FIRST anchor of this bot session;
    //         on each subsequent anchor we place as many slots as fit before crossing the frozen
    //         bound (e.g. 9, 10, 11, 12 depending on anchor drift). Bound for long = price floor;
    //         for short = price ceiling. Bound is reset on bot stop.
    public bool UseStaticRange { get; set; } = false;

    // Exchange leverage applied before the first market entry of every cycle.
    public int Leverage { get; set; } = 1;
}
