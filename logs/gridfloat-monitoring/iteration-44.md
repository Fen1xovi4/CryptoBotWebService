# GridFloat monitoring — Iteration 44

**Captured**: 2026-05-16 02:29 UTC (04:29 Warsaw)
**Δ from iteration-43**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **15 trades** (8 TPs, 7 DCAs), **+$0.548 realized** — all on **BB-SASH-ShortSMA** (3 bots).
- 🟢 **BB-BANANAS** ground out 4 DCAs followed by 4 TPs in 30 min (DCA#5→6→7→8, then TP#8→7→6→5) on a +0.5% bounce — textbook small-step grid behavior.
- ✅ 0 errors (10th clean hour). 0 phantom dupes (hour 10).
- 🟡 **BB-M-Algida silent** this hour (no trades on the 4 big bots), and Bitget+BingX both idle.

## Δ Activity since iter-43

### Trades (15)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 01:31:35 | BB-ZBT-SASH (#7e) | Sell | TP#3  | 64.10 | 0.15722 | +$0.096 |
| 01:35:02 | BB-FF (#46) | Sell | TP#1  | 112   | 0.08957 | +$0.094 |
| 01:35:24 | BB-BANANAS (#4f) | Buy  | DCA#5 | 800   | 0.01162 | — |
| 01:36:25 | BB-BANANAS (#4f) | Buy  | DCA#6 | 800   | 0.01156 | — |
| 01:41:40 | BB-FF (#46) | Buy  | DCA#1 | 112   | 0.08869 | — |
| 01:52:31 | BB-ZBT-SASH (#7e) | Buy  | DCA#3 | 64.20 | 0.15566 | — |
| 02:01:51 | BB-BANANAS (#4f) | Buy  | DCA#7 | 800   | 0.01150 | — |
| 02:05:16 | BB-BANANAS (#4f) | Buy  | DCA#8 | 800   | 0.01144 | — |
| 02:05:26 | BB-ZBT-SASH (#7e) | Sell | TP#3  | 64.20 | 0.15721 | +$0.096 |
| 02:06:01 | BB-BANANAS (#4f) | Sell | TP#8  | 800   | 0.01149 | +$0.042 |
| 02:06:16 | BB-BANANAS (#4f) | Sell | TP#7  | 800   | 0.01155 | +$0.042 |
| 02:07:16 | BB-BANANAS (#4f) | Sell | TP#6  | 800   | 0.01161 | +$0.042 |
| 02:15:24 | BB-FF (#46) | Sell | TP#1  | 112   | 0.08957 | +$0.094 |
| 02:18:16 | BB-BANANAS (#4f) | Sell | TP#5  | 800   | 0.01167 | +$0.043 |
| 02:25:09 | BB-FF (#46) | Buy  | DCA#1 | 112   | 0.08869 | — |

### Realized PnL delta

| Bot | iter-43 | iter-44 | Δ |
|---|---|---|---|
| BB-BANANAS (#4f) | 0.302 | 0.470 | **+$0.168** (4 TPs) |
| BB-FF (#46) | 0.378 | 0.567 | **+$0.189** (2 TPs) |
| BB-ZBT-SASH (#7e) | 1.052 | 1.243 | **+$0.191** (2 TPs) |
| **Σ Δ** | | | **+$0.548** |

### Log counts (since 01:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 0 | 0 |
| BB-SASH-ShortSMA | 0 | 3 | 30 |
| BG-SASH-Insider | 0 | 0 | 0 |
| BX-M-IJKL | 0 | 0 | 0 |

All 3 warnings are healthy `🔎 RECONCILE DCA` adoption notices that triggered alongside DCA fills (Bybit's fill detection lagged behind the exchange).

## 🚨 Issue tracker

### 🟢 BB-JCT phantom DCA#5 — 10 clean hours
### 🟢 BB-M-Algida error spam — 10th clean hour
### 🟢 BX-BUSDT margin-cooldown (from iter-41) — 2nd consecutive silent hour (no new warnings)

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): **idle** — 0 trades, 0 logs.
- **Bybit BB-SASH-ShortSMA** (3 bots): 8 TPs (+$0.548), 7 DCAs. All the action this hour was here.
- **Bitget** (3 bots): idle. lastPrices within ±0.3% of iter-43 — no triggers.
- **BingX** (2 bots): idle.

## State delta

| Bot | batches Δ | dcas Δ | notes |
|---|---|---|---|
| BB-BANANAS (#4f) | 5 → 5 | 21 → 21 | 4 DCA + 4 TP cycle |
| BB-FF (#46) | 2 → 2 | 14 → 14 | 1 DCA + 1 TP + re-DCA |
| BB-ZBT-SASH (#7e) | 4 → 3 | 12 → 13 | 1 DCA + 2 TPs |

All other bots unchanged.

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$52.220** |
| Δ this iteration | +$0.548 |
| Δ from iter-34 baseline | +$5.194 |

## Verdict for iteration 44

Pure BB-SASH-ShortSMA hour — the small-step ($10 tier / 0.5% TP for BANANAS, 1% for FF/ZBT) configuration captured a tight 0.7% downswing-and-recovery on BANANAS31USDT for $0.169 of pure grid-grind PnL across 4 batches. Demonstrates the floating-grid strategy is most efficient on micro-cap symbols with frequent intra-bar oscillation. BB-M-Algida, Bitget, BingX all dormant — their grids are anchored too far above the current price band to fire on a sideways tick. **Next cron fire ~03:17 UTC (05:17 Warsaw).**
