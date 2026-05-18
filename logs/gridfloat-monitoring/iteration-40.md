# GridFloat monitoring — Iteration 40

**Captured**: 2026-05-15 22:29 UTC (00:29 Warsaw, May 16)
**Δ from iteration-39**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **2 trades** (1 TP, 1 DCA), **+$0.043 realized** — quietest active hour yet.
- ✅ Single `RECONCILE TP` warning on BB-BANANAS demonstrated the **fallback fill-detection** working correctly (exchange-authoritative path).
- ✅ 0 errors (6th clean hour); 0 phantom dupes (hour 6).
- 🟡 Bitget + BingX **idle 4th consecutive hour**.

## Δ Activity since iter-39

### Trades (2)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 21:57:28 | BB-XRP (#e7) | Buy  | DCA#7 | 6.9 | 1.43010 | — |
| 22:04:52 | BB-BANANAS (#4f) | Sell | TP#2  | 800 | 0.01185 | +$0.043 |

### Realized PnL delta

| Bot | iter-39 | iter-40 | Δ |
|---|---|---|---|
| BB-BANANAS (#4f) | 0.216 | 0.259 | +$0.043 |

### Log counts (since 21:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 0 | 2 |
| BB-SASH-ShortSMA | 0 | 1 | 2 |
| BG-SASH-Insider | 0 | 0 | 0 |
| BX-M-IJKL | 0 | 0 | 0 |

The lone warning is the **reconcile-TP backstop firing as designed**:
```
22:04:52  RECONCILE TP: state qty=2400 vs exchange qty=1600 (дельта=800, цена=0.011854).
          Закрываю батчи, чьи TP пересечены ценой.
```
Exchange reported the TP fill before PollTpFills could capture it — `ReconcileBatchesFromPosition` at [GridFloatHandler.cs:675-756](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L675-L756) detected the 800-qty shortfall, matched it to the price-crossed batch, and recorded the TP. This is exactly the "exchange is authoritative" path described in the docstring.

## 🚨 Issue tracker

### 🟢 BB-JCT phantom DCA#5 — 6 clean hours

### 🟢 BB-M-Algida error spam — 6th clean hour

## Cross-exchange health

- **Bybit** (7 bots): 1 TP (+$0.043), 1 DCA. Sleepy hour — most bots holding inventory.
- **Bitget** (3 bots): idle 4th consecutive hour.
- **BingX** (2 bots): idle 4th consecutive hour.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-XRP (#e7) | 7 → 8 | 4 → 3 |
| BB-BANANAS (#4f) | 3 → 2 | 23 → 24 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$49.842** |
| Δ this iteration | +$0.043 |
| Δ from iter-34 baseline | +$2.816 |

## Verdict for iteration 40

Sleep-cycle hour — market quiet, only one TP and one DCA fired. The interesting datapoint is the reconcile-TP backstop firing cleanly: a price-crossed BB-BANANAS TP would have lingered as a phantom batch on state until next poll, but ReconcileBatchesFromPosition caught it the same tick and recorded the fill normally. No money lost, log noise contained to one warning. BG and BX continue dormant for 4 hours — both their grids are anchored well above the current price band and will need either a rebound (to fire TPs) or a deeper drop (to fire next-tier DCAs) to make a move. **Next cron fire ~23:17 UTC (01:17 Warsaw).**
