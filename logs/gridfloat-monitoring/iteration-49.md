# GridFloat monitoring — Iteration 49

**Captured**: 2026-05-16 07:29 UTC (09:29 Warsaw)
**Δ from iteration-48**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **42 trades** (18 TPs, 24 DCAs), **+$4.187 realized** — **NEW SINGLE-HOUR RECORD** (more than double the previous record of iter-47's $1.88).
- 🚀 **BUSDT recovery rally** fired 4 big TPs: BG-BUSDT TP#8 +$0.876, BG-BUSDT TP#7 +$0.881, BX-BUSDT TP#8 +$0.436, BX-BUSDT TP#7 +$0.430 = **$2.62 from BUSDT-only**.
- 🟢 **BB-SAGA TP#13 fired** (+$0.588) — deepest DCA in the workspace finally cleared.
- 🚨 BX-BUSDT margin issue now spans **3 slots** (#5, #6, #9). 28 cooldown warnings this hour.
- 🟡 8-warning reconcile-TP noise burst on BX-BUSDT at 07:16:08 (Fix #3 candidate — same pattern as iter-45).
- ✅ 0 errors (15th clean hour); 0 phantom dupes (hour 15).

## Δ Activity since iter-48 — TPs only (DCAs summarized)

| Time UTC | Bot | Status | Qty | Price | PnL |
|---|---|---|---|---|---|
| 06:29:32 | BB-FF (#46) | TP#6 | 118 | 0.08505 | +$0.095 |
| 06:32:52 | BB-BANANAS (#4f) | TP#6 | 800 | 0.01161 | +$0.042 |
| 06:37:18 | BB-ZBT-SASH (#7e) | TP#1 | 60.5 | 0.16688 | +$0.096 |
| 06:52:41 | BB-ZBT-SASH (#7e) | TP#4 | 62.4 | 0.16182 | +$0.096 |
| 06:53:40 | BB-BANANAS (#4f) | TP#6 | 800 | 0.01161 | +$0.042 |
| 07:02:00 | BB-BANANAS (#4f) | TP#4 | 800 | 0.01173 | +$0.043 |
| 07:02:00 | BB-BANANAS (#4f) | TP#5 | 800 | 0.01167 | +$0.043 |
| 07:08:43 | BG-BUSDT (#3f) | **TP#8** | 74 | 0.41270 | **+$0.876** 🏆 |
| 07:14:13 | BX-BUSDT (#1c) | TP#8 | 36.37 | 0.41920 | **+$0.436** |
| 07:16:20 | BX-BUSDT (#1c) | TP#7 | 35.45 | 0.42470 | **+$0.430** |
| 07:16:36 | BB-BANANAS (#4f) | TP#5 | 800 | 0.01167 | +$0.043 |
| 07:18:37 | BB-FF (#46) | TP#6 | 118 | 0.08505 | +$0.095 |
| 07:19:05 | BB-FF (#46) | TP#5 | 117 | 0.08596 | +$0.095 |
| 07:19:32 | BG-BUSDT (#3f) | **TP#7** | 72 | 0.42890 | **+$0.881** 🏆 |
| 07:20:32 | BB-ZBT-SASH (#7e) | TP#6 | 63.7 | 0.15844 | +$0.095 |
| 07:23:18 | BB-SAGA (#c6) | **TP#13** | 890.2 | 0.02313 | **+$0.588** |
| 07:23:45 | BB-FF (#46) | TP#4 | 116 | 0.08686 | +$0.096 |
| 07:27:22 | BB-FF (#46) | TP#3 | 115 | 0.08776 | +$0.095 |

24 DCAs filled across BB-JCT, BB-SAGA, BB-ZBT (#67), BB-BANANAS, BB-FF, BB-ZBT-SASH, BG-BUSDT, BG-OPEN, BG-ZBT, BX-ZBT (most via reconcile-DCA on the BUSDT/ZBT downswing 06:50-07:15).

### Realized PnL delta

| Bot | iter-48 | iter-49 | Δ |
|---|---|---|---|
| BB-SAGA (#c6) | 12.340 | 12.928 | +$0.588 |
| BB-BANANAS (#4f) | 0.810 | 1.022 | +$0.213 (5 TPs) |
| BB-FF (#46) | 0.759 | 1.235 | **+$0.476** (5 TPs) |
| BB-ZBT-SASH (#7e) | 2.200 | 2.487 | +$0.287 (3 TPs) |
| BG-BUSDT (#3f) | 10.131 | 11.888 | **+$1.757** (2 TPs) 🏆 |
| BX-BUSDT (#1c) | 6.481 | 7.347 | **+$0.866** (2 TPs) |
| **Σ Δ** | | | **+$4.187** |

### Log counts (since 06:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 0 | 8 |
| BB-SASH-ShortSMA | 0 | 1 | 52 |
| BG-SASH-Insider | 0 | 1 | 14 |
| BX-M-IJKL | 0 | **31** | 10 |

The 31 BX-M-IJKL warnings:
- 18× margin-cooldown across 3 slots (#5, #6, #9)
- 9× the 07:16:08 reconcile-TP false-positive burst (Fix #3 candidate)
- 3× healthy reconcile-DCA adoptions
- 1× "После reconcile TP остаток qtyDelta" follow-up

## 🚨 Issue tracker

### 🚨 BX-BUSDT margin loop — now spanning 3 slots (#5, #6, #9)

Slot count of stuck DCAs has grown each iteration: iter-41 #7 → iter-46 #4 → iter-47 #5 → iter-48 #5+#9 → iter-49 **#5+#6+#9**. BingX margin is short enough that every successful TP frees one slot but the next-attempted DCA hits the wall. **PnL is fine** (BX-BUSDT just earned +$0.87 this hour), but log noise grows linearly.

### 🟡 Reconcile-TP false-positive burst on BX-BUSDT — 8 warnings at 07:16:08

Same shape as iter-45's BB-BANANAS event. ReconcileBatchesFromPosition detected a 5.96 qty drop, but `price=0.4237` was below every batch's TpPrice (0.4248 lowest), so reconcile correctly skipped — but each batch logged a "не пересечён ценой" line, producing 8 warnings for a single near-miss. **Fix #3 follow-up tightening still pending.**

### 🟢 BB-JCT phantom DCA#5 — 15 clean hours

BB-JCT DCA#5 reconcile-adopted at 07:07:47 (qty=2500, single record, no doubles).

### 🟢 BB-M-Algida error spam — 15th clean hour (still 0 errors)

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): 1 TP (BB-SAGA #13 +$0.588), 3 DCAs. The reconcile-DCA at 07:07 was the first BB-JCT DCA since iter-46.
- **Bybit BB-SASH-ShortSMA** (3 bots): 13 TPs (+$0.976), ~15 DCAs. Most volume.
- **Bitget** (3 bots): **2 BG-BUSDT TPs (+$1.757)** — biggest Bitget hour. Plus BG-ZBT and BG-OPEN DCAs.
- **BingX** (2 bots): **2 BX-BUSDT TPs (+$0.866)** + BX-ZBT DCAs.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 5 → 6 | 6 → 5 |
| BB-SAGA (#c6) | 12 → 13 | 9 → 9 (DCA + TP) |
| BB-ZBT (#67) | 3 → 4 | 8 → 7 |
| BB-BANANAS (#4f) | 7 → 7 | 19 → 19 (5 TP + 5 DCA = neutral) |
| BB-FF (#46) | 7 → 3 | 9 → 13 |
| BB-ZBT-SASH (#7e) | 2 → 6 | 14 → 10 |
| BG-BUSDT (#3f) | 8 → 7 | 6 → 7 |
| BG-OPEN (#b3) | 1 → 3 | 6 → 4 |
| BG-ZBT (#9d) | 1 → 3 | 6 → 4 |
| BX-BUSDT (#1c) | 9 → 7 | 0 → 2 |
| BX-ZBT (#0a) | 1 → 3 | 4 → 4 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$61.177** |
| Δ this iteration | **+$4.187** 🏆 (new record, 2.2× previous best) |
| Δ from iter-34 baseline | **+$14.151** |

## Verdict for iteration 49

The hour the strategy paid off across the board. BUSDT recovered to 0.43 after the 05:49 wick to 0.375 — the deep DCAs that bots had been holding (BG-BUSDT batches accumulated since iter-37, BX-BUSDT slot #7 since iter-41) all triggered TPs at 1-3% gain, banking **$2.62 from BUSDT-related bots alone**. BB-SAGA TP#13 marks the deepest DCA in the workspace finally closing — the bot has been carrying it since well before this monitoring run started. Strategy demonstrates exactly the value proposition of grid-float: hold inventory through drawdowns, close it on recoveries at 1-3% from fill. **Next cron fire ~08:17 UTC (10:17 Warsaw).**
