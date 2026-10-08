namespace CryptoBotWeb.Core.DTOs;

// Cross-exchange perp-perp arbitrage. The bot watches the executable spread between the SAME
// (or equivalent) USDT-perp contract on two different exchanges — the primary account
// (Strategy.AccountId) and the secondary account (Strategy.SecondAccountId) — and opens a
// delta-neutral pair when the spread widens: SHORT on the expensive exchange at its bid,
// LONG on the cheap exchange at its ask.
//
// Spread definition (executable, fee-unaware — levels must be set to clear ~4 taker fees):
//   entry spread % = (bid_expensive − ask_cheap) / ask_cheap × 100
//   exit  spread % = (ask_expensive − bid_cheap) / bid_cheap × 100   (cost to unwind)
//
// Levels form a "spread grid": each level opens independently when the entry spread reaches
// its EntrySpreadPercent and closes independently when the exit spread falls back to its
// ExitSpreadPercent. Example: level 1 = enter 1% / exit 0%, level 2 = enter 3% / exit 1% —
// a widening to 3% adds size, a partial convergence to 1% trims it, full convergence flattens.
//
// All open levels share one direction (which exchange is expensive), fixed when the first
// level opens. Both legs go out concurrently (market or limit-IOC, see OrderMode) in one-way
// position mode. Funding is NOT modeled
// in V1 — it lands on the exchange balances and is not part of the recorded PnL.
public class ArbitrageLevelConfig
{
    // Open this level when the entry spread reaches this percentage.
    public decimal EntrySpreadPercent { get; set; }

    // Close this level when the exit spread falls to (or below) this percentage.
    // Must be strictly less than EntrySpreadPercent.
    public decimal ExitSpreadPercent { get; set; }

    // USDT notional per leg for this level (each exchange gets this amount).
    public decimal NotionalUsdt { get; set; }
}

public class ArbitrageConfig
{
    // Symbol on the primary account's exchange, e.g. "BTCUSDT".
    public string Symbol { get; set; } = string.Empty;

    // Symbol on the secondary exchange; null/empty means same as Symbol.
    public string? SecondSymbol { get; set; }

    public int Leverage { get; set; } = 1;

    // When true the bot trades the spread in either sign (whichever exchange is expensive);
    // when false only PrimaryExpensive (short primary / long secondary) setups are taken.
    public bool AllowBothDirections { get; set; } = true;

    // Sorted ascending by EntrySpreadPercent by convention; the handler sorts defensively.
    public List<ArbitrageLevelConfig> Levels { get; set; } = new();

    // Stop the bot after this many consecutive order failures (leg-risk protection).
    public int MaxConsecutiveFailures { get; set; } = 3;

    // How the two legs are executed on threshold opens and closes:
    //   "Market"   — plain market orders: always fill, at whatever price the book gives.
    //   "LimitIoc" — limit ImmediateOrCancel at the quoted price ± MaxSlippagePercent: a leg can
    //                never fill worse than that, but it may fill partly or not at all. Any
    //                mismatch between the two legs is trimmed back at market right away, so the
    //                level only ever holds a matched pair (see ArbitrageHandler.SettleOpenAsync).
    // Safety closes (leg unwinds, rollbacks, trims, manual force close) are always market.
    public string OrderMode { get; set; } = ArbitrageOrderModes.Market;

    // LimitIoc only: how far past the quoted price each leg may fill, in percent of price.
    // Buy limit = ask × (1 + x/100), sell limit = bid × (1 − x/100).
    public decimal MaxSlippagePercent { get; set; } = 0.05m;
}

public static class ArbitrageOrderModes
{
    public const string Market = "Market";
    public const string LimitIoc = "LimitIoc";

    public static bool IsLimitIoc(string? mode) =>
        string.Equals(mode, LimitIoc, StringComparison.OrdinalIgnoreCase);
}
