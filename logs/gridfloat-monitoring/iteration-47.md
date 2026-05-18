# GridFloat monitoring — Iteration 47

**Captured**: 2026-05-16 05:29 UTC (07:29 Warsaw)
**Δ from iteration-46**: ~60 min
**Cron**: `17 * * * *` (Warsaw)

## TL;DR

- **23 trades** (12 TPs, 6 DCAs, 5 Entries), **+$1.878 realized** — biggest single-hour PnL of the run.
- 🎯 **Halfway point** — 13 iterations done out of 24. Cumulative now **$55.817** vs $47.026 actual baseline (**+$8.791**).
- 🟢 **BB-M-Algida woke up** after 2 sleepy hours: 3 TPs across BB-JCT/BB-ZBT/BB-XRP (+$0.870). Cleanest BB-M-Algida hour in the run.
- 🟢 **BB-ZBT-SASH cycled 3 times** in 21 min (04:39 → 04:49 → 04:57 → 05:00 Entry) — fastest grid-cycle frequency observed.
- 🚨 BX-BUSDT margin loop **migrated again** — now on slot #5 (11 cooldown warnings this hour). Slot #4 cleared.

## Δ Activity since iter-46

### Trades (23) — TPs and Entries marked, DCAs omitted from the table for brevity

| Time UTC | Bot | Side | Status | PnL |
|---|---|---|---|---|
| 04:31:54 | BB-FF (#46) | Sell | TP#4 | +$0.096 |
| 04:32:13 | BB-BANANAS (#4f) | Sell | TP#5 | +$0.043 |
| 04:35:17 | BB-ZBT (#67) | Sell | TP#3 | **+$0.296** |
| 04:37:46 | BB-XRP (#e7) | Buy  | DCA#8 (reconcile) | — |
| 04:39:01 | BB-ZBT-SASH (#7e) | Sell | TP#0 | +$0.096 |
| 04:40:10 | BB-ZBT-SASH (#7e) | Buy  | **Entry** | — |
| 04:49:15 | BB-ZBT-SASH (#7e) | Sell | TP#0 | +$0.096 |
| 04:49:22 | BG-ZBT (#9d) | Sell | TP#0 | **+$0.295** |
| 04:49:50 | BB-ZBT (#67) | Sell | TP#1 | **+$0.281** |
| 04:50:02 | BB-ZBT (#67) | Buy  | DCA#1 | — |
| 04:50:19 | BB-ZBT-SASH (#7e) | Buy  | **Entry** | — |
| 04:50:20 | BG-ZBT (#9d) | Buy  | **Entry** | — |
| 04:52:53 | BB-BANANAS (#4f) | Sell | TP#4 | +$0.043 |
| 04:56:31 | BB-JCT (#3) | Sell | TP#4 | **+$0.293** |
| 04:57:51 | BX-ZBT (#0a) | Sell | TP#0 | **+$0.148** |
| 04:57:52 | BB-ZBT-SASH (#7e) | Sell | TP#0 | +$0.096 |
| 05:00:01 | BB-ZBT-SASH (#7e) | Buy  | **Entry** | — |
| 05:00:03 | BX-ZBT (#0a) | Buy  | **Entry** | — |
| 05:02:01 | BB-BANANAS (#4f) | Buy  | DCA#4 | — |
| 05:13:14 | BB-ZBT-SASH (#7e) | Buy  | DCA#1 | — |
| 05:24:54 | BB-ZBT-SASH (#7e) | Buy  | DCA#2 (reconcile) | — |
| 05:28:06 | BB-ZBT-SASH (#7e) | Sell | TP#2 | +$0.095 |
| 05:29:17 | BB-FF (#46) | Buy  | DCA#4 | — |

### Realized PnL delta

| Bot | iter-46 | iter-47 | Δ |
|---|---|---|---|
| BB-JCT (#3) | 10.049 | 10.343 | +$0.294 |
| BB-ZBT (#67) | 6.818 | 7.395 | **+$0.577** (2 TPs) |
| BB-BANANAS (#4f) | 0.682 | 0.768 | +$0.086 |
| BB-FF (#46) | 0.663 | 0.759 | +$0.096 |
| BB-ZBT-SASH (#7e) | 1.626 | 2.008 | **+$0.383** (4 TPs) |
| BG-ZBT (#9d) | 3.236 | 3.531 | +$0.295 |
| BX-ZBT (#0a) | 1.330 | 1.478 | +$0.148 |
| **Σ Δ** | | | **+$1.878** |

### Log counts (since 04:29 UTC)

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida | 0 | 1 | 10 |
| BB-SASH-ShortSMA | 0 | 2 | 82 |
| BG-SASH-Insider | 0 | 0 | 13 |
| BX-M-IJKL | 0 | **12** | 12 |

All BB warnings were healthy reconcile-DCA/TP adoption notices. The 12 BX-M-IJKL warnings are the **migrating margin issue** — first 2 still on slot #4 (04:30-04:35), then slot #5 took over (every ~5 min from 04:40 to 05:26).

## 🚨 Issue tracker

### 🚨 BX-BUSDT margin-cooldown — now stuck on slot #5

Two-slot migration this run: **#7 (iter-41/42) → cleared in iter-46 → #4 (iter-46/47 brief) → cleared → #5 (now)**. The rotation pattern suggests the BingX account has enough margin for ~10 of the 11 grid slots but always 1 short — whichever slot gets re-armed first after a TP claims the available margin, the next-attempted slot trips the cooldown.

This is **not blocking PnL** (BX-BUSDT still earned +$0 this hour through the holding batches — actually $0.382 since iter-41), but it's a persistent log-noise generator. Real fix: top up the BingX account or reduce one tier size.

### 🟢 BB-JCT phantom DCA#5 — 13 clean hours, no recurrence

### 🟢 BB-M-Algida error spam — 13th clean hour

## Cross-exchange health

- **Bybit BB-M-Algida** (4 bots): 3 TPs (+$0.870) on BB-ZBT/BB-JCT, 1 DCA on BB-XRP. **Re-awoke** after 2 silent hours.
- **Bybit BB-SASH-ShortSMA** (3 bots): 6 TPs (+$0.477), 3 Entries, 3 DCAs. BB-ZBT-SASH cycled 3× in 21 min.
- **Bitget** (3 bots): 1 TP (+$0.295) on BG-ZBT; full cycle close + re-anchor.
- **BingX** (2 bots): 1 TP on BX-ZBT (+$0.148) + cycle, margin loop ongoing.

## State delta

| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3) | 5 → 4 | 6 → 7 |
| BB-ZBT (#67) | 4 → 3 | 7 → 8 |
| BB-XRP (#e7) | 8 → 9 | 3 → 2 |
| BB-BANANAS (#4f) | 6 → 5 | 20 → 21 |
| BB-ZBT-SASH (#7e) | 1 → 2 | 15 → 14 (after multi-cycle, ended mid-grid) |
| BX-ZBT (#0a) | 1 → 1 | 3 → 4 (re-anchored, fresh ladder) |

## Cumulative scoreboard

| | Value |
|---|---|
| Sum of state-side realized | **$55.817** |
| Δ this iteration | **+$1.878** (record) |
| Δ from iter-34 baseline | **+$8.791** |

## Verdict for iteration 47

Best PnL hour of the run by a wide margin — $1.88 across **all 3 exchanges**, with BB-M-Algida back in action driving over a third of it. The market drifted up enough to trigger a wave of TPs across batches placed deeper in the grid (BB-ZBT @ 0.166, BB-JCT @ 0.00425). Strategy showing strong recovery behavior after the iter-37 downswing. The BX-BUSDT margin issue remains an annoying log-noise generator but isn't blocking anything. **Next cron fire ~06:17 UTC (08:17 Warsaw).**
