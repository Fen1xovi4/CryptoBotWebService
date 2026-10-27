namespace CryptoBotWeb.Core.DTOs;

// SpotPingPong: spot-only, long-only "ping-pong" on hand-picked price levels for instruments
// that sit in a long flat. Every level is an independent cell:
//   limit BUY at BuyPrice for AmountUsdt worth of coin → once filled, limit SELL of everything
//   that actually landed in the wallet (fill qty minus the buy fee when the exchange charges it
//   in the base coin) at SellPrice → once filled, place the BUY again. Forever.
//
// No stop-loss by design: a level whose buy filled and whose price then ran away downward just
// holds its coins with the sell resting until price comes back. A level whose buy never fills
// (price ran up) keeps its buy resting. Both are accepted risk.
//
// Supported on Bybit, BingX and Bitget spot (ISpotExchangeService). Dzengi has no spot client.
public class SpotPingPongConfig
{
    public string Symbol { get; set; } = string.Empty;

    public List<SpotPingPongLevel> Levels { get; set; } = new();
}

public class SpotPingPongLevel
{
    // Both prices are entered by hand. SellPrice must exceed BuyPrice by more than two fees or
    // the cycle loses money — validated at create/start time and by the handler.
    public decimal BuyPrice { get; set; }
    public decimal SellPrice { get; set; }

    // Fixed quote notional per cycle; coin qty = AmountUsdt / BuyPrice floored to the lot step.
    public decimal AmountUsdt { get; set; }
}
