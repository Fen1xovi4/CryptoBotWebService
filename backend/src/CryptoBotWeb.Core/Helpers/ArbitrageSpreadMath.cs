namespace CryptoBotWeb.Core.Helpers;

/// <summary>
/// The two spread formulas FuturesArbitrage trades on, in one place.
///
/// Both the worker (entry/exit decisions) and the API (the live spread the user watches) compute
/// them, and the numbers must agree exactly — a monitor that shows a different spread than the
/// one the bot acts on is worse than no monitor at all.
///
/// Both are quoted against the CHEAP venue's price, and both are executable prices: you sell into
/// a bid and buy from an ask, never at mid.
/// </summary>
public static class ArbitrageSpreadMath
{
    /// <summary>Entry: sell the expensive venue at its bid, buy the cheap one at its ask.</summary>
    public static decimal EntrySpreadPercent(decimal expensiveBid, decimal cheapAsk)
        => cheapAsk > 0 ? (expensiveBid - cheapAsk) / cheapAsk * 100m : 0m;

    /// <summary>Cost to unwind right now: buy back the short at its ask, sell the long at its bid.</summary>
    public static decimal ExitSpreadPercent(decimal expensiveAsk, decimal cheapBid)
        => cheapBid > 0 ? (expensiveAsk - cheapBid) / cheapBid * 100m : 0m;
}
