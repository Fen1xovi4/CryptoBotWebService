# GridFloat monitoring — Iteration 57

**Captured**: 2026-05-16 15:30 UTC (17:30 Warsaw)
**Δ from iteration-56**: ~60 min
**Cron**: `17 * * * *` (Warsaw) — job `7042ba25`

## TL;DR

- **12 trades** (5 TPs, 7 DCAs), **+$0.478 realized** — quiet hour after iter-55's $5.79 record.
- 🟢 BB-BANANAS accumulated 4 deep DCAs (#17-#20) at 1.07-1.09 ¢ range — primed for next bounce.
- 🟡 BB-SASH-ShortSMA had a **Fix #3-style reconcile-TP burst** at 15:01:04 — 7 warnings logged when ZBT-SASH's TP#4 fill was caught by reconcile but couldn't cross-check against state.LastPrice (price 0.16173 vs batch TPs at 0.16182+).
- 🚨 BB-M-Algida qtyExcess loop continues — 514 warnings, no change.
- ✅ 0 errors (23rd clean hour); 0 phantom Trade dupes (hour 23).

## Δ Activity since iter-56

### Trades (12) — TPs

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 14:31:08 | BB-ZBT-SASH (#7e) | Sell | TP#5 | 63 | 0.16013 | +$0.096 |
| 14:46:07 | BB-ZBT-SASH (#7e) | Sell | TP#5 (re-fill) | 63 | 0.16013 | +$0.096 |
| 14:46:52 | BB-FF (#46) | Sell | TP#3 | 112 | 0.09002 | +$0.095 |
| 15:01:17 | BB-ZBT-SASH (#7e) | Sell | TP#4 | 62.4 | 0.16182 | +$0.096 |
| 15:12:20 | BB-FF (#46) | Sell | TP#2 | 111 | 0.09095 | +$0.096 |

7 DCAs: BB-BANANAS DCA#17/#18/#19/#20 (deep accumulation), BB-FF DCA#3, BB-ZBT-SASH DCA#5, BX-BUSDT DCA#9.

### Realized PnL delta

| Bot | iter-56 | iter-57 | Δ |
|---|---|---|---|
| BB-FF (#46) | 2.182 | 2.373 | +$0.191 (2 TPs) |
| BB-ZBT-SASH (#7e) | 3.729 | 4.016 | +$0.287 (3 TPs) |
| **Σ Δ** | | | **+$0.478** |

### Log counts (since 14:30 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | **514** | 0 |
| BB-SASH-ShortSMA | 0 | 9 | 22 |
| BG-SASH-Insider | 0 | 0 | 0 |
| BX-M-IJKL | 0 | 0 | 2 |

## 🚨 Issue tracker

### 🚨 BB-M-Algida qtyExcess phantom — chronic (5h old, 514 warnings/hr stable)

Pattern unchanged. The 3082.36 phantom hasn't been triggered to change (no new BB-JCT TP+DCA cycle this hour). Will likely remain until a BB-JCT TP fills with full qty 3043 instead of rounded 3000, or until manual position close.

### 🟡 BB-SASH reconcile-TP false-positive burst at 15:01:04 (Fix #3 candidate)

7 warnings for one event: state.qty=305.6 vs exchange qty=243.3 (-62.3 = TP fill on BB-ZBT-SASH batch #4 at price 0.16173, but batch #4's TpPrice was 0.16182 — 0.06% above). Same pattern as iter-45's BB-BANANAS and iter-49's BX-BUSDT. The 0.06% tolerance gap creates 5 "не пересечён ценой" log lines per occurrence.

### 🟢 BX-BUSDT chronic — **2nd consecutive silent hour**

### 🟢 BB-JCT phantom DCA#5 (iter-34 root) — **23 clean hours**

### 🟢 BB-M-Algida error spam (errors only) — 23rd clean hour

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): **idle** (0 trades), only the chronic qtyExcess noise.
- **Bybit BB-SASH-ShortSMA** (3 bots): 5 TPs (+$0.478), 5 DCAs. All this hour's activity sat here.
- **Bitget** (3 bots): **fully idle**.
- **BingX** (2 bots): 1 BX-BUSDT DCA#9 re-arm, 0 TPs.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-BANANAS (#4f) | 17 → 21 | 9 → 5 |
| BB-FF (#46) | 3 → 2 | 13 → 14 |
| BB-ZBT-SASH (#7e) | 6 → 4 | 10 → 12 |
| BX-BUSDT (#1c) | 9 → 10 | 7 → 6 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$81.833** |
| Δ this iteration | +$0.478 |
| Δ from iter-34 baseline ($47.026) | **+$34.807** |

## Verdict for iteration 57

Quiet hour — markets cooled off after iter-55's record breaker. BB-BANANAS deepened its accumulation pile (now 21 batches!) on a continued 1.5% drift down — sets up another payoff iteration if it bounces. Bitget and BingX both contributed 0 TPs but their grids are sitting on big inventory for the next recovery move. **Next cron fire ~16:17 UTC (18:17 Warsaw) → iter-58 = FINAL ITERATION (24h run complete, cron self-deletes).**
