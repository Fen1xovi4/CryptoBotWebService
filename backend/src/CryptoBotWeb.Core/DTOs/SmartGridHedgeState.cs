namespace CryptoBotWeb.Core.DTOs;

public enum SmartGridHedgePhase
{
    NotStarted = 0,    // freshly created or freshly Start'd; next tick begins Opening
    Opening = 1,       // switch hedge mode + leverage + open qInit/hedge + lay limits
    Active = 2,        // poll fills, re-arm pairs, watch boundary triggers
    HardClosing = 3,   // boundary touched — cancel all + market-close everything
    Closed = 4         // cycle ended, AutoRestart=false; user must Stop+Start to re-arm
}

// One DCA cell (k = 1..NDown-1). The "Buy" limit sits at D_k = P0 * (1-Step)^k.
// When it fills, we place a paired reduce-only "Sell" limit at D_{k-1}. When the sell
// fills we realize qty*(D_{k-1} - D_k), reset, and re-arm the buy at D_k.
public class SmartGridDcaCell
{
    public int K { get; set; }              // 1..NDown-1
    public decimal BuyPrice { get; set; }   // D_k
    public decimal SellPrice { get; set; }  // D_{k-1}

    public string? BuyOrderId { get; set; }
    public string? SellOrderId { get; set; }

    // True after BuyOrderId is observed Filled and SellOrderId is placed; cleared on Sell fill.
    public bool Paired { get; set; }

    // Filled qty in coins of the latest buy. Used as the sell qty and for realized PnL.
    public decimal QtyCoins { get; set; }
}

// One skim cell (k = 1..NUp-1). Behavior depends on SkimMode:
//
//   OneShot:     on first U_k cross we trim qInit by (qInit*U_k - LotUsd)/U_k coins via a
//                MARKET sell on the LONG side (positionIdx=1). Cell then stays Fired forever.
//                No paired short, no recycle.
//
//   ExcessRecycle / FullRecycle: at U_k we open a paired SHORT on the hedge side
//                (positionIdx=2) sized to ShortQtyCoins. Paired Buy limit sits at U_{k-1}.
//                When the buy fills we realize qShort*(U_k − U_{k-1}), reset, and re-arm.
public class SmartGridSkimCell
{
    public int K { get; set; }                // 1..NUp-1
    public decimal SellPrice { get; set; }    // U_k

    // Used only in recycle modes — U_{k-1}, where the paired Buy (cover) sits.
    public decimal CoverPrice { get; set; }

    // OneShot: true once trim has happened, permanently.
    // Recycle: not used (use Paired instead).
    public bool FiredOnceShot { get; set; }

    // Recycle modes only — the short-side limit on U_k (sell) and its paired cover on U_{k-1}.
    public string? ShortOrderId { get; set; }
    public string? CoverOrderId { get; set; }
    public bool Paired { get; set; }
    public decimal ShortQtyCoins { get; set; }
}

public class SmartGridHedgeState
{
    public SmartGridHedgePhase Phase { get; set; } = SmartGridHedgePhase.NotStarted;

    // Cycle anchor — set at Opening from the live mark price. On AutoRestart, a fresh value
    // is captured at the next cycle's Opening. 0 = not yet anchored.
    public decimal P0 { get; set; }

    // Cached cycle boundaries derived from P0 + Step + NUp/NDown at cycle start.
    public decimal HBreak { get; set; }
    public decimal LBreak { get; set; }

    // Initial long, opened market at Opening on positionIdx=1.
    public decimal QInitCoins { get; set; }
    public decimal PAvgInit { get; set; }   // pinned = P0; not recomputed by DCA

    // Static short hedge, opened market at Opening on positionIdx=2. Stays constant
    // through the cycle (ADR-0004).
    public decimal QHedgeCoins { get; set; }
    public decimal HedgeEntryPrice { get; set; }

    // Index 0 is unused (k starts at 1). Length = NDown.
    public List<SmartGridDcaCell> DcaCells { get; set; } = new();

    // Index 0 is unused (k starts at 1). Length = NUp.
    public List<SmartGridSkimCell> SkimCells { get; set; } = new();

    // Cumulative across cycles — preserved across AutoRestart and across manual Stop/Start.
    public decimal GridRealizedPnl { get; set; }
    public decimal HedgeRealizedPnl { get; set; }
    public decimal TotalFees { get; set; }
    public int CompletedCycles { get; set; }

    // Per-cycle running totals — reset to 0 at each Opening so the user can see "this cycle".
    public decimal CycleGridRealized { get; set; }
    public decimal CycleHedgeRealized { get; set; }
    public decimal CycleFees { get; set; }

    public decimal? LastMarkPrice { get; set; }
    public DateTime? CycleStartedAt { get; set; }
    public DateTime? LastTickAt { get; set; }

    // Reason the most recent cycle ended (for the UI/log).
    public string? LastCycleEndReason { get; set; }   // "HBreak" | "LBreak" | "Manual" | null
}
