# GridFloat monitoring — Iteration 56

**Captured**: 2026-05-16 14:30 UTC (16:30 Warsaw)
**Δ from iteration-55**: ~60 min
**Cron**: `17 * * * *` (Warsaw) — job `7042ba25`

## TL;DR

- **17 trades** (7 TPs, 10 DCAs), **+$2.495 realized**.
- 🎯 **BB-XRP earned its first TP of the run** (TP#9 +$0.095 at 13:31:34) — last hold-out from the BB-M-Algida 4-bot group.
- 🟢 BB-JCT cycled TP#10 → DCA#10 → TP#10 → DCA#10 in **65 seconds** (13:32:24 → 13:34:27) — fastest BB-JCT cycle so far. Both TPs identical price/qty/PnL but 44 sec apart with intervening DCA = legitimate slot re-arm, NOT phantom dup.
- 🚨 BB-M-Algida qtyExcess phantom: 489 warnings, still same 3082.36 delta. Each new BB-JCT TP+DCA cycle leaves another 42.69 dust → no resolution path.
- ✅ 0 errors (22nd clean hour); 0 phantom Trade dupes (hour 22).

## Δ Activity since iter-55

### Trades (17) — TPs

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 13:31:34 | BB-XRP (#e7) | Sell | TP#9 ⭐ first BB-XRP TP | 7.10 | 1.41320 | +$0.095 |
| 13:32:24 | BB-JCT (#3) | Sell | TP#10 (partial 3000/3043) | 3000 | 0.00339 | +$0.292 |
| 13:33:08 | BB-JCT (#3) | Sell | TP#10 (re-fill, partial again) | 3000 | 0.00339 | +$0.292 |
| 13:34:44 | BX-BUSDT (#1c) | Sell | TP#9 | 39.44 | 0.39160 | +$0.444 |
| 13:57:48 | BG-BUSDT (#3f) | Sell | **TP#9** | 80 | 0.38550 | **+$0.884** 🏆 |
| 13:58:15 | BX-BUSDT (#1c) | Sell | TP#9 (re-fill) | 39.44 | 0.39160 | +$0.444 |
| 14:06:30 | BB-BANANAS (#4f) | Sell | TP#14 | 900 | 0.01113 | +$0.046 |

10 DCAs across BB-JCT (×2 DCA#10 re-arms), BB-BANANAS (×4), BB-ZBT-SASH, BG-OPEN, BG-BUSDT, BX-BUSDT.

### Realized PnL delta

| Bot | iter-55 | iter-56 | Δ |
|---|---|---|---|
| BB-JCT (#3) | 12.069 | 12.652 | +$0.583 (2 TPs) |
| BB-XRP (#e7) | 1.233 | 1.328 | ⭐ +$0.095 (first TP) |
| BB-BANANAS (#4f) | 1.720 | 1.766 | +$0.046 |
| BG-BUSDT (#3f) | 18.903 | 19.787 | +$0.884 |
| BX-BUSDT (#1c) | 12.663 | 13.550 | +$0.887 (2 TPs) |
| **Σ Δ** | | | **+$2.495** |

### Log counts (since 13:30 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | **489** | 10 |
| BB-SASH-ShortSMA | 0 | 2 | 12 |
| BG-SASH-Insider | 0 | 1 | 6 |
| BX-M-IJKL | 0 | 0 | 6 |

## 🚨 Issue tracker

### 🚨 BB-M-Algida qtyExcess phantom (4 hours old) — chronic

Same 3082.36 delta visible across all 489 warnings this hour. Mechanism reconfirmed: BB-JCT TP#10 partial fills 3000 of 3043 → 42.69 dust persists → DCA#10 re-arms 3042.69 → orphan keeps growing additively. The TP+DCA cycle repeats every ~10-30 min during volatile periods, each adding ~43 to the orphan but the orphan magnitude stays clamped because reconcile attempts (and fails) to adopt every tick.

**Hypothesis on root cause**: the Bybit TP limit order is placed for batch.Qty=3042.69, but the exchange's lot-size rounding rejects the fractional part — fills exactly 3000 and effectively cancels the remainder. State assumes the full batch closed (Trade row records 3000 qty), drops the batch from state.Batches. Next tick: exchange has 42.69 leftover qty that state doesn't track + 3042.69 from the new DCA = 3085 excess. The 3082.36 we see is this minus the lot-size rounding error. **Fix candidates**:
1. After a TP fill, query the exchange for residual qty on the symbol; create a "leftover" mini-batch with the residual.
2. Place TP limits with qty rounded down to the lot step (avoid the fractional remainder entirely).
3. Reconcile path: when qtyExcess matches "last TP residual + DCA size" pattern, auto-merge into a single batch at the DCA price.

### 🟢 BX-BUSDT chronic — silent this hour (0 warnings)

The previously-chronic BingX qtyExcess is now genuinely resolved. Three iterations of progressive cleanup (Fix #5 sub-min purges + TP fills clearing orphans).

### 🟢 BB-JCT phantom DCA#5 (iter-34 root) — **22 clean hours**

### 🟢 BB-M-Algida error spam — 22nd clean hour

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): 3 TPs (+$0.679) — best Algida hour since iter-52. BB-XRP TP#9 marks first XRP profit of the run.
- **Bybit BB-SASH-ShortSMA** (3 bots): 1 TP (+$0.046), 4 DCAs. Mostly quiet, BANANAS accumulating again.
- **Bitget** (3 bots): 1 BG-BUSDT TP (+$0.884), 2 DCAs (BG-OPEN #4 + BG-BUSDT #9 re-arm). BG-BUSDT realized now $19.79 — biggest contributor of the run.
- **BingX** (2 bots): 2 BX-BUSDT TPs (+$0.887), 1 DCA. **0 warnings** — fully recovered from chronic noise.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-XRP (#e7) | 10 → 9 | 1 → 2 |
| BB-BANANAS (#4f) | 14 → 17 | 12 → 9 |
| BB-ZBT-SASH (#7e) | 5 → 6 | 11 → 10 |
| BG-OPEN (#b3) | 4 → 5 | 3 → 2 |
| BX-BUSDT (#1c) | 10 → 9 | 6 → 7 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$81.355** |
| Δ this iteration | +$2.495 |
| Δ from iter-34 baseline ($47.026) | **+$34.329** |

## Verdict for iteration 56

Sixth $2+ hour out of the last 8. BUSDT continues to be the workhorse — BG-BUSDT and BX-BUSDT together account for $13.84 (40%) of the cumulative gain. BB-XRP finally registered a TP after 22 hours of holding inventory. The chronic noise pattern is now contained to BB-M-Algida (BingX is fully quiet). The Fix candidate analysis above lands a concrete proposal for next sprint. **Next cron fire ~15:17 UTC (17:17 Warsaw). 2 iterations remain (57, then iter-58 final).**
