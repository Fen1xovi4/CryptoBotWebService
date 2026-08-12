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
// level opens. Both legs are market orders in one-way position mode. Funding is NOT modeled
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
}
