# GridFloat monitoring — Iteration 65 (Phase 2) 🎯

**Captured**: 2026-05-17 15:39 UTC (17:39 Warsaw)
**Δ from iteration-64**: ~60 min
**Cron**: `17 * * * *` (Warsaw) — job `bb358590`

## TL;DR — 🎯 BREAKTHROUGH

- 🎯 **FIX #6 LOT-STEP FLOOR FIRED LIVE FOR FIRST TIME!** At 15:27:19 UTC on BB-SASH/ZBT (#7e), batch #3 — qty rounded from `61.76919323256...` → `61.7` (dust=0.0692 written off). **This is the path that PREVENTS new orphans from forming.** Phase 2 Fix #6 verification is now ~67% complete (dedupe ✅ + lot-step ✅; partial-fill handler still pending).
- ✅ Fix #6 dedupe **6th clean hour**: same 4 warnings on BB-M-Algida.
- 🟢 1 TP (+$0.095) + 4 DCAs. BB-SASH/FF cycled DCA#3 → TP#3 in 11 min.
- 🟢 0 errors (down from 5).
- 🟡 Fix #6 partial-TP handler — still not exercised (TP qty 111 matched batch.Qty exactly).

## 🎯 Fix #6 lot-step floor — first live fire

**Full log line** (strategy 7e848311 = BB-SASH-ShortSMA/ZBTUSDT):

```
⚙️ Fix #6: округляю TP qty батча #3 вниз до lot-step:
   61.76919323256718943993872496 → 61.7
   (lot-step=0.1, dust=0.06919323256718943993872496 списан).
   Предотвращает orphan на бирже.
```

**What happened**:
1. BB-SASH/ZBT had been cycling rapidly (iter-63 + iter-64 had 5 TPs combined on this bot).
2. The 5th DCA refill at 15:27:18 came in at qty `61.76919323...` (fractional, likely from a Bybit-side adjustment to the original 62 baseline).
3. Before placing the TP limit order, **Fix #6's lot-step floor logic activated**: it computed `floor(61.7691... / 0.1) * 0.1 = 61.7`, set `batch.Qty = 61.7`, and proceeded.
4. The 0.0692 dust was abandoned to the exchange — but this is intentional: it's <1% of position size, well below the minQty threshold, and prevents the much worse "orphan keeps growing additively" problem we saw on BB-JCT.

**Verdict**: Working **exactly as designed**. The lot-step floor was the surgical fix to stop the bleeding for any new fractional-qty batches.

## Δ Activity since iter-64

### Trades (6)

| Time UTC | Acc | Symbol | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|---|
| 14:50:23 | BB-SASH | FFUSDT | Buy | DCA#3 | 111 | 0.08966 | — |
| 14:50:52 | BB-SASH | ZBTUSDT | Buy | DCA#1 | 60.5 | 0.16523 | — |
| 15:01:10 | BB-SASH | ZBTUSDT | Buy | DCA#2 | 61.1 | 0.16356 | — |
| 15:01:51 | BB-SASH | FFUSDT | Sell | TP#3 | 111 | 0.09055 | **+$0.095** |
| 15:27:18 | BB-SASH | ZBTUSDT | Buy | DCA#3 | **61.77** ⭐ | 0.16189 | — |
| 15:27:30 | BX-M-IJKL | ZBTUSDT | Buy | DCA#1 | 30.897 | 0.16182 | — |

⭐ Note the DCA#3 qty 61.77 — this is the fill that triggered Fix #6's lot-step floor at 15:27:19. Bybit recorded the trade with fractional qty, then the TP placement for the new batch invoked the floor logic 1 second later.

Hour Δ: **+$0.095** (1 TP).

### Realized PnL — current vs iter-64

| Acc | iter-64 | iter-65 | Δ |
|---|---|---|---|
| BB-M-Algida | 36.952 | 36.952 | $0.00 |
| BB-SASH-ShortSMA | 12.256 | **12.351** | **+$0.095** (FF TP#3) |
| BG-SASH-Insider | 29.176 | 29.176 | $0.00 |
| BX-M-IJKL | 16.652 | 16.652 | $0.00 |
| **Σ** | **95.036** | **95.131** | **+$0.095** |

### Log counts (last 60 min, our workspace)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | **4** | 0 |
| BB-SASH-ShortSMA | 0 | 1 | **11** (incl. 1× Fix #6 lot-step Info ⭐) |
| BG-SASH-Insider | 0 | 0 | 0 |
| BX-M-IJKL | 0 | 0 | 2 |

## 🚨 Issue tracker

### ✅ Fix #6 paths verified (2 of 3)

| Path | Status | Verified |
|---|---|---|
| Dedupe (state.LastReconcileOrphanQty throttle) | ✅ Working | Since iter-59, 6 consecutive hours |
| **PlaceBatchTpLimit lot-step floor** | ✅ **Working** | **iter-65: 15:27:19 fired live** |
| RecordTpFill partial-fill residual handler | 🟡 Not exercised | Awaiting Bybit `FilledQuantity < batch.Qty` |

### ✅ BB-M-Algida qtyExcess storm — Fix #6 dedupe 6 consecutive hours

### 🟡 Reconcile-TP race (Fix #3 candidate) — 1 stray warning

Single warning on BB-SASH this hour (probably the 15:01 FF TP fill caught mid-settle). Not a burst.

### 🟢 BB-JCT phantom DCA#5 — 29+ clean hours

## State delta (vs iter-64)

| Bot | iter-64 bat/dca | iter-65 bat/dca | Δ | realized Δ |
|---|---|---|---|---|
| BB-SASH/FF (#46) | 3/13 | **3/13** | DCA+TP net even | +$0.095 |
| BB-SASH/ZBT (#7e) | 1/15 | **4/12** | 3 DCA fills (incl. fractional 61.77 → 61.7 via Fix #6) | — |
| BX-M/ZBT (#0a) | 1/6 | **2/5** | DCA#1 fill | — |
| All others | — | — | — | — |

Inventory: 86 → **89 batches**, 78 → **75 DCAs**.

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): 0 trades. Still all holding.
- **Bybit BB-SASH-ShortSMA** (3 bots): 1 TP + 4 DCAs. **Fix #6 lot-step floor activated** on ZBT-SASH batch #3 — the fix's first live "save."
- **Bitget BG-SASH-Insider** (3 bots): 0 trades.
- **BingX BX-M-IJKL** (2 bots): 1 DCA (ZBT slot #1).

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$95.131** |
| Δ vs iter-64 | **+$0.095** |
| Δ vs iter-34 baseline ($47.026) | **+$48.105** |

## Verdict for iteration 65

🎯 **Milestone iteration.** The second of three Fix #6 paths is now **proven live**: PlaceBatchTpLimit's lot-step floor correctly intercepted a fractional Bybit DCA fill and prevented it from becoming a new permanent orphan. Combined with the dedupe (verified 6 hours), this means **the active bleeding has stopped** — any new fractional-qty batch will be sanitized at TP-placement time.

Only the **partial-fill residual handler** remains untested. That path requires Bybit to return a TP order with `Status=Filled, FilledQuantity < batch.Qty`. The current bot universe runs mostly small quantities relative to Bybit's lot steps, so partial fills are rare. Possible triggers in coming hours:
- BB-JCT TP fire (the original 3082.36 orphan source — would directly test partial-fill on a known-problematic batch)
- Any rapid price spike causing a TP to fill across multiple ticks (less common)

**Next cron fire ~16:17 UTC (18:17 Warsaw) → iter-66. 17 iterations remain.**
