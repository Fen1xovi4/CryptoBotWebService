# GridFloat monitoring — Iteration 51

**Captured**: 2026-05-16 09:29 UTC (11:29 Warsaw)
**Δ from iteration-50**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **28 trades** (15 TPs, 12 DCAs, 1 Entry), **+$3.866 realized** — 2nd-best hour of the run (after iter-49's $4.19).
- 🟢 **2 BG-BUSDT TPs (+$1.75) + 2 BX-BUSDT TPs (+$0.87)** — BUSDT recovery rally fired again on top of iter-50's accumulation. **Cross-exchange synergy** at full effect.
- 🟢 **iter-50's "unrecoverable" qtyExcess=38.36 self-resolved** — adopted on the 08:28:14 DCA placement, then TP#9 at 09:15:27 closed the batch for +$0.429.
- 🟢 **BB-FF full grid close** at 09:10 — TP#0 cleared the anchor, then Entry at 09:10:06 (new anchor 0.09086) → fresh ladder.
- 🚨 BX-BUSDT noise **escalated to 325 warnings** — another phantom qtyExcess=39.44 appeared mid-hour, repeated 2×/tick for ~3 min until next placement adopted it.
- ✅ 0 errors (17th clean hour); 0 phantom dupes (hour 17).

## Δ Activity since iter-50

### Trades (28) — TPs and Entries shown

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 08:30:04 | BB-BANANAS (#4f) | Sell | TP#7  | 800 | 0.01155 | +$0.042 |
| 08:32:43 | BG-ZBT (#9d) | Sell | TP#2  | 64.0 | 0.16072 | +$0.296 |
| 08:32:45 | BB-ZBT-SASH (#7e) | Sell | TP#5  | 63.0 | 0.16013 | +$0.096 |
| 08:36:24 | BB-ZBT-SASH (#7e) | Sell | TP#5  | 63.0 | 0.16013 | +$0.096 (re-fill) |
| 08:53:06 | BB-ZBT-SASH (#7e) | Sell | TP#6  | 63.7 | 0.15844 | +$0.095 |
| 08:57:42 | BB-FF (#46) | Sell | TP#2  | 113  | 0.08866 | +$0.094 |
| 08:58:13 | BB-ZBT-SASH (#7e) | Sell | TP#5  | 63.0 | 0.16014 | +$0.096 |
| 09:02:32 | BX-ZBT (#0a) | Sell | TP#2  | 31.88 | 0.16152 | +$0.148 |
| 09:03:30 | BB-FF (#46) | Sell | TP#1  | 112  | 0.08957 | +$0.095 |
| 09:06:42 | BB-FF (#46) | Sell | **TP#0** (full close) | 111 | 0.09048 | +$0.095 |
| 09:10:06 | BB-FF (#46) | Buy  | **Entry** (new anchor) | 110 | 0.09086 | — |
| 09:13:10 | BB-ZBT-SASH (#7e) | Sell | TP#4  | 62.4 | 0.16182 | +$0.096 |
| 09:15:27 | BX-BUSDT (#1c) | Sell | **TP#9** (cleared iter-50 orphan!) | 38.36 | 0.39160 | **+$0.429** |
| 09:17:18 | BX-BUSDT (#1c) | Sell | TP#9 (fresh fill) | 39.44 | 0.39160 | **+$0.441** |
| 09:17:31 | BG-BUSDT (#3f) | Sell | **TP#9** | 77 | 0.39640 | **+$0.874** 🏆 |
| 09:25:27 | BG-BUSDT (#3f) | Sell | **TP#8** | 76 | 0.40190 | **+$0.877** 🏆 |

### Realized PnL delta

| Bot | iter-50 | iter-51 | Δ |
|---|---|---|---|
| BB-BANANAS (#4f) | 1.064 | 1.106 | +$0.042 |
| BB-FF (#46) | 1.519 | 1.802 | +$0.283 (3 TPs) |
| BB-ZBT-SASH (#7e) | 2.773 | 3.251 | +$0.478 (5 TPs) |
| BG-BUSDT (#3f) | 11.888 | 13.638 | **+$1.750** (2 TPs) 🏆 |
| BG-ZBT (#9d) | 3.826 | 4.122 | +$0.296 |
| BX-BUSDT (#1c) | 7.347 | 8.216 | **+$0.869** (2 TPs) |
| BX-ZBT (#0a) | 1.478 | 1.626 | +$0.148 |
| **Σ Δ** | | | **+$3.866** |

### Log counts (since 08:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 0 | 2 |
| BB-SASH-ShortSMA | 0 | 1 | 56 |
| BG-SASH-Insider | 0 | 0 | 8 |
| BX-M-IJKL | 0 | **325** 🚨 | 8 |

## 🚨 Issue tracker

### 🚨 BX-BUSDT qtyExcess noise — escalating (185 → 325 warnings/hour)

Pattern repeats: a partial/odd-qty DCA fills exchange-side, state.DcaOrders is empty → reconcile-DCA logs "qtyExcess=X, нет больше DCA-уровней для адаптации" every tick until a fresh placement finally matches. This iteration showed it can resolve cleanly (the 38.36 orphan from iter-50 became TP#9 at 09:15 for +$0.429), but during the unresolved window the log throughput is ~5.4 warnings/minute. **Fix #6 candidate confirmed** — either fabricate a DcaOrder entry from `ComputeDcaLevels` to match the excess, or rate-limit the warning to 1× per change in qtyExcess value.

### 🟢 BB-JCT phantom DCA#5 — 17 clean hours

### 🟢 BB-M-Algida error spam — 17th clean hour

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): 1 DCA (BB-SAGA #13), no TPs. Mostly idle but BB-SAGA accumulating deeper.
- **Bybit BB-SASH-ShortSMA** (3 bots): 11 TPs (+$1.190), 6 DCAs, 1 full grid close + re-anchor on BB-FF.
- **Bitget** (3 bots): **3 TPs (+$2.046)** — biggest Bitget hour of the run, beats iter-49's $1.76.
- **BingX** (2 bots): 2 BX-BUSDT TPs (+$0.869) + 1 BX-ZBT TP (+$0.148), 1 DCA. Best BingX hour after iter-48.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-SAGA (#c6) | 13 → 14 | 9 → 8 |
| BB-BANANAS (#4f) | 8 → 11 | 18 → 15 |
| BB-FF (#46) | 3 → 2 | 13 → 14 (cycled: full close + new anchor + DCA#1) |
| BB-ZBT-SASH (#7e) | 6 → 5 | 10 → 11 |
| BG-BUSDT (#3f) | 10 → 8 | 4 → 6 |
| BG-OPEN (#b3) | 3 → 4 | 4 → 3 |
| BG-ZBT (#9d) | 3 → 2 | 4 → 5 |
| BX-BUSDT (#1c) | 10 → 9 | 0 → 1 |
| BX-ZBT (#0a) | 3 → 2 | 4 → 5 |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$65.950** |
| Δ this iteration | **+$3.866** (2nd-best hour) |
| Δ from iter-34 baseline | **+$18.924** |

## Verdict for iteration 51

Cross-exchange BUSDT recovery delivered again — Bitget bagged $1.75 (TP#9 + TP#8), BingX $0.87 (two consecutive TP#9 fills via reconcile-adopted batches). Combined with the BB-ZBT-SASH and BB-FF micro-cycles on Bybit, this is the second consecutive "post-accumulation rally" payoff hour: iter-49 booked $4.19, iter-51 books $3.87. The qtyExcess noise on BX-BUSDT is a real-but-cosmetic issue — when it resolves, the orphan qty becomes a clean TP fill (proved this iteration). **Next cron fire ~10:17 UTC (12:17 Warsaw).**
