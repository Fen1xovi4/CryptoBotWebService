# GridFloat monitoring — Iteration 10

**Captured**: 2026-05-14 16:13 UTC (18:13 Warsaw)
**Δ from iteration-9**: ~60 min
**Cron**: `342c898f` fired at 16:07 UTC

## TL;DR

- **17 new trades**, **+$2.008 realized this hour**.
- 🟢 **Fix #2 verified across 3 BG full-closes total** — BG-ZBT did TWO full-closes this hour (15:27:20 and 16:09:25); cross-symbol cancellation events: **zero**.
- 1 reconcile-DCA warning total (normal); zero false-positive reconcile-TP warnings this hour.

## Δ Activity since 15:13 UTC

### Highlights

**BG-ZBT (#6) — two full cycles back-to-back on Bitget**:
```
15:27:20 TP#0  +$0.292 → FULL CLOSE
15:30:06 Entry @ 0.15133 (new cycle 1)
15:34:36 TP#1  +$0.287  (sibling — BG-BUSDT, not BG-ZBT)
...
16:09:25 TP#0  +$0.295 → FULL CLOSE (again)
16:10:12 Entry @ 0.15621 (new cycle 2)
```

**BB-ZBT (#2) — biggest single-bot harvest**:
```
15:39:47 TP#1 +$0.258
15:39:59 DCA#1 re-fill
15:43:47 TP#4 +$0.285
15:43:48 TP#5 +$0.295   (back-to-back TPs at same price ≈0.1529)
15:43:59 DCA#4 re-fill
```
Total +$0.838.

**BX-ZBT (#9) full cycle**: TP#0 at 15:43:50 (+$0.148) → Entry @ 0.15315 at 15:45:03.

**BX-BUSDT (#8) one micro-cycle**: DCA#2 fill at 15:17, TP#2 at 15:21 (+$0.148).

**BB-SAGA (#4)** added 2 DCAs (#7, #8) — no TPs this hour (price reversed up before reaching DCA#4's TP).

### realizedPnL delta
| Bot | iter-9 | now | Δ |
|---|---|---|---|
| BB-ZBT (#2)   | $3.966 | $4.804 | **+$0.838** |
| BG-BUSDT (#5) | $7.511 | $7.798 | +$0.287 |
| BG-ZBT (#6)   | $0.586 | $1.173 | +$0.587 (2 cycles) |
| BX-BUSDT (#8) | $3.093 | $3.241 | +$0.148 |
| BX-ZBT (#9)   | $0.296 | $0.444 | +$0.148 |
| **Δ this hour** |     |        | **+$2.008** |

## 🟢 Fix #2 — three-for-three across BG full-closes

Cumulative cross-symbol-cancel test results since deploy at 13:24:30:

| Time UTC | Bot | Event | Cross-symbol cancels observed | Expected pre-fix |
|---|---|---|---|---|
| iter-8 | 14:02:09 | BG-ZBT TP#0 full close | **0** | 12-13 |
| iter-10 | 15:27:20 | BG-ZBT TP#0 full close | **0** | 12-13 |
| iter-10 | 16:09:25 | BG-ZBT TP#0 full close | **0** | 12-13 |

Three consecutive Bitget full-closes, zero cross-symbol warnings. Fix is solid.

The single reconcile-DCA warning this hour (16:00:44, BG-BUSDT) is unrelated — it caught a normal DCA fill that PollDcaFills missed, then adopted it cleanly.

## Grid math — ✓ spot-check on new BG-ZBT anchor 0.15621

Tiers=[≤10%:$10, ≤20%:$20], static, isLong:
- k=1: 0.15621·0.97 = **0.151524**
- k=2: 0.15621·0.94 = **0.146837**
- k=3: 0.15621·0.91 = **0.142151**
- k=4: 0.15621·0.88 = **0.137465** (tier2 — $20 size, qty = 20/0.137465 = 145.49)
- k=5: 0.15621·0.85 = **0.132779**
- k=6: 0.15621·0.82 = **0.128092**

Static bound was set at 0.13958·0.80 = 0.111664 (from very first anchor at 0.13958 in iter-1 baseline). Current anchor 0.15621 + bound 0.111664 = max k where 0.15621·(1-k·0.03) ≥ 0.111664: solve k ≤ 9.99 → up to k=9. So static range allows up to k=9, but tier list only goes to 20% (k=6 max). Tier wins → 6 slots placed. State shows 6 DCAs. ✓

## State delta

| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BB-XRP (#1)   | 1 → 1 | 10 → 10 | unchanged |
| BB-ZBT (#2)   | 6 → 5 | 5 → 6  | unchanged (TP3 fired, slot re-armed) |
| BB-SAGA (#4)  | 7 → 9 | 6 → 4  | unchanged (DCA#7, DCA#8 adopted) |
| BG-BUSDT (#5) | 2 → 2 | 12 → 12 | unchanged |
| BG-ZBT (#6)   | 1 → 1 | 6 → 6 | 0.14714 → **0.15621** (2 new anchors) |
| BX-BUSDT (#8) | 2 → 2 | 9 → 9 | unchanged |
| BX-ZBT (#9)   | 1 → 1 | 6 → 6 | 0.14842 → **0.15315** (new anchor) |

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.474 | +$0.189 |
| #2 BB-ZBT   | $1.774 | $4.804 | **+$3.030** |
| #3 BB-JCT   | $5.184 | $6.345 | +$1.161 |
| #4 BB-SAGA  | $6.484 | $9.212 | **+$2.728** |
| #5 BG-BUSDT | $0     | $7.798 | **+$7.798** |
| #6 BG-ZBT   | $0     | $1.173 | +$1.173 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $3.241 | **+$3.241** |
| #9 BX-ZBT   | $0     | $0.444 | +$0.444 |
| **Total Δ from baseline** |  |  | **+$19.764** |

## Verdict for iteration 10

✅ **Fix #2 cumulative evidence: 3/3 BG full-closes, 0/3 cross-symbol blast events.** The diagnostic and remediation in iter-6 are confirmed correct.

✅ Grid math verified on the new BG-ZBT anchor (0.15621) — 6 DCA slots match `min(maxTierPct/Step, (anchor-bound)/anchor/Step)` = min(6, 9) = 6.

🟡 Fix #1 (Stop+Start state preserve) still not exercised — all 9 bots have been continuously Running since deploy. The cron loop has 113 iterations to go before the 5-day window closes; will probably see at least one user-triggered Stop+Start in that span.

🟡 Reconcile-TP false-positive (Fix #3 candidate) didn't recur this hour — situational, depends on volatility + poll-vs-fill timing.

📅 Next cron fire 17:07 UTC.
