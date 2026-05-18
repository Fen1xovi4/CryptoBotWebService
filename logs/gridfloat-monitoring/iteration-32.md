# GridFloat monitoring — Iteration 32

**Captured**: 2026-05-15 14:13 UTC (16:13 Warsaw)
**Δ from iteration-31**: ~60 min
**Cron**: `342c898f` fired at 14:07 UTC

## 🎯 TL;DR

- **15 trades**, **+$1.573 realized this hour** — biggest hour since iter-9.
- 🏆 **BB-SAGA TP#13 (+$0.481)** — FIRST profit harvest at a level created by Fix #4 widening (k=13 didn't exist before the bound recompute).
- 🆕 **BG-OPEN (#7) finally activated** — first DCA fill since baseline 32 hours ago. PnL still $0 (no TP yet).
- 🚨 **NEW BUG candidate**: BB-JCT stuck on TP#5 placement, 30+ errors `"Qty 0 < min 100 for JCTUSDT"` — partial-fill reconcile produced a sub-minimum batch.
- **Crossed $30 cumulative** — now at +$31.202.

## Δ Activity since 13:13 UTC

### Headline events

**🏆 BB-SAGA TP#13 — Fix #4 first harvest** (13:45:17 → 13:45:28):
```
13:45:17  DCA#12 fill   848.5 @ 0.02357  (already existed before Fix #4, k=12 was the old bound limit)
13:45:17  DCA#13 fill   424.1 @ 0.02247  ← was placed by Fix #4 widening!
13:45:28  TP#13 fill    424.1 @ 0.02361  PnL = +$0.481
```
Without Fix #4, k=13 would have been blocked by the old static bound 0.022648. Within 11 seconds of being adopted as a batch, its TP fired for +$0.481. **This single trade alone justifies Fix #4's existence.**

**🆕 BG-OPEN first activity** (13:51:20):
- DCA#1 fill 56 @ 0.176249 — exactly 0.1817·0.97 ✓
- Bot has been dormant since iter-1 baseline. Realized PnL still $0 (waiting for TP).

**🚨 BB-JCT stuck-TP error loop** (started 14:07:38, ongoing):
```
TP батча #5 не выставлен: Qty 0 < min 100 for JCTUSDT
```
30+ errors fired at ~12 sec intervals. Root cause traced below.

### All 15 trades

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 13:33:33 | BB-XRP (#1)  | Buy  | DCA#6 fill        | 6.90    | 1.4455   | — |
| 13:45:17 | BB-SAGA (#4) | Buy  | DCA#12 fill       | 848.49  | 0.02357  | — |
| 13:45:17 | BB-SAGA (#4) | Buy  | DCA#13 fill       | 424.11  | 0.02247  | — |
| **13:45:28** | **BB-SAGA (#4)** | **Sell** | **TakeProfit#13** | 424.10 | 0.02361 | **+$0.481** |
| 13:49:36 | BB-JCT (#3)  | Buy  | DCA#2 fill        | 2200    | 0.00441  | — |
| 13:50:42 | BB-JCT (#3)  | Buy  | DCA#3 fill        | 2340.53 | 0.00427  | — |
| 13:50:42 | BB-JCT (#3)  | Buy  | DCA#4 fill        | 2259.47 | 0.00413  | — |
| 13:50:55 | BB-JCT (#3)  | Buy  | DCA#5 fill        | 2400    | 0.00399  | — |
| 13:51:04 | BB-JCT (#3)  | Sell | TakeProfit#5      | 2400    | 0.00421  | +$0.530 |
| **13:51:20** | **BG-OPEN (#7)** | **Buy** | **DCA#1 fill (FIRST EVER!)** | 56 | 0.17625 | — |
| 13:51:28 | BB-XRP (#1)  | Buy  | DCA#7 fill        | 6.90    | 1.4301   | — |
| 13:54:33 | BB-JCT (#3)  | Sell | TakeProfit#4      | 2200    | 0.00426  | +$0.269 |
| 13:54:35 | BB-JCT (#3)  | Buy  | DCA#5 (reconcile fractional!) | **59.47** | 0.00399 | — |
| 13:57:45 | BB-JCT (#3)  | Buy  | DCA#4 fill        | 2400    | 0.00413  | — |
| 14:05:41 | BB-JCT (#3)  | Sell | TakeProfit#4      | 2400    | 0.00426  | +$0.293 |

### realizedPnL delta
| Bot | iter-31 | now | Δ |
|---|---|---|---|
| BB-JCT (#3)  | $8.075  | $9.167 | **+$1.092** (3 TPs) |
| BB-SAGA (#4) | $11.274 | $11.755 | +$0.481 (TP#13 — Fix #4 fruit) |
| **Δ this hour** |     |        | **+$1.573** |

## 🚨 Bug candidate (Fix #5): partial-fill reconcile → sub-minimum batch

### Reproduction trace on BB-JCT

1. **13:50:55** — DCA#5 placed at 0.00399 with qty 2400 (rounded to lot 100) fills via Poll → batch #5 with qty=2400.
2. **13:51:04** — TP#5 fires immediately → batch #5 closed. Slot 5 re-armed by HealMissingDcas, placing a new DCA limit at qty ≈ $10/0.00399 = 2506 rounded to 2500.
3. **13:54:33** — TP#4 fires (different batch) → state qty drops by 2200.
4. **13:54:35** — ReconcileBatchesFromPosition sees `exchange.qty - state.qty = +59.47` (small partial fill on the newly-placed DCA#5 limit, probably from a single-tick microliquidation hitting the limit briefly).
5. **ReconcileMissedDcaFills** picks up the DCA at slot 5 (it's the only DCA matching), computes `adoptQty = min(dca.Qty=2500, qtyExcess=59.47) = 59.47`, calls `AdoptDcaFill` with that 59.47 qty → creates batch #5 with **batch.Qty = 59.47**.
6. **PlaceBatchTpLimit** for batch #5 tries to place reduce-only sell limit at qty 59.47. Bybit JCTUSDT min order qty is **100** → exchange rejects with `Qty 0 < min 100` (likely the SDK rounded 59.47 down to 0 before the request).
7. **HealMissingTps** on every subsequent tick retries → infinite error loop because `batch.TpOrderId` is null.

### Why the cap was right, but the placement is wrong

The `adoptQty = min(...)` cap in `ReconcileMissedDcaFills` is correct in principle — it prevents over-adopting more than the actual exchange delta. But it can produce a `batch.Qty` below the exchange's min order qty, and then the heal/TP-placement loop becomes stuck.

### Proposed Fix #5

In `ReconcileMissedDcaFills`, when `adoptQty < some_threshold` (either configurable, or 0.5% of `dca.Qty`, or a hard-coded "5 USDT worth"), **defer adoption** and break out. The 2-second second-probe will retry next tick when either:
- More of the partial fill arrives (delta grows above threshold), OR
- The placement was cancelled and we should drop it instead.

Alternative cleaner fix: in `PlaceBatchTpLimit`, if the qty would be below the exchange min, **delete the batch** (treat as if the partial DCA fill never happened — small loss accepted) and let the slot re-arm normally. Marginally lossy, but bounded.

Another option: at `AdoptDcaFill` entry, if `fillQty < min_order_qty_for_symbol` (we don't currently know this from `IFuturesExchangeService`), skip adoption — return without creating the batch.

I'd recommend the first option (defer in reconcile) as it's lossless and self-healing.

## State delta

| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BB-XRP (#1)   | 6 → 8 | 5 → 3 | unchanged |
| BB-JCT (#3)   | 2 → 5 | 9 → 6 | unchanged |
| BB-SAGA (#4)  | 12 → 13 | 10 → 9 | unchanged |
| BG-OPEN (#7)  | 1 → 2 | 6 → 5 | unchanged (first new batch since baseline!) |

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $1.139 | +$0.854 |
| #2 BB-ZBT   | $1.774 | $5.659 | +$3.885 |
| #3 BB-JCT   | $5.184 | $9.167 | **+$3.984** |
| #4 BB-SAGA  | $6.484 | $11.755 | **+$5.271** |
| #5 BG-BUSDT | $0     | $9.549 | **+$9.549** |
| #6 BG-ZBT   | $0     | $1.763 | +$1.763 |
| #7 BG-OPEN  | $0     | $0     | 0 (still no TP) |
| #8 BX-BUSDT | $0     | $5.159 | **+$5.159** |
| #9 BX-ZBT   | $0     | $0.739 | +$0.739 |
| **Total Δ from baseline** |  |  | **+$31.202** 🎉 |

## Verdict for iteration 32

✅ **Fix #4 paid off** — k=13 batch generated $0.481 profit it couldn't have generated before.

✅ **BG-OPEN finally moving** — 7th bot to register activity (only BG-ZBT at lowest tier was lower-PnL among the active ones).

🚨 **Fix #5 needed**: partial-fill reconcile adoption can produce sub-min-qty batches that loop in TP-placement errors. **Non-blocking** (other batches keep trading and earning), but pollutes logs at ~12-sec intervals and the stuck batch is dead inventory until manually cleaned up. Worth fixing soon.

📅 Next cron fire 15:07 UTC.
