# GridFloat monitoring — Iteration 52

**Captured**: 2026-05-16 10:29 UTC (12:29 Warsaw)
**Δ from iteration-51**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **38 trades** (13 TPs, 24 DCAs, 1 Entry), **+$2.888 realized** — 3rd big-payoff hour in 4 iterations.
- 🟢 **Best BB-M-Algida hour ever**: BB-JCT triple TP (TP#9 +$0.420, TP#8 +$0.275, TP#10 +$0.448) totaling **+$1.143** on a sharp JCT crash + immediate bounce.
- 🟢 **BX-BUSDT 4 TPs** captured a fast BUSDT wick: TP#11 +$0.419, TP#12 +$0.490, TP#10 ×2 +$0.187 each = **+$1.283**.
- 🟢 **BB-FF full close** at 10:14 (TP#0 +$0.095) → Entry at 10:15:18 (new anchor 0.09189).
- 🚨 BX-BUSDT noise continues — 212 warnings, including new qtyExcess=79.43 phantom that persisted ~6 min before resolving.
- ✅ 0 errors (18th clean hour); 0 phantom dupes (hour 18).

## Δ Activity since iter-51 — TPs and Entries only (DCAs summarized)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 09:37:00 | BB-FF (#46) | Sell | TP#1 | 111 | 0.09084 | +$0.095 |
| 09:52:22 | BB-ZBT-SASH (#7e) | Sell | TP#5 | 63 | 0.16013 | +$0.096 |
| 09:58:19 | BB-JCT (#3) | Sell | **TP#9** | 2800 | 0.00358 | **+$0.420** |
| 09:59:52 | BB-JCT (#3) | Sell | **TP#8** | 2600 | 0.00368 | **+$0.275** |
| 10:05:28 | BB-BANANAS (#4f) | Sell | TP#12 | 800 | 0.01125 | +$0.040 |
| 10:10:55 | BX-BUSDT (#1c) | Sell | **TP#11** | 58.34 | 0.35010 | **+$0.419** |
| 10:10:55 | BX-BUSDT (#1c) | Sell | **TP#12** | 21.08 | 0.35010 | **+$0.490** 🏆 |
| 10:11:22 | BX-BUSDT (#1c) | Sell | TP#10 | 55.73 | 0.35640 | +$0.187 |
| 10:14:06 | BB-FF (#46) | Sell | **TP#0** (full close) | 110 | 0.09176 | +$0.095 |
| 10:15:18 | BB-FF (#46) | Buy  | **Entry** | 108 | 0.09189 | — |
| 10:15:25 | BB-BANANAS (#4f) | Sell | TP#13 | 800 | 0.01119 | +$0.040 |
| 10:17:19 | BB-ZBT-SASH (#7e) | Sell | TP#5 | 63 | 0.16013 | +$0.096 |
| 10:17:42 | BB-JCT (#3) | Sell | **TP#10** | 2800 | 0.00345 | **+$0.448** 🏆 |
| 10:18:57 | BX-BUSDT (#1c) | Sell | TP#10 | 55.73 | 0.36090 | +$0.187 |

24 DCAs across all 3 exchanges captured the JCT (0.00343 low) and BUSDT (0.327 low) wicks. Notable cluster: BB-JCT DCA#6 → DCA#7 → DCA#8 → DCA#9 in 36 sec at 09:57-09:58 UTC.

### Realized PnL delta

| Bot | iter-51 | iter-52 | Δ |
|---|---|---|---|
| BB-JCT (#3) | 10.343 | 11.486 | **+$1.143** (3 TPs) 🏆 |
| BB-BANANAS (#4f) | 1.106 | 1.187 | +$0.081 |
| BB-FF (#46) | 1.802 | 1.992 | +$0.190 (2 TPs + new anchor) |
| BB-ZBT-SASH (#7e) | 3.251 | 3.442 | +$0.191 |
| BX-BUSDT (#1c) | 8.216 | 9.499 | **+$1.283** (4 TPs) 🏆 |
| **Σ Δ** | | | **+$2.888** |

### Log counts (since 09:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 4 | 22 |
| BB-SASH-ShortSMA | 0 | 0 | 44 |
| BG-SASH-Insider | 0 | 0 | 8 |
| BX-M-IJKL | 0 | **212** | 26 |

The 4 BB-M-Algida warnings were healthy `🔎 RECONCILE DCA` adoptions during the BB-JCT crash burst.

## 🚨 Issue tracker

### 🚨 BX-BUSDT qtyExcess noise — chronic; new pattern qtyExcess=79.43

Same shape as iter-50/51 but the orphan qty doubled (79.43 ≈ 2× normal DCA size). Persisted from 10:03 to 10:10 → resolved when DCA#10, DCA#11, DCA#12 placed in a 1-second cluster and reconcile finally adopted them. **Fix #6 remains the right action.**

### 🟢 BB-JCT phantom DCA#5 — 18 clean hours

BB-JCT had 5 distinct DCA fills this hour (#6, #7, #8, #9, #10), all single records, all different level indices. No dupes.

### 🟢 BB-M-Algida error spam — 18th clean hour (0 errors, 4 healthy warnings)

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): **best hour of the run** — 3 TPs on BB-JCT (+$1.143), 6 DCAs, 1 DCA on BB-XRP. Strategy correctly inventoried the JCT crash and immediately closed on the bounce.
- **Bybit BB-SASH-ShortSMA** (3 bots): 5 TPs (+$0.467), many DCAs, 1 BB-FF full cycle.
- **Bitget** (3 bots): **0 TPs**, 4 BG-BUSDT DCAs — accumulating for the next BUSDT rebound (recently #8/#9/#10/#11 placed at 0.390/0.374/0.369/0.353).
- **BingX** (2 bots): **4 BX-BUSDT TPs (+$1.283)** — captured the wick perfectly; ALL 4 closures happened in 8 minutes between 10:11 and 10:19.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 6 → **10** | 5 → 1 |
| BB-XRP (#e7) | 9 → 10 | 2 → 1 |
| BB-BANANAS (#4f) | 11 → 13 | 15 → 13 |
| BB-FF (#46) | 2 → 1 | 14 → 15 (full close + new anchor) |
| BB-ZBT-SASH (#7e) | 5 → 5 | 11 → 11 |
| BG-BUSDT (#3f) | 8 → 12 | 6 → 2 (4 deep DCAs added) |
| BX-BUSDT (#1c) | 9 → 11 | 1 → 5 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$68.838** |
| Δ this iteration | **+$2.888** (3rd-best hour) |
| Δ from iter-34 baseline | **+$21.812** |

## Verdict for iteration 52

Volatility paid off across two symbols simultaneously: BB-JCT captured a 14% drawdown → 7% bounce in 20 minutes for $1.14, and BX-BUSDT did the same for BUSDT booking $1.28. The grid-float thesis — *hold inventory through drawdowns, close on partial recoveries* — is demonstrably working: this iteration's wins came from batches placed at -10% to -25% from anchor that closed within 1-3% of their fill price thanks to a fast V-shape. Bitget loaded up BG-BUSDT inventory (now 12 batches deep) — primed for the next rally similar to iter-49/51. **Next cron fire ~11:17 UTC (13:17 Warsaw). 6 iterations remain.**
