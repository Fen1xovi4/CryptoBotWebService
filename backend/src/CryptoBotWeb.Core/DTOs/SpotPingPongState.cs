namespace CryptoBotWeb.Core.DTOs;

public enum SpotPingPongLevelPhase
{
    // No coins held for this level. BuyOrderId is either null (place next tick) or a resting buy.
    WaitingBuy = 0,

    // Coins held. SellOrderId is either null (place next tick) or a resting sell.
    WaitingSell = 1
}

// Runtime state of one configured level. Keyed to its config row by index (Levels[i] ↔
// LevelStates[i]); the handler re-syncs the list length against the config every tick so a
// level added in the form starts cycling without a restart.
public class SpotPingPongLevelState
{
    public SpotPingPongLevelPhase Phase { get; set; } = SpotPingPongLevelPhase.WaitingBuy;

    public string? BuyOrderId { get; set; }
    public string? SellOrderId { get; set; }

    // The resting order's price/qty as sent — lets Stop/Close cancel and lets the UI show them.
    public decimal BuyQty { get; set; }
    public decimal SellQty { get; set; }

    // Gross qty the exchange reported filled on the buy, the average price, and the fee charged
    // (quote-denominated estimate when the exchange reports it in the base coin or not at all).
    public decimal FilledQty { get; set; }
    public decimal FilledPrice { get; set; }
    public decimal BuyFeeUsd { get; set; }
    // True when the buy fee was taken in USDT (the wallet received the full FilledQty), so the
    // round-trip PnL must subtract it explicitly. False when it came out of the coins — then it
    // is already inside "paid for FilledQty, sold SellQty".
    public bool BuyFeeInQuote { get; set; }
    public DateTime? BoughtAt { get; set; }

    public int CompletedCycles { get; set; }
    public decimal RealizedPnlUsd { get; set; }

    // Set when the exchange refused a placement — shown on the bot card, retried with backoff.
    public string? LastError { get; set; }
    public DateTime? RetryAfter { get; set; }

    // Consecutive polls where the exchange said the resting order does not exist. A handful in
    // a row means it was cancelled by hand and purged — the handler then re-places it.
    public int PollMisses { get; set; }
}

public class SpotPingPongState
{
    public List<SpotPingPongLevelState> LevelStates { get; set; } = new();

    // Cumulative across Stop/Start.
    public int CompletedCycles { get; set; }
    public decimal RealizedPnlUsd { get; set; }
    public decimal FeesUsd { get; set; }

    public decimal? LastPrice { get; set; }
    public DateTime? LastTickAt { get; set; }
}
