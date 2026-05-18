# GridFloat monitoring — Iteration 62 (Phase 2)

**Captured**: 2026-05-17 12:39 UTC (14:39 Warsaw)
**Δ from iteration-61**: ~60 min
**Cron**: `17 * * * *` (Warsaw) — job `bb358590`

## TL;DR

- ✅ Fix #6 dedupe **3rd clean hour**: still exactly 2 qtyExcess fires + 2 reconcile-DCA headers = 4 warnings on BB-M-Algida. Bulletproof.
- 🟢 1 TP (+$0.095): BB-SASH/FFUSDT TP#2 at 11:44. FF cycled TP#3→DCA→TP#2 in 28 minutes.
- 🟢 3 DCAs filled: BB-SASH/ZBT DCA#3, BX-M/BUSDT DCA#8, **BG-SASH/BUSDT DCA#8** — first BG-SASH activity in 3+ hours.
- 🟡 4 Bybit rate-limit errors (SAGA/BANANAS/ZBT-Algida/JCT) — transient, self-heal next tick. Same pattern as iter-59.
- 🟡 0 lot-step / 0 partial-TP fires — TPs still full closes. JCT inventory holding without trigger.

## Δ Activity since iter-61

### Trades (4)

| Time UTC | Acc | Symbol | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|---|
| 11:44:09 | BB-SASH | FFUSDT | Sell | TP#2 | 110 | 0.09149 | +$0.095 |
| 11:59:46 | BB-SASH | ZBTUSDT | Buy | DCA#3 | 61.7 | 0.16189 | — |
| 12:02:23 | BX-M-IJKL | BUSDT | Buy | DCA#8 | 37.84 | 0.39630 | — |
| 12:16:08 | BG-SASH | BUSDT | Buy | DCA#8 | 76 | 0.39020 | — |

Hour total: **+$0.095** (1 TP, 3 DCAs).

### Realized PnL — current vs iter-61

| Acc | iter-61 | iter-62 | Δ |
|---|---|---|---|
| BB-M-Algida | 36.952 | 36.952 | $0.00 |
| BB-SASH-ShortSMA | 11.683 | **11.778** | **+$0.095** (FF TP#2) |
| BG-SASH-Insider | 28.587 | 28.587 | $0.00 (DCA only) |
| BX-M-IJKL | 16.504 | 16.504 | $0.00 (DCA only) |
| **Σ** | **93.726** | **93.821** | **+$0.095** |

### Log counts (last 60 min, our workspace)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | **4** | 0 |
| BB-SASH-ShortSMA | 0 | 0 | 4 |
| BG-SASH-Insider | 0 | 0 | 2 |
| BX-M-IJKL | 0 | 0 | 2 |

Warning total: **4** (all BB-M-Algida, all from Fix #6 dedupe envelope). Compare iter-57: 503 warnings/hr → now **4 warnings/hr** = **-99.2%**.

## 🚨 Issue tracker

### ✅ BB-M-Algida qtyExcess storm — Fix #6 verified across 3 clean hours

Now have iter-59, iter-61, iter-62 all showing the same exact pattern: 1 RECONCILE DCA header + 1 qtyExcess body = 2 lines per fire, 2 fires per hour. Total 4 warnings/hr, sustained.

### 🟡 Bybit rate-limit transient errors (4 this hour) — chronic, harmless

Strategies hit on this run: c60574be (SAGA), 4f733480 (BANANAS), 67e5fa86 (ZBT-Algida), 783dcdab (JCT). Bybit periodically rate-limits the kline endpoint when 7 Bybit strategies tick concurrently. Each strategy retries on the next 5s tick — observed strategies do not lose state or skip trades. This is unrelated to Fix #6.

### 🟡 Fix #6 PlaceBatchTpLimit / RecordTpFill — still not exercised after 3h

Every TP this hour was a clean full close (state.Batches.Remove path). Need either:
- A BB-JCT TP fire (the 3082.36 orphan source) → would test PlaceBatchTpLimit lot-step floor
- Any Bybit partial-fill response → would test RecordTpFill residual handler

JCT price is still ~-7% below batch #0 TP — no recovery yet.

### 🟢 BB-JCT phantom DCA#5 — 26+ clean hours

## State delta (vs iter-61)

| Bot | iter-61 bat/dca | iter-62 bat/dca | Δ | realized Δ |
|---|---|---|---|---|
| BB-SASH/FF (#46) | 3/13 | **2/14** | TP fill | +$0.095 |
| BB-SASH/ZBT (#7e) | 3/13 | **4/12** | DCA fill | — |
| BG-SASH/BUSDT (#3f) | 8/6 | **9/5** | DCA fill | — |
| BX-M/BUSDT (#1c) | 8/8 | **9/7** | DCA fill | — |
| All others | — | — | — | — |

Inventory: 87 → **89 batches**, 77 → **75 DCAs** (3 DCA fills - 1 TP fill = +2 batches net).

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): 0 trades. Holding inventory (33 batches across 4 bots).
- **Bybit BB-SASH-ShortSMA** (3 bots): 1 TP + 1 DCA. Active cycling.
- **Bitget BG-SASH-Insider** (3 bots): 1 DCA fill on BUSDT — first BG-SASH activity since iter-58. BG-BUSDT slot #8 hit at 0.39020.
- **BingX BX-M-IJKL** (2 bots): 1 DCA fill on BUSDT slot #8 at 0.39630.

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$93.821** |
| Δ vs iter-61 | **+$0.095** |
| Δ vs iter-34 baseline ($47.026) | **+$46.795** |

## Verdict for iteration 62

Quiet but healthy hour. **Fix #6 has now sustained perfect performance across 3 consecutive clean 60-min windows** — confidence is high that the dedupe is stable. BUSDT pairs across both Bitget and BingX dropped into deeper DCAs (slot #8 = ~3% below anchor) which sets up a potential payoff iteration if the move retraces. FF on Bybit continues to be the workhorse, completing its 2nd full TP cycle today.

**Next cron fire ~13:17 UTC (15:17 Warsaw) → iter-63. 20 iterations remain.**
