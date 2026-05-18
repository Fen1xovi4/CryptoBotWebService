# GridFloat monitoring — Iteration 54

**Captured**: 2026-05-16 12:30 UTC (14:30 Warsaw)
**Δ from iteration-53**: ~61 min
**Cron**: `17 * * * *` (Warsaw) — restored as job `7042ba25` after session restart

## TL;DR

- **18 trades** (12 TPs, 6 DCAs), **+$2.668 realized** — 4th big-payoff hour out of last 6.
- 🟢 **BUSDT recovery rally fired again**: BG-BUSDT TP#12 +$0.877 and TP#11 +$0.870, BX-BUSDT TP#11 ×2 + TP#10 +$0.555. **$2.30 from BUSDT alone**.
- 🟢 BB-BANANAS marched through TP#18 → TP#17 ×2 → TP#16 → TP#15 → TP#14 in 53 min as the small-step grid unwound the iter-52/53 inventory bottom up.
- 🚨 BB-M-Algida qtyExcess noise **escalated to 530 warnings** — same 3039.67 phantom unchanged from iter-53. Chronic until next BB-JCT DCA placement matches the offset.
- ✅ 0 errors (20th clean hour); 0 phantom Trade dupes (hour 20).

## Δ Activity since iter-53

### Trades (18) — TPs only

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 11:31:16 | BB-BANANAS (#4f) | Sell | TP#18 | 900 | 0.01089 | +$0.044 |
| 11:39:10 | BB-BANANAS (#4f) | Sell | TP#17 | 900 | 0.01095 | +$0.045 |
| 11:47:44 | BB-BANANAS (#4f) | Sell | TP#17 (re-fill) | 900 | 0.01095 | +$0.045 |
| 11:52:11 | BB-ZBT-SASH (#7e) | Sell | TP#5 | 63 | 0.16013 | +$0.096 |
| 12:00:23 | BB-BANANAS (#4f) | Sell | TP#16 | 900 | 0.01101 | +$0.045 |
| 12:06:41 | BX-BUSDT (#1c) | Sell | TP#11 | 58.34 | 0.34610 | +$0.190 |
| 12:06:43 | BG-BUSDT (#3f) | Sell | **TP#12** | 88 | 0.34750 | **+$0.877** 🏆 |
| 12:16:02 | BB-BANANAS (#4f) | Sell | TP#15 | 900 | 0.01107 | +$0.045 |
| 12:17:16 | BX-BUSDT (#1c) | Sell | TP#11 (re-fill) | 54.41 | 0.34620 | +$0.178 |
| 12:24:17 | BB-BANANAS (#4f) | Sell | TP#14 | 900 | 0.01113 | +$0.045 |
| 12:26:37 | BX-BUSDT (#1c) | Sell | TP#10 | 55.73 | 0.36230 | +$0.187 |
| 12:27:03 | BG-BUSDT (#3f) | Sell | **TP#11** | 84 | 0.36370 | **+$0.870** 🏆 |

6 DCAs across BB-BANANAS (×3 on #14, #17 re-arms), BB-ZBT-SASH (×1), BX-BUSDT (×1), BB-BANANAS (×1 final).

### Realized PnL delta

| Bot | iter-53 | iter-54 | Δ |
|---|---|---|---|
| BB-BANANAS (#4f) | 1.365 | 1.634 | +$0.269 (6 TPs) |
| BB-ZBT-SASH (#7e) | 3.538 | 3.633 | +$0.096 |
| BG-BUSDT (#3f) | 13.638 | 15.386 | **+$1.748** (2 TPs) 🏆 |
| BX-BUSDT (#1c) | 10.407 | 10.962 | +$0.555 (3 TPs) |
| **Σ Δ** | | | **+$2.668** |

### Log counts (since 11:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | **530** 🚨 | 0 |
| BB-SASH-ShortSMA | 0 | 63 | 24 |
| BG-SASH-Insider | 0 | 0 | 5 |
| BX-M-IJKL | 0 | 11 | 9 |

## 🚨 Issue tracker

### 🚨 BB-M-Algida qtyExcess phantom — escalating (399 → 530 warnings)

Same 3039.67 mismatch from iter-53 (BB-JCT partial TP fill at 10:43:45 left 42.69 dust + DCA#10 re-arm overshoot). Now ~2 hours old and unchanged because no new BB-JCT DCA placements have occurred (state.dcas=0, batches=11 — fully inventoried). Each tick adds 2 warnings, hence the linear growth.

**Operational impact**: zero PnL impact, but log throughput on BB-M-Algida is now ~9 warnings/min. Will continue until BB-JCT receives a new DCA fill or the position partially closes.

### 🚨 BX-BUSDT chronic — quieter this hour (212 → 11)

The chronic qtyExcess on BingX dropped this hour because the 3 BX-BUSDT TPs cleared the standing phantom. New DCA#11 at 12:11:43 placed/filled cleanly. Good sign — the bot can self-resolve when active.

### 🟢 BB-JCT phantom DCA#5 (iter-34 root issue) — **20 clean hours**

Original iter-34 17-dup pattern still absent.

### 🟢 BB-M-Algida error spam — 20th clean hour (errors, not warnings)

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): **0 trades**, just 530 warnings. All 4 bots holding deep inventory.
- **Bybit BB-SASH-ShortSMA** (3 bots): 6 TPs (+$0.365), 4 DCAs.
- **Bitget** (3 bots): **2 BG-BUSDT TPs (+$1.748)** on continued BUSDT recovery (0.327 → 0.364 in this hour).
- **BingX** (2 bots): 3 BX-BUSDT TPs (+$0.555), 1 DCA.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-BANANAS (#4f) | 18 → 15 | 8 → 11 |
| BG-BUSDT (#3f) | 13 → 11 | 1 → 3 |
| BX-BUSDT (#1c) | 12 → 10 | 3 → 6 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$73.073** |
| Δ this iteration | +$2.668 |
| Δ from iter-34 baseline ($47.026) | **+$26.046** |

## Verdict for iteration 54

Fourth $2+ hour out of the last six — BUSDT recovery continues to pay off across both Bitget and BingX, BB-BANANAS drains its accumulation, the strategy is humming. The BB-M-Algida qtyExcess noise is now a chronic but cosmetic issue: 530 warnings without errors or PnL impact. **Next cron fire ~13:17 UTC (15:17 Warsaw). 4 iterations remain (55, 56, 57, then iter-58 final).**
