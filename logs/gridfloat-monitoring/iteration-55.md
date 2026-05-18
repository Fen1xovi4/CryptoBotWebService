# GridFloat monitoring — Iteration 55

**Captured**: 2026-05-16 13:30 UTC (15:30 Warsaw)
**Δ from iteration-54**: ~60 min
**Cron**: `17 * * * *` (Warsaw) — job `7042ba25`

## TL;DR

- **28 trades** (14 TPs, 14 DCAs), **+$5.787 realized** — 🏆 **NEW SINGLE-HOUR RECORD** (beats iter-49's $4.19).
- 🎯 **BG-BUSDT booked 4 TPs in 19 min**: TP#10 (+$0.879), TP#9 (+$0.884), TP#8 ×2 (+$0.877 each) — **$3.517 from one bot** alone.
- 🎯 **BX-BUSDT 5 TPs**: TP#10 ×2, TP#9 ×2, TP#8 = +$1.701.
- 🟢 **Fix #5 sub-min cleanup fired twice** on BX-BUSDT (DCA#11 qty=3.93 < 5.56 min; DCA#10 qty=3.93 < 5.25 min) — confirms the cleanup path is the right guard.
- 🚨 BB-M-Algida qtyExcess loop persists (511 warnings, phantom morphed 3039.67 → 3082.36 after BB-JCT TP+DCA cycle at 13:20).
- ✅ 0 errors (21st clean hour); 0 phantom Trade dupes (hour 21).

## Δ Activity since iter-54 — TPs

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 12:34:05 | BB-ZBT-SASH (#7e) | Sell | TP#5 | 63 | 0.16014 | +$0.096 |
| 12:34:07 | BB-BANANAS (#4f) | Sell | TP#14 | 900 | 0.01113 | +$0.046 |
| 12:34:16 | BX-BUSDT (#1c) | Sell | TP#10 | 55.73 | 0.36240 | +$0.190 |
| 12:37:47 | BB-BANANAS (#4f) | Sell | TP#13 | 800 | 0.01119 | +$0.041 |
| 12:41:09 | BX-BUSDT (#1c) | Sell | TP#10 (re-fill) | 55.73 | 0.36230 | +$0.187 |
| 12:42:45 | BG-BUSDT (#3f) | Sell | **TP#10** | 81 | 0.38010 | **+$0.879** 🏆 |
| 12:44:06 | BG-BUSDT (#3f) | Sell | **TP#9** | 80 | 0.38550 | **+$0.884** 🏆 |
| 12:47:25 | BX-BUSDT (#1c) | Sell | **TP#9** | 39.44 | 0.39160 | +$0.441 |
| 12:53:03 | BG-BUSDT (#3f) | Sell | **TP#8** | 76 | 0.40190 | **+$0.877** 🏆 |
| 13:06:00 | BX-BUSDT (#1c) | Sell | TP#9 (re-fill) | 39.44 | 0.39160 | +$0.441 |
| 13:10:05 | BG-BUSDT (#3f) | Sell | **TP#8** (re-fill) | 76 | 0.40190 | **+$0.877** 🏆 |
| 13:13:19 | BX-BUSDT (#1c) | Sell | TP#8 | 37.84 | 0.40820 | +$0.443 |
| 13:19:41 | BB-FF (#46) | Sell | TP#3 | 112 | 0.09002 | +$0.096 |
| 13:20:08 | BB-JCT (#3) | Sell | TP#10 (partial 3000/3043 again) | 3000 | 0.00339 | +$0.292 |

14 DCAs (mostly BB-FF, BG-BUSDT, BX-BUSDT re-arms; 2 BX-BUSDT sub-min auto-purged by Fix #5; 1 BB-JCT DCA#10 re-arm completing the cycle).

### Realized PnL delta

| Bot | iter-54 | iter-55 | Δ |
|---|---|---|---|
| BB-JCT (#3) | 11.777 | 12.069 | +$0.292 |
| BB-BANANAS (#4f) | 1.634 | 1.720 | +$0.087 |
| BB-FF (#46) | 2.086 | 2.182 | +$0.096 |
| BB-ZBT-SASH (#7e) | 3.633 | 3.729 | +$0.096 |
| BG-BUSDT (#3f) | 15.386 | 18.903 | **+$3.517** (4 TPs) 🏆 |
| BX-BUSDT (#1c) | 10.962 | 12.663 | **+$1.701** (5 TPs) |
| **Σ Δ** | | | **+$5.787** 🏆 |

### Log counts (since 12:30 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | **511** | 4 |
| BB-SASH-ShortSMA | 0 | 0 | 16 |
| BG-SASH-Insider | 0 | 1 | 12 |
| BX-M-IJKL | 0 | 4 | 24 |

## 🚨 Issue tracker

### 🚨 BB-M-Algida qtyExcess phantom — morphed but persistent (511 warnings)

After the BB-JCT TP#10 + DCA#10 cycle at 13:20:08-25, the phantom signature **changed** from `delta=3039.67` to `delta=3082.36` — but did not resolve. Same partial-TP-fill mechanic: TP fills 3000 of 3043 qty, leaves 42.69 dust, DCA#10 re-arms 3042.69, leaving 3042.69 - 0 (didn't take the old dust) → new phantom 3082.36 = 3042.69 + (40-ish from earlier residual).

### 🟢 Fix #5 sub-min cleanup verified live (2nd & 3rd occurrence)

Two BX-BUSDT sub-min batches dropped this hour at 12:31:17 and 13:02:37 (qty=3.93 < ~5.5 min). Same mechanism as iter-43's BB-JCT cleanup. **Three clean live demonstrations of Fix #5 working.**

### 🟢 BX-BUSDT chronic qtyExcess — quieting (1234 → 11 → 4 warnings over last 4 hours)

The chronic BingX issue is now near zero — recent TP fills cleared the standing phantoms, and Fix #5 is auto-purging the new sub-min residue. The pattern has shifted to Bybit instead.

### 🟢 BB-JCT phantom DCA#5 (iter-34 root) — **21 clean hours**

### 🟢 BB-M-Algida error spam — 21st clean hour

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): 1 TP on BB-JCT (+$0.292), 1 DCA, 511 qtyExcess warnings.
- **Bybit BB-SASH-ShortSMA** (3 bots): 3 TPs (+$0.279), 4 DCAs.
- **Bitget** (3 bots): **4 BG-BUSDT TPs (+$3.517)** 🏆 — best per-exchange hour of the run.
- **BingX** (2 bots): 5 BX-BUSDT TPs (+$1.701), 6 DCAs (2 auto-purged).

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 11 → 11 | 0 → 0 (TP + re-armed DCA) |
| BB-BANANAS (#4f) | 15 → 14 | 11 → 12 |
| BB-FF (#46) | 1 → 3 | 15 → 13 |
| BB-ZBT-SASH (#7e) | 6 → 5 | 10 → 11 |
| BG-BUSDT (#3f) | 11 → 9 | 3 → 5 |
| BX-BUSDT (#1c) | 10 → 10 | 6 → 6 (cycle + 2 Fix #5 deletions) |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$78.860** |
| Δ this iteration | **+$5.787** 🏆 (new single-hour record, 38% more than iter-49) |
| Δ from iter-34 baseline ($47.026) | **+$31.834** |

## Verdict for iteration 55

The single best hour of the run. The BUSDT rally from 0.34 to 0.40 fired 9 TPs across BG-BUSDT and BX-BUSDT for $5.22, plus tail-end fills on Bybit. iter-49 ($4.19) and iter-55 ($5.79) demonstrate the same pattern: **deep accumulation phase → recovery rally → multi-batch cascade**. BUSDT alone has produced $7.81 of the $31.83 cumulative gain since baseline. Fix #5 visibly handled 2 sub-min residues without operator intervention. The chronic BB-M-Algida qtyExcess noise continues but causes no operational damage. **Next cron fire ~14:17 UTC (16:17 Warsaw). 3 iterations remain (56, 57, then iter-58 final).**
