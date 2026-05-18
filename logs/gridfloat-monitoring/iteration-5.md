# GridFloat monitoring — Iteration 5

**Captured**: 2026-05-14 11:13 UTC (13:13 Warsaw)
**Δ from iteration-4**: ~60 min
**Cron**: `342c898f` fired at 11:07 UTC

## Δ Activity since 10:13 UTC

### Trades (7 new — first wake-up of BB-JCT this session)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 10:14:38 | BB-JCT (#3) | Buy  | DCA#2 fill        | 2300  | 0.0042652 | — |
| 10:25:38 | BB-JCT (#3) | Buy  | DCA#3 (reconcile) | 2400  | 0.0041291 | — |
| 10:29:51 | BB-JCT (#3) | Buy  | DCA#4 fill        | 2500  | 0.003993  | — |
| 10:32:13 | BB-JCT (#3) | Sell | TakeProfit#4      | 2500  | 0.0041127 | +$0.295 |
| 10:57:08 | BB-JCT (#3) | Buy  | DCA#4 re-arm fill | 2500  | 0.003993  | — |
| 11:01:29 | BX-BUSDT    | Buy  | DCA#5 fill        | 23.47 | 0.426     | — |
| 11:01:32 | BG-BUSDT    | Buy  | DCA#5 (reconcile) | 46    | 0.42755   | — |

BB-JCT was a sleeper through iterations 1-4; price finally dropped enough to wake the DCA ladder.

### realizedPnL delta
| Bot | iter-4 | now | Δ |
|---|---|---|---|
| BB-JCT (#3)  | $5.184 | $5.479 | **+$0.295** |
| (BG-BUSDT, BX-BUSDT: DCAs filled but no new TPs, PnL unchanged this hour) |  |  | — |

### State delta
| Bot | batches Δ | dcas Δ |
|---|---|---|
| BB-JCT (#3)  | 2 → 5 | 5 → 2 |
| BG-BUSDT (#5) | 5 → 6 | 2 → 1 |
| BX-BUSDT (#8) | 5 → 6 | 2 → 1 |

### Grid math — ✓ all 7 fills match spec

**BB-JCT** anchor=0.0045375, step=3%, legacy tier $10 (NormalizeTiers from baseSize/range):
- k=2: **0.00426525** = 0.0045375·0.94 ✓
- k=3: **0.00412913** = 0.0045375·0.91 ✓
- k=4: **0.003993**   = 0.0045375·0.88 ✓
- qty rounding to lot-size 100: 10/0.00426525 = 2344.5 → **2300** (Bybit lot floor); 2421.8 → **2400**; 2504.4 → **2500**. ✓

**BG-BUSDT** k=5 = 0.503·0.85 = **0.42755** ✓, tier2 $20, qty=20/0.42755=46.78 → recorded 46 (Bitget integer rounding) ✓

**BX-BUSDT** k=5 = 0.5012·0.85 = **0.42602** ✓ (recorded 0.426), tier2 $10, qty=10/0.426=23.47 ✓

All level prices, tier-based sizes, and per-exchange lot rounding hit spec.

## Reconcile pattern still trending

Two new reconcile warnings this hour:
- **BB-JCT** 10:25:38: state=6700 vs exchange=9100 (+2400 = DCA#3). Adopted.
- **BG-BUSDT** 11:01:32: state=126 vs exchange=172 (+46 = DCA#5). Adopted.

BB-JCT used `Poll` successfully for DCA#2 (10:14:38) and DCA#4 (10:29:51) — but missed DCA#3, requiring reconcile. So Bybit's poll/reconcile mix is **partially flaky**, not consistently broken. BingX poll on BX-BUSDT continues to land cleanly.

## Cross-symbol Bitget cancel — still not re-tested

BG-BUSDT continues to accumulate batches without a full close (now 6 batches, 1 remaining DCA). The trigger condition (`OnFullClose → CancelAllOrdersAsync`) has not fired since iteration-2's incident.

Worth watching: **BG-BUSDT static range bound** = 0.503·0.80 = **0.4024**. With current price near 0.42755 (DCA#5 just filled), the next DCA #6 at 0.503·0.82=**0.41246** is still inside the bound. After that, k=7 = 0.503·0.79=0.39737, which is BELOW 0.4024 → no DCA#7 placed (static bound stops the ladder). So this bot has at most one more DCA fill before it's frozen waiting for either price recovery (TP fills) or a manual close.

If BG-BUSDT does eventually do a full close after a price recovery, that's when the cross-symbol-cancel pattern from iteration-2 may re-appear.

## Other observations

- **Zero errors anywhere** this hour.
- **BB-XRP (#1), BB-ZBT (#2), BB-SAGA (#4), BG-ZBT (#6), BG-OPEN (#7), BX-ZBT (#9)** all idle — no trades, no warnings. State unchanged.
- **No Pause/Resume / tier-update** activity.

## Cumulative scoreboard (since iteration-1 baseline)

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.285 | 0 |
| #2 BB-ZBT   | $1.774 | $1.774 | 0 |
| #3 BB-JCT   | $5.184 | $5.479 | **+$0.295** |
| #4 BB-SAGA  | $6.484 | $7.233 | +$0.748 |
| #5 BG-BUSDT | $0     | $1.158 | +$1.158 |
| #6 BG-ZBT   | $0     | $0     | 0 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $0.738 | +$0.738 |
| #9 BX-ZBT   | $0     | $0     | 0 |
| **Total Δ from baseline** |  |  | **+$2.939** |

## Verdict for iteration 5

✅ BB-JCT first wake-up validated full DCA→TP→DCA cycle on Bybit legacy single-tier config (with lot-size rounding to 100). Spec adherence holds.

🟡 Reconcile-vs-poll mix on Bybit is partially flaky (3 of 5 BB DCAs caught by Poll, 1 needed reconcile). On Bitget poll appears to consistently lag → reconcile carries the load. Working as designed.

📅 Next cron fire 12:07 UTC.
