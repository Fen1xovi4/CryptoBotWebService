# GridFloat monitoring — Iteration 58 (FINAL)

**Captured**: 2026-05-16 16:30 UTC (18:30 Warsaw)
**Δ from iteration-57**: ~60 min
**Cron**: `17 * * * *` (Warsaw) — job `7042ba25` (will be deleted)

## ✅ 24-hour run complete

This is iteration 24 of the 24-iteration monitoring loop, started from the iter-34 baseline at 2026-05-15 17:11 UTC. Cron will self-delete after this file is written.

## TL;DR

- **9 trades this hour** (7 TPs, 2 DCAs), **+$3.125 realized**.
- 🎯 Closing strong: BG-BUSDT TP#8 ×2 (+$1.755), BX-BUSDT TP#9 +$0.444 + TP#8 +$0.441 (+$0.884 total), BB-ZBT (#67) TP#3 +$0.296.
- 🎯 **24-hour total: +$37.93 across 12 bots** (sum of per-bot PnL from trades since baseline; matches state-side cumulative within rounding).
- ✅ 0 errors all 24 hours (iter-34 baseline rate was 112/hr).

## Δ Activity since iter-57

### Trades (9)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 15:33:32 | BB-ZBT (#67) | Sell | TP#3 | 62.9 | 0.16364 | +$0.295 |
| 15:33:32 | BB-ZBT-SASH (#7e) | Sell | TP#3 | 61.7 | 0.16350 | +$0.095 |
| 15:36:57 | BG-BUSDT (#3f) | Sell | **TP#8** | 76 | 0.40190 | **+$0.877** 🏆 |
| 15:36:58 | BX-BUSDT (#1c) | Sell | TP#9 | 39.44 | 0.39160 | +$0.444 |
| 15:42:22 | BG-BUSDT (#3f) | Buy  | DCA#8 | 76 | 0.39020 | — |
| 16:09:09 | BG-BUSDT (#3f) | Sell | **TP#8** (re-fill) | 76 | 0.40190 | **+$0.877** 🏆 |
| 16:19:46 | BB-FF (#46) | Sell | TP#1 | 109 | 0.09187 | +$0.094 |
| 16:21:15 | BB-ZBT-SASH (#7e) | Buy  | DCA#3 | 61.77 | 0.16189 | — |
| 16:23:23 | BX-BUSDT (#1c) | Sell | TP#8 | 37.84 | 0.40810 | +$0.441 |

### Realized PnL delta this hour

| Bot | iter-57 | iter-58 | Δ |
|---|---|---|---|
| BB-ZBT (#67) | 7.395 | 7.691 | +$0.296 |
| BB-FF (#46) | 2.373 | 2.467 | +$0.094 |
| BB-ZBT-SASH (#7e) | 4.016 | 4.112 | +$0.096 |
| BG-BUSDT (#3f) | 19.787 | 21.542 | **+$1.755** |
| BX-BUSDT (#1c) | 13.550 | 14.434 | +$0.884 |
| **Σ Δ** | | | **+$3.125** |

### Log counts (since 15:30 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 518 | 2 |
| BB-SASH-ShortSMA | 0 | 1 | 6 |
| BG-SASH-Insider | 0 | 0 | 6 |
| BX-M-IJKL | 0 | 0 | 4 |

## 🏁 Per-bot 24-hour summary

| Acc | Bot | Trades | TPs | 24h realized | Biggest TP |
|---|---|---|---|---|---|
| BB-M-Algida | **JCTUSDT** | 29 | 11 | **+$3.485** | $0.448 |
| BB-M-Algida | SAGAUSDT | 4 | 1 | +$0.588 | $0.588 |
| BB-M-Algida | XRPUSDT | 4 | 1 | +$0.095 | $0.095 |
| BB-M-Algida | ZBTUSDT | 10 | 6 | +$1.736 | $0.296 |
| BB-SASH | BANANAS31USDT | **102** | 41 | +$1.766 | $0.046 |
| BB-SASH | FFUSDT | 52 | 26 | +$2.467 | $0.096 |
| BB-SASH | ZBTUSDT | 87 | 42 | **+$4.016** | $0.096 |
| BG-SASH | **BUSDT** | 29 | 13 | **+$11.411** 🏆 | $0.884 |
| BG-SASH | OPENUSDT | 5 | 1 | +$0.290 | $0.290 |
| BG-SASH | ZBTUSDT | 14 | 7 | +$2.063 | $0.296 |
| BX-M-IJKL | **BUSDT** | 52 | 24 | **+$9.276** | **$0.908** 🏆 |
| BX-M-IJKL | ZBTUSDT | 10 | 5 | +$0.739 | $0.148 |
| **TOTAL** | **12 bots** | **398** | **178** | **+$37.93** | **$0.908** (BX-BUSDT TP#12 @ iter-53) |

## Per-exchange totals

| Exchange | Bots | Trades | TPs | 24h Realized |
|---|---|---|---|---|
| Bybit (BB-M-Algida) | 4 | 47 | 19 | +$5.903 |
| Bybit (BB-SASH-ShortSMA) | 3 | 241 | 109 | +$8.249 |
| Bitget (BG-SASH-Insider) | 3 | 48 | 21 | +$13.764 |
| BingX (BX-M-IJKL) | 2 | 62 | 29 | +$10.015 |

**Bitget is the highest-earning exchange of the run** ($13.76), driven entirely by BG-BUSDT. BingX second ($10.02). Bybit two-account combined: $14.15.

## 🚨 Issue tracker — FINAL STATUS

### ✅ BB-JCT phantom DCA#5 (iter-34 root issue) — **RESOLVED**

The 17-duplicate-Trade-rows pattern from iter-34 (16:05-16:08 UTC, 12-sec interval) did **not recur** in 24 hours of monitoring. Fix #5 sub-min batch cleanup in [GridFloatHandler.cs:396-407](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L396-L407) fired live **3+ times** during the run:
- iter-43 BB-JCT DCA#6 qty=53.73 → dropped (< 100 min)
- iter-55 BX-BUSDT DCA#11 qty=3.93 → dropped (< 5.56 min)
- iter-55 BX-BUSDT DCA#10 qty=3.93 → dropped (< 5.25 min)

The mechanism is operationally validated.

### ✅ BB-M-Algida error spam — **RESOLVED**

iter-34 baseline: 221 errors / 118 min = **112 errors/hour**. Iter-58 cumulative: **0 errors / 24 hours**. Root cause (sub-min batch trying to place TP at qty=0) was cleared by Fix #5. The error stream stopped at iter-35 and never returned.

### ✅ BX-BUSDT margin loop (iter-41 → iter-46) — **SELF-RESOLVED**

Migrated through slots #7 → #4 → #5 → #6 → #9 across iter-41 to iter-48 as the BingX account juggled limited free margin. Eventually all slots filled or were cleared by reconcile-DCA adoptions on BUSDT wicks. Total impact: 0 PnL loss; BX-BUSDT was the **2nd highest earner** ($9.28). Margin warnings ceased after iter-50.

### 🚨 qtyExcess phantom pattern (NEW, identified iter-50 → iter-58) — **UNRESOLVED**

A chronic state-vs-exchange qty mismatch first observed on BX-BUSDT (iter-50, 185 warnings/hr) and later on BB-M-Algida (iter-53 onwards, 400-530 warnings/hr).

**Root cause** (analyzed iter-56):
- TP limit placed at `batch.Qty=3042.69`, exchange fills exactly 3000 (lot-size rounding), 42.69 dust remains
- State records `Trade.Quantity=3000` and drops the full batch from `state.Batches`
- Next tick: exchange has 42.69 leftover + 3042.69 from next DCA fill = 3085 excess
- `ReconcileMissedDcaFills` at [GridFloatHandler.cs:817-861](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L817-L861) detects the excess but has no `state.DcaOrder` to adopt it into → logs every ~13 seconds indefinitely

**24-hour log totals**:
- BB-M-Algida: **2,986 warnings** (~99% qtyExcess on BB-JCT from iter-53)
- BX-M-IJKL: **832 warnings** (qtyExcess + margin loops, mostly iter-41-55)

**Fix #6 proposal** (drafted iter-56):
1. After a partial TP fill, query exchange residual qty for the symbol; create a leftover mini-batch tracking that qty at the original FillPrice.
2. Place TP limits with qty floored to the symbol's lot step (avoid the fractional remainder).
3. Reconcile path: when `qtyExcess` magnitude matches "lastFill residual + DCA size", auto-merge into a single batch.

**Operational severity**: cosmetic — no PnL impact, no missed fills, but generates ~70k warnings/day at current scale. Would page a monitoring dashboard.

## Cross-exchange health (final)

- **Bybit BB-M-Algida** (4 bots): 4 TP-bearing bots; biggest contributor BB-JCT (+$3.49). The qtyExcess chronic on BB-JCT contributes 99% of the 2986-warning total.
- **Bybit BB-SASH-ShortSMA** (3 bots): Most active by trade count (241 trades). BB-ZBT-SASH cycled most: 87 trades, 42 TPs. Workhorse on small-tier ($10) grids.
- **Bitget** (3 bots): Highest-earning exchange (+$13.76). BG-BUSDT alone +$11.41 (30% of run total).
- **BingX** (2 bots): +$10.02 from 2 bots. BX-BUSDT printed the run's largest single TP ($0.908) at iter-53.

## State at end (final batches/dcas snapshot)

| Bot | batches | dcas | Anchor |
|---|---|---|---|
| BB-JCT (#3) | 11 | 0 | 0.0046951 |
| BB-SAGA (#c6) | 14 | 8 | 0.03683 |
| BB-XRP (#e7) | 9 | 2 | 1.5378 |
| BB-ZBT (#67) | 3 | 8 | 0.17460 |
| BB-BANANAS (#4f) | 21 | 5 | 0.011914 |
| BB-FF (#46) | 1 | 15 | 0.09189 (re-anchored 10:15 May 16) |
| BB-ZBT-SASH (#7e) | 4 | 12 | 0.16690 |
| BG-BUSDT (#3f) | 8 | 6 | 0.5273 |
| BG-OPEN (#b3) | 5 | 2 | 0.1817 |
| BG-ZBT (#9d) | 2 | 5 | 0.166 (re-anchored 18:00 May 15) |
| BX-BUSDT (#1c) | 8 | 8 | 0.5356 |
| BX-ZBT (#0a) | 2 | 5 | 0.16683 (re-anchored 05:00 May 16) |

Inventory total: **88 open batches + 76 pending DCAs** = sized for the next big move.

## Cumulative scoreboard — FINAL

| | Value |
|---|---|
| Sum of state-side realized PnL | **$84.958** |
| iter-34 baseline | $47.026 |
| **24h realized gain** | **+$37.932** |
| **Best hour** | iter-55 +$5.787 |
| **Best single TP** | BX-BUSDT TP#12 +$0.908 (iter-53) |
| **Worst hour** | iter-37, iter-38, iter-40, iter-43 (all +$0 ↔ +$0.09 quiet hours) |
| **0-error hours** | 23/24 (iter-34 itself was the only error-producing hour, with 221 errors) |

## ✅ Verdict — 24-hour run complete

**Strategy verdict**: GridFloat works as designed across all 3 exchanges. The thesis — *hold inventory through drawdowns, close on partial recoveries at 1-3% from fill* — was demonstrably profitable: 4 distinct payoff-rallies generated >$2 each (iter-47, 49, 51, 55), and the largest hour (iter-55) closed $5.79 from 14 TPs across BG/BX BUSDT and Bybit micro-caps.

**Bug/quality findings**:
1. Fix #5 (sub-min batch cleanup) was validated live 3+ times — keep it in.
2. New Fix #6 needed: partial-TP-fill leaves persistent qtyExcess orphan that reconcile-DCA cannot adopt. Cosmetic only, but generates ~70k warnings/day at current scale.
3. Fix #3 follow-up still pending: 0.06% TP-price tolerance gap in reconcile-TP causes 5-warning bursts per near-miss. Not urgent.
4. Reconcile-DCA backstop ([GridFloatHandler.cs:817-861](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L817-L861)) and reconcile-TP backstop ([GridFloatHandler.cs:762-806](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L762-L806)) both fired correctly multiple times — strategy is resilient to exchange/state desynchronization.

**Bot health**: 12/12 bots earned positive PnL. Lowest earner: BG-OPEN +$0.29 (single TP). Highest: BG-BUSDT +$11.41 (30% of run total).

📌 **This is the final iteration. Cron `7042ba25` will be deleted next.**
