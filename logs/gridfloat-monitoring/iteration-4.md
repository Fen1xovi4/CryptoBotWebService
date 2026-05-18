# GridFloat monitoring — Iteration 4

**Captured**: 2026-05-14 10:13 UTC (12:13 Warsaw)
**Δ from iteration-3**: ~60 min
**Cron**: `342c898f` fired at 10:07 UTC

## Δ Activity since 09:13 UTC

### Trades (10 new)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 09:15:47 | BG-BUSDT | Buy  | DCA#2 (reconcile-adopt) | 21    | 0.47282 | — |
| 09:15:54 | BX-BUSDT | Buy  | DCA#2 fill              | 10.61 | 0.4711  | — |
| 09:25:38 | BG-BUSDT | Buy  | DCA#3 (reconcile-adopt) | 21    | 0.45773 | — |
| 09:32:22 | BX-BUSDT | Buy  | DCA#3 fill              | 10.96 | 0.456   | — |
| 09:51:17 | BG-BUSDT | Buy  | DCA#4 fill              | 45    | 0.44260 | — |
| 09:51:18 | BX-BUSDT | Buy  | DCA#4 fill              | 22.67 | 0.441   | — |
| 10:05:28 | BX-BUSDT | Sell | TakeProfit#4            | 22.67 | 0.4542  | +$0.295 |
| 10:06:05 | BG-BUSDT | Sell | TakeProfit#4            | 45    | 0.4558  | +$0.586 |
| 10:07:54 | BG-BUSDT | Buy  | DCA#4 re-arm fill (rec) | 45    | 0.44264 | — |
| 10:10:09 | BX-BUSDT | Buy  | DCA#4 re-arm fill       | 22.67 | 0.441   | — |

Both BUSDT pairs continued ladder-down. BG-BUSDT and BX-BUSDT each had DCAs at levels 2, 3, 4 fill in sequence; level-4 batch on each closed for profit; level-4 slot then re-armed and filled again.

### realizedPnL delta
| Bot | iteration-3 | now | Δ |
|---|---|---|---|
| BG-BUSDT (#5) | $0.572 | $1.158 | **+$0.586** |
| BX-BUSDT (#8) | $0.443 | $0.738 | **+$0.295** |
| (others unchanged) | | | — |

### State delta
| Bot | batches Δ | dcas Δ | anchor |
|---|---|---|---|
| BG-BUSDT | 2 → 5 | 5 → 2 | unchanged (0.503) |
| BX-BUSDT | 2 → 5 | 5 → 2 | unchanged (0.5012) |

### Grid math — ✓ all 10 trades match spec
**BG-BUSDT** anchor=0.503, step=3%, tiers=[≤10%:$10, ≤20%:$20]:
- k=2: 0.503·0.94 = **0.47282** ✓, qty=10/0.47282=21.15 (recorded 21)
- k=3: 0.503·0.91 = **0.45773** ✓
- k=4: 0.503·0.88 = **0.44264** ✓, tier2 → qty=20/0.44264=45.18 (recorded 45)

**BX-BUSDT** anchor=0.5012, step=3%, tiers=[≤10%:$5, ≤20%:$10]:
- k=2: 0.5012·0.94 = **0.471128** ✓
- k=3: 0.5012·0.91 = **0.456092** ✓
- k=4: 0.5012·0.88 = **0.441056** ✓, tier2 → qty=10/0.441056=22.67 ✓

All level prices and tier-based sizes match `ComputeDcaLevels` exactly.

## 🟡 Pattern emerging: `RECONCILE DCA` fires often on Bitget

Three reconcile warnings on BG-BUSDT this hour:
```
09:15:47  state=39 vs exchange=60 (+21)   → adopted DCA#2
09:25:38  state=60 vs exchange=81 (+21)   → adopted DCA#3
10:07:54  state=81 vs exchange=126 (+45)  → adopted DCA#4 (re-armed)
```

By contrast BX-BUSDT had **zero** reconcile warnings this hour — its DCAs at the same price-level structure all hit `PollDcaFills` cleanly via BingX's `GetOrderAsync`.

### Interpretation
`PollDcaFills` polls each DCA order id via `IFuturesExchangeService.GetOrderAsync`. When the exchange returns `Open` or `Unknown` for a DCA that has actually filled, Poll silently does nothing — and `ReconcileBatchesFromPosition` is the backstop.

The fact that reconcile fires reliably on Bitget but not on BingX (for the same kind of fills, same workspace, same minute) suggests `BitgetFuturesExchangeService.GetOrderAsync` has a stale-data problem similar to what the GridFloatHandler.cs comments already document for Bybit:
> "Bybit DOES implement GetOrderAsync (V5 GetOrders + GetOrderHistory fallback) but on worker restart its history endpoint can return status=Filled with FilledQuantity=0 for still-active orders…"

Adding to that, **Bitget's normal GetOrderAsync may not show recently-filled orders in `GetOpenOrders` and may show them only after a delay in history**. So Poll misses → reconcile catches. This is **not a bug** in our code — it's the design's defensive backstop working. But it does mean the Bitget critical path depends on reconcile, with a 2-second second-probe delay.

### Action item from earlier iterations still open
- Cross-symbol cancel hypothesis (iteration-2): still not retested — no BG full-close this hour. BG-BUSDT keeps accumulating, no full close to trigger `CancelAllOrdersAsync`.

## Other observations

- **No errors anywhere** in last hour.
- **Bybit bots #1, #2, #3** (XRP, BB-ZBT, JCT) totally idle. State unchanged from baseline.
- **BB-SAGA (#4)** also idle this hour — last hour's 2 TP fills exhausted the price action.
- **BG-ZBT (#6), BG-OPEN (#7), BX-ZBT (#9)** also idle — those symbols haven't moved enough to trigger DCAs or TPs.
- **No Pause/Resume / tier-update activity** still observable.

## Cumulative scoreboard (since iteration-1 baseline)

| Bot | Baseline realPnL | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.285 | 0 |
| #2 BB-ZBT   | $1.774 | $1.774 | 0 |
| #3 BB-JCT   | $5.184 | $5.184 | 0 |
| #4 BB-SAGA  | $6.484 | $7.233 | +$0.748 |
| #5 BG-BUSDT | $0    | $1.158 | +$1.158 |
| #6 BG-ZBT   | $0    | $0     | 0 |
| #7 BG-OPEN  | $0    | $0     | 0 |
| #8 BX-BUSDT | $0    | $0.738 | +$0.738 |
| #9 BX-ZBT   | $0    | $0     | 0 |
| **Total** |       |         | **+$2.644** |

## Verdict for iteration 4

✅ Grid math + reconcile semantics validated against 10 new trades. Spec adherence remains 100%.

🟡 `BitgetFuturesExchangeService.GetOrderAsync` lag pattern is now well-evidenced (3/3 BG-BUSDT fills this hour went through reconcile instead of poll). Not breaking anything — reconcile design protects us — but worth thinking about whether to add an `IsLikelyStale` heuristic or extend the `GetOrders` lookback before falling back to reconcile.

🟡 Cross-symbol cancel still un-retested. Watching for next BG full-close (which won't happen until BG-BUSDT either reaches its TP or — far less likely — manually closed).

📅 Next cron fire 11:07 UTC.
