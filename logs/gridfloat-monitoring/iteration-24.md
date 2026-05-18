# GridFloat monitoring — Iteration 24

**Captured**: 2026-05-15 06:13 UTC (08:13 Warsaw)
**Δ from iteration-23**: ~60 min
**Cron**: `342c898f` fired at 06:07 UTC

## TL;DR

- **8 trades** (7 DCA fills + 1 TP), **+$0.478 realized**.
- 🎯 **BB-SAGA hit k=12 — the deepest level its static bound allows.**
- 1 reconcile-DCA warning (normal), 0 errors.

## Δ Activity since 05:13 UTC

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 05:31:12 | BB-XRP (#1)   | Buy  | DCA#5 fill       | 6.80   | 1.4609   | — |
| 05:32:27 | BB-SAGA (#4)  | Buy  | DCA#10 fill      | 387.8  | 0.02578  | — |
| 05:34:26 | BG-ZBT (#6)   | Buy  | DCA#2 fill       | 68     | 0.14683  | — |
| 05:58:29 | BB-JCT (#3)   | Buy  | DCA#1 fill       | 2100   | 0.004554 | — |
| 06:01:33 | BX-BUSDT (#8) | Buy  | DCA#4 fill       | 21.21  | 0.4713   | — |
| 06:04:42 | BG-BUSDT (#5) | Buy  | DCA#4 fill       | 43     | 0.464    | — |
| **06:09:09** | **BB-SAGA (#4)** | **Buy** | **DCA#12 adopt (DEEPEST k!)** | 405.2 | 0.023571 | — |
| 06:09:22 | BB-SAGA (#4)  | Sell | TakeProfit#12    | 405.2  | 0.02476  | **+$0.478** |

### BB-SAGA reaches the static bound

Static range was set at 0.022648 (= 0.02831·0.80 from the very first anchor of this bot session back in iter-1). With anchor 0.03683 and step 3%:
- k=12: 0.03683·(1−0.36) = **0.023571** → just barely above bound
- k=13: 0.03683·(1−0.39) = 0.022467 → below bound, blocked by `if (price < state.StaticLowerBound) break`

So k=12 is the **maximum-depth slot** for this static bound. Reaching it means price dipped 36% below anchor.

Within 13 seconds of DCA#12 being adopted via reconcile, price bounced and TP#12 fired at 0.02476 ≈ TpPrice 0.023571·1.03 = 0.024278 (slight upside slippage to 0.02476 — **+$0.478 PnL**, biggest single trade of the session).

### realizedPnL delta
| Bot | iter-23 | now | Δ |
|---|---|---|---|
| BB-SAGA (#4) | $10.204 | $10.682 | **+$0.478** |
| **Δ this hour** |     |        | **+$0.478** |

### State delta
| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-XRP (#1)   | 5 → 6 | 6 → 5 |
| BB-JCT (#3)   | 1 → 2 | 10 → 9 |
| BB-SAGA (#4)  | 10 → 11 | 2 → 0 (all levels traversed; k=12 cycled) |
| BG-BUSDT (#5) | 4 → 5 | 10 → 9 |
| BG-ZBT (#6)   | 2 → 3 | 5 → 4 |
| BX-BUSDT (#8) | 4 → 5 | 7 → 6 |

### Observation: BB-SAGA's 0 DCAs in state

Curious snapshot: BB-SAGA has 11 batches but 0 pending DCAs. With max slot k=12 and 11 currently held as batches, slot 12 just emptied (its TP fired) and should be re-armed by `HealMissingDcas` on the next tick. The 4-minute gap between TP#12 (06:09:22) and capture (06:13) is enough that re-arm should have happened — possibly the placement cooldown is suppressing it, or HealMissingDcas declined because `ComputeDcaLevels` returned an entry that overlaps with an existing batch's LevelIdx (slots 0..11 are batches). Worth a peek next iteration.

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $1.043 | +$0.758 |
| #2 BB-ZBT   | $1.774 | $5.659 | +$3.885 |
| #3 BB-JCT   | $5.184 | $7.511 | +$2.327 |
| #4 BB-SAGA  | $6.484 | $10.682 | **+$4.198** |
| #5 BG-BUSDT | $0     | $9.549 | **+$9.549** |
| #6 BG-ZBT   | $0     | $1.468 | +$1.468 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $4.578 | +$4.578 |
| #9 BX-ZBT   | $0     | $0.739 | +$0.739 |
| **Total Δ from baseline** |  |  | **+$27.502** |

## Verdict for iteration 24

✅ Static-range mechanic battle-tested at its maximum depth — DCA#12 adoption + TP fire within 13 seconds = +$0.478, biggest single trade of the session.

🟡 BB-SAGA's missing slot-12 DCA re-arm worth re-checking next iteration.

📅 Next cron fire 07:07 UTC.
