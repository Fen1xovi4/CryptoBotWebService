using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Helpers;

/// <summary>
/// Leaderboard math: realized PnL over a period and the peak capital the bot had invested during
/// that same period, both reconstructed from its trade log.
///
/// The trade log is the only source that exists for every strategy type, but it is not an exact
/// position ledger — rounding and the odd unrecorded fill make "sum of opens − sum of closes" drift
/// over months. So the invested amount is rebuilt in a way that cannot accumulate that drift:
///   • single-position strategies (every close flattens the whole position) restart from zero
///     after each close — the capital of a cycle is just the cost of its opens;
///   • grid strategies (many independent batches) release cost in proportion to the quantity
///     closed, clamped at zero.
/// Open trades are rows with PnlDollar == null, closes carry PnlDollar.
/// </summary>
public static class TopBotMetrics
{
    public readonly record struct Result(
        decimal PnlUsd, decimal PeakCapitalUsd, int ClosedTrades, int WinningTrades)
    {
        public decimal ReturnPercent => PeakCapitalUsd > 0 ? PnlUsd / PeakCapitalUsd * 100m : 0m;
    }

    /// <param name="trades">All trades of one bot, any order.</param>
    /// <param name="sinceUtc">Start of the period; the position carried into it counts as invested.</param>
    /// <param name="closeFlattens">True when every close trade closes the whole position.</param>
    public static Result Compute(IEnumerable<TopBotTrade> trades, DateTime sinceUtc, bool closeFlattens)
    {
        decimal cost = 0m, qty = 0m;
        decimal peak = 0m, pnl = 0m;
        int closed = 0, wins = 0;
        var inPeriod = false;

        foreach (var t in trades.OrderBy(t => t.ExecutedAt))
        {
            if (!inPeriod && t.ExecutedAt >= sinceUtc)
            {
                inPeriod = true;
                peak = cost; // position carried into the period
            }

            if (t.PnlDollar == null)
            {
                cost += t.Quantity * t.Price;
                qty += t.Quantity;
            }
            else
            {
                if (closeFlattens || qty <= 0m || t.Quantity >= qty)
                {
                    cost = 0m;
                    qty = 0m;
                }
                else
                {
                    cost -= cost * (t.Quantity / qty);
                    qty -= t.Quantity;
                }

                if (inPeriod)
                {
                    pnl += t.PnlDollar.Value;
                    closed++;
                    if (t.PnlDollar.Value > 0m) wins++;
                }
            }

            if (inPeriod && cost > peak) peak = cost;
        }

        if (!inPeriod) peak = cost; // nothing happened inside the period

        return new Result(pnl, peak, closed, wins);
    }
}
