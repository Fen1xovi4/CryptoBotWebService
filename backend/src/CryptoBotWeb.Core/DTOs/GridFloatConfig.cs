namespace CryptoBotWeb.Core.DTOs;

// Floating grid strategy. After a first market entry, the bot lays a ladder of DCA limits
// spaced DcaStepPercent apart on the losing side of the anchor. The grid is divided into
// expanding "tiers" — each tier covers offsets up to its UpToPercent, and every fill inside
// that tier uses the tier's SizeUsdt as notional. Tier 1 covers 0% → upTo₁, tier 2 covers
// upTo₁ → upTo₂, etc. So a config like
//   [ { upTo: 10, size: 10 }, { upTo: 20, size: 20 } ]
// places 10 USDT-sized DCA fills inside the first 10% and 20 USDT-sized DCA fills between
// 10% and 20% from the anchor. The anchor itself uses tier 1's size.
//
// Each fill becomes its own batch with its own reduce-only TP limit at
// fill_price ± TpStepPercent. A batch's DCA slot is re-armed when its TP fills, so the grid
// keeps capturing oscillations until *all* batches are gone — at which point we wait one bar
// and seed a brand-new anchor.
public class GridFloatTier
{
    // Upper offset boundary of this tier, as a % of the anchor price. Each tier covers the
    // range from the previous tier's UpToPercent (or 0 for tier 1) up to and including this
    // value. Must be strictly increasing across the tier list.
    public decimal UpToPercent { get; set; }

    // Notional sent to the exchange for every fill inside this tier — both the DCA limit and
    // the matching reduce-only TP close that exact qty.
    public decimal SizeUsdt { get; set; }

    // Optional per-tier DCA step override. When null, falls back to GridFloatConfig.DcaStepPercent.
    // Tiers walk independently: tier N's DCA levels start at the previous tier's UpToPercent
    // boundary and stride by this step until the tier's own UpToPercent is exceeded.
    public decimal? DcaStepPercent { get; set; }

    // Optional per-tier TP step override. When null, falls back to GridFloatConfig.TpStepPercent.
    // A batch's TP step is determined by the tier in which its fill offset% (from anchor) lies.
    public decimal? TpStepPercent { get; set; }
}

public class GridFloatConfig
{
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = "1h";

    // "Long" or "Short". One direction per bot — hedge mode not supported.
    public string Direction { get; set; } = "Long";

    // Expanding tier list. Sorted ascending by UpToPercent. The largest UpToPercent acts as
    // the total grid width (replaces the old RangePercent). Anchor uses Tiers[0].SizeUsdt.
    public List<GridFloatTier> Tiers { get; set; } = new();

    // Default distance between adjacent DCA levels (% of anchor). Used for any tier that
    // does NOT define its own DcaStepPercent. Each tier walks independently: levels inside
    // tier N start at the previous tier's UpToPercent boundary and stride by the tier's
    // effective step until reaching its own UpToPercent.
    public decimal DcaStepPercent { get; set; } = 1m;

    // Default per-batch TP offset from THAT batch's individual fill price (NOT the average).
    // Used for any tier that does NOT define its own TpStepPercent. The TP step for a fill
    // is determined by the tier in which the fill's offset% from the anchor lies.
    public decimal TpStepPercent { get; set; } = 1m;

    // false → dynamic range: each new anchor recenters the grid (slot count = floor(maxTier/Step)).
    // true  → static range: the lower/upper bound is frozen at the FIRST anchor of this bot
    //         session; on each subsequent anchor we place as many slots as fit before crossing
    //         the frozen bound. Bound for long = price floor; for short = price ceiling.
    //         Bound is reset on bot stop.
    public bool UseStaticRange { get; set; } = false;

    // Exchange leverage applied before the first market entry of every cycle.
    public int Leverage { get; set; } = 1;

    // Take-profit. When enabled, the handler cancels all orders, market-closes the aggregate
    // position, and sets Status=Stopped as soon as the cumulative realized PnL plus the
    // mark-to-market unrealized on all open batches reaches TakeProfitTargetUsd. Survives
    // OnFullClose / cooldown via the cumulative RealizedPnlDollar on state.
    public bool TakeProfitEnabled { get; set; } = false;
    public decimal TakeProfitTargetUsd { get; set; } = 100m;

    // When true, reaching the take-profit closes everything and the bot keeps trading: the
    // target is then measured per series (state.SeriesRealizedPnl + unrealized, zeroed at each
    // take-profit) instead of against lifetime realized PnL, and the next anchor opens one
    // closed bar later. RestartOnRangeExit always behaves this way regardless of this flag.
    public bool TakeProfitContinue { get; set; } = false;

    // Restart-on-range-exit ("stake ladder") mode. Tiers stop being an expanding grid and
    // become STAKE STEPS: the grid only ever spans Tiers[0]'s geometry (UpToPercent width,
    // DCA/TP steps), and the per-order size comes from Tiers[StakeStage].SizeUsdt.
    //   - Price leaves the range on the losing side (below anchor·(1−width%) for Long) →
    //     cancel everything, market-close, StakeStage++, re-anchor after one closed bar.
    //   - Grid fully closes by its own TPs → if the series (PnL since the last reset) is
    //     ≥ 0 and no take-profit is set, StakeStage resets to 0; otherwise the stage stays.
    //   - With TakeProfitEnabled the target applies to the SERIES and, instead of stopping
    //     the bot, closes everything and restarts at the base stake.
    //   - Range exit on the last stage → close everything and stop the bot.
    // UseStaticRange is ignored in this mode.
    public bool RestartOnRangeExit { get; set; } = false;

    // Optional anchor price guard. Long: no new anchor while price is ABOVE this; Short: no
    // new anchor while price is BELOW it. Stops the bot from building a fresh grid at the top
    // of a parabolic move that the pullback would then fill all the way down. Null = off.
    public decimal? AnchorPriceLimit { get; set; }

    // ───────────── Legacy fields (back-compat, read-only on load) ─────────────
    // Older configs (pre-tier-grid) used a single BaseSizeUsdt + RangePercent. We keep these
    // optional fields on the DTO so deserialization of stored ConfigJson succeeds; the
    // strategy handler converts them into a single-tier list on first read. New configs MUST
    // populate Tiers; these fields are ignored if Tiers is non-empty.
    public decimal? BaseSizeUsdt { get; set; }
    public decimal? RangePercent { get; set; }
}
