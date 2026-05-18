# GridFloat monitoring — Iteration 63 (Phase 2)

**Captured**: 2026-05-17 13:39 UTC (15:39 Warsaw)
**Δ from iteration-62**: ~60 min
**Cron**: `17 * * * *` (Warsaw) — job `bb358590`

## TL;DR

- ✅ Fix #6 dedupe **4th clean hour**: same 4 warnings on BB-M-Algida (2 qtyExcess + 2 reconcile-DCA headers). Pattern unchanged.
- 🎯 **Best hour of Phase 2 so far** — **6 TPs (+$1.023)** triggered by a cross-exchange ZBT rally. ZBT TP'd simultaneously on 3 exchanges within 1 min (BB-SASH ZBT TP#1 + BG-SASH ZBT TP#1 + BX-M ZBT TP#1 all at 13:38-13:39).
- 🏆 BG-SASH/OPEN earned its first TP since iter-58 (+$0.293, qty 55 @ 0.18710 = TP#0 = anchor close).
- 🟢 0 worker errors this hour (down from 4).
- 🟡 0 lot-step / 0 partial-fill fires still — all 6 TPs were clean full-close transitions (state.Batches.Remove path).

## Δ Activity since iter-62

### Trades (8) — 6 TPs + 1 DCA + 1 Entry

| Time UTC | Acc | Symbol | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|---|
| 12:51:27 | BB-SASH | ZBTUSDT | Sell | TP#3 | 61.7 | 0.16350 | +$0.095 |
| 13:02:33 | BB-M-Algida | SAGAUSDT | Buy | DCA#13 | 890.2 | 0.02246 | — |
| 13:31:43 | BG-SASH | OPENUSDT | Sell | **TP#0** ⭐ | 55 | 0.18710 | **+$0.293** |
| 13:35:11 | BG-SASH | OPENUSDT | Buy | Entry | 53 | 0.18780 | — |
| 13:38:32 | BB-SASH | ZBTUSDT | Sell | TP#2 | 61.1 | 0.16519 | +$0.096 |
| 13:38:42 | BG-SASH | ZBTUSDT | Sell | **TP#1** | 62 | 0.16585 | **+$0.295** |
| 13:39:13 | BX-M-IJKL | ZBTUSDT | Sell | TP#1 | 30.9 | 0.16667 | +$0.148 |
| 13:39:13 | BB-SASH | ZBTUSDT | Sell | TP#1 | 60.5 | 0.16688 | +$0.096 |

Hour total: **+$1.023** 🎯

### Realized PnL — current vs iter-62

| Acc | iter-62 | iter-63 | Δ |
|---|---|---|---|
| BB-M-Algida | 36.952 | 36.952 | $0.00 (SAGA DCA only) |
| BB-SASH-ShortSMA | 11.778 | **12.064** | **+$0.286** (3 ZBT TPs) |
| BG-SASH-Insider | 28.587 | **29.176** | **+$0.589** (OPEN TP + ZBT TP) |
| BX-M-IJKL | 16.504 | **16.652** | **+$0.148** (ZBT TP) |
| **Σ** | **93.821** | **94.844** | **+$1.023** 🎯 |

### Log counts (last 60 min, our workspace)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | **4** | 2 |
| BB-SASH-ShortSMA | 0 | 0 | 4 |
| BG-SASH-Insider | 0 | 0 | 15 |
| BX-M-IJKL | 0 | 0 | 0 |

Workspace total warnings: **4** (all Fix #6 dedupe envelope). **4 consecutive clean hours**.

## 🚨 Issue tracker

### ✅ BB-M-Algida qtyExcess storm — Fix #6 verified 4 consecutive hours

Identical signature each hour: 2 qtyExcess + 2 RECONCILE DCA headers = 4 lines. Bullet-proof.

### 🟡 Fix #6 PlaceBatchTpLimit / RecordTpFill — still not exercised

All 6 TPs this hour were clean full-close transitions (qty matched batch.Qty exactly, no residual). Need either:
- A partial Bybit TP fill on JCT (unlikely until JCT recovers — still ~-6% below TP)
- A new TP placement on a batch with fractional qty (most batches in this workspace have already-rounded integer qtys)

May need to wait for a fresh anchor on a high-volatility low-priced micro-cap to trigger.

### 🟡 Cross-exchange ZBT rally — coordinated 3-exchange close

ZBT pumped from ~0.1605 to ~0.1670 in ~5 min (4% move at 13:36-13:39). All three exchanges' ZBT bots hit their TP#1 batch within 1 minute of each other. This is **exactly the payoff scenario the multi-exchange grid was designed to capture** — the system worked as intended.

### 🟢 BB-JCT phantom DCA#5 — 27+ clean hours

## State delta (vs iter-62)

| Bot | iter-62 bat/dca | iter-63 bat/dca | Δ | realized Δ |
|---|---|---|---|---|
| BB-M-Algida/SAGA (#c6) | 13/9 | **14/8** | DCA#13 fill | — |
| BB-SASH/ZBT (#7e) | 4/12 | **1/15** | 3 TP fills! | +$0.286 |
| BG-SASH/OPEN (#b3) | 1/6 | 1/6 | TP→Entry cycle | +$0.293 |
| BG-SASH/ZBT (#9d) | 2/5 | **1/6** | 1 TP fill | +$0.295 |
| BX-M/ZBT (#0a) | 2/5 | **1/6** | 1 TP fill | +$0.148 |
| All others | — | — | — | — |

Inventory: 89 → **85 batches**, 75 → **79 DCAs** (5 batches closed via TP, 4 slots re-armed to DCAs, +1 SAGA DCA fill).

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): 0 TPs, 1 DCA (SAGA #13 — first SAGA DCA in 4h, indicating SAGA hit deep).
- **Bybit BB-SASH-ShortSMA** (3 bots): 3 TPs ($0.286). ZBT-SASH cleared from 4 batches → 1 in one hour.
- **Bitget BG-SASH-Insider** (3 bots): 2 TPs ($0.589). OPEN earned its 2nd TP of the run.
- **BingX BX-M-IJKL** (2 bots): 1 TP ($0.148). ZBT TP#1 — first BX-ZBT TP since iter-58.

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$94.844** |
| Δ vs iter-62 | **+$1.023** 🎯 |
| Δ vs iter-34 baseline ($47.026) | **+$47.818** |

## Verdict for iteration 63

**Best Phase-2 hour so far** — proves the strategy is still earning normally under Fix #6, with **zero degradation** vs Phase 1. The cross-exchange ZBT TP cluster is the textbook payoff pattern: when one alt rallies briefly, all three exchanges close batches at near-anchor TPs and re-arm DCAs for the next dip. Fix #6 noise stays pinned at 4 warnings/hr — the dedupe envelope is doing exactly what was designed.

Phase 2 cumulative gain (since iter-58 final $84.958): **+$9.886 over 4 monitored hours + the ~19h gap**.

**Next cron fire ~14:17 UTC (16:17 Warsaw) → iter-64. 19 iterations remain.**
