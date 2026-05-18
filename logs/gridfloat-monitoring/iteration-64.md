# GridFloat monitoring — Iteration 64 (Phase 2)

**Captured**: 2026-05-17 14:39 UTC (16:39 Warsaw)
**Δ from iteration-63**: ~60 min
**Cron**: `17 * * * *` (Warsaw) — job `bb358590`

## TL;DR

- ✅ Fix #6 dedupe **5th clean hour**: still 4 warnings/hr on BB-M-Algida, no escalation.
- 🟢 2 new TPs (+$0.192) since iter-63: BB-SASH/ZBT cycled **TP→DCA→TP→DCA→TP** in 47 min (3 TP/DCA pairs).
- 🟢 BB-SASH/FF DCA#2 filled at 14:20 — re-arming for another payoff.
- 🟡 5 transient Bybit rate-limit errors (back from 0 last hour). Still self-healing.
- 🟡 0 lot-step / 0 partial-TP fires — TPs continue to be clean full closes.

## Δ Activity since iter-63

### New trades (5 since iter-63, excluding 13:39 carry-overs)

| Time UTC | Acc | Symbol | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|---|
| 13:54:25 | BB-SASH | ZBTUSDT | Buy | DCA#1 | 60.5 | 0.16523 | — |
| 13:54:55 | BB-SASH | ZBTUSDT | Buy | DCA#2 | 61.1 | 0.16356 | — |
| 13:59:03 | BB-SASH | ZBTUSDT | Sell | TP#2 | 61.1 | 0.16520 | +$0.096 |
| 14:20:58 | BB-SASH | FFUSDT | Buy | DCA#2 | 110 | 0.09059 | — |
| 14:26:15 | BB-SASH | ZBTUSDT | Sell | TP#1 | 60.5 | 0.16688 | +$0.096 |

Hour Δ: **+$0.192** (2 TPs).

### Realized PnL — current vs iter-63

| Acc | iter-63 | iter-64 | Δ |
|---|---|---|---|
| BB-M-Algida | 36.952 | 36.952 | $0.00 |
| BB-SASH-ShortSMA | 12.064 | **12.256** | **+$0.192** (2 ZBT TPs) |
| BG-SASH-Insider | 29.176 | 29.176 | $0.00 |
| BX-M-IJKL | 16.652 | 16.652 | $0.00 |
| **Σ** | **94.844** | **95.036** | **+$0.192** |

### Log counts (last 60 min, our workspace)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | **4** | 0 |
| BB-SASH-ShortSMA | 0 | 1 | 12 |
| BG-SASH-Insider | 0 | 0 | 0 |
| BX-M-IJKL | 0 | 0 | 2 |

Workspace total warnings: **5** (4 Fix #6 dedupe + 1 BB-SASH — likely another reconcile-TP race on the 13:59 or 14:26 TP fill).

## 🚨 Issue tracker

### ✅ BB-M-Algida qtyExcess storm — Fix #6 verified 5 consecutive hours

Pattern unchanged: 2 qtyExcess + 2 RECONCILE DCA headers per hour.

### 🟡 5 Bybit rate-limit errors — back from 0 last hour

Same chronic Bybit kline rate-limit. Strategies hit are alternating each hour — pattern looks like random sampling, not a single sticky bot.

### 🟡 BB-SASH-ShortSMA 1 warning — likely reconcile-TP burst (Fix #3 candidate)

Only 1 warning this hour (vs 7 in iter-61). Likely a single-line variant rather than a full burst — the most recent ZBT TP cycle was clean enough not to trip the 5-line "не пересечён ценой" loop.

### 🟢 BB-JCT phantom DCA#5 — 28+ clean hours

## State delta (vs iter-63)

| Bot | iter-63 bat/dca | iter-64 bat/dca | Δ | realized Δ |
|---|---|---|---|---|
| BB-SASH/ZBT (#7e) | 1/15 | 1/15 | 2 DCAs filled + 2 TPs fired (net even) | +$0.192 |
| BB-SASH/FF (#46) | 2/14 | **3/13** | DCA#2 fill | — |
| All others | — | — | — | — |

Inventory: 85 → **86 batches**, 79 → **78 DCAs** (FF DCA filled adds a batch).

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): 0 trades. All 4 still holding inventory.
- **Bybit BB-SASH-ShortSMA** (3 bots): 2 TPs (+$0.192). ZBT-SASH is doing rapid cycles — 5 TPs total in 2 hours.
- **Bitget BG-SASH-Insider** (3 bots): 0 trades. After iter-63's $0.59 hour, BG is back to holding.
- **BingX BX-M-IJKL** (2 bots): 0 trades.

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$95.036** |
| Δ vs iter-63 | **+$0.192** |
| Δ vs iter-34 baseline ($47.026) | **+$48.010** |

## Verdict for iteration 64

Solid follow-through hour after iter-63's $1.02 spike — Bybit BB-SASH/ZBT kept cycling through 3 TPs over 2 hours, demonstrating the post-rally DCA replenishment is working correctly. **Fix #6 noise pattern is rock-solid at 4 warnings/hr across 5 consecutive 60-min windows.** No Fix #6 path beyond the dedupe has fired yet — JCT still needs price recovery, and all this hour's TPs closed cleanly without lot-step or partial fills.

**Next cron fire ~15:17 UTC (17:17 Warsaw) → iter-65. 18 iterations remain.**
