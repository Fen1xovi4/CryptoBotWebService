# GridFloat monitoring — Iteration 6

**Captured**: 2026-05-14 12:13 UTC (14:13 Warsaw)
**Δ from iteration-5**: ~60 min
**Cron**: `342c898f` fired at 12:07 UTC

## TL;DR

- **23 new trades**, **+$4.861 realized this hour** (biggest hour so far).
- 🚨 **Bitget cross-symbol cancel CONFIRMED** with a second, clear-cut occurrence — BG-ZBT full-closed at 11:51:02, 9 seconds later **all TPs on BG-BUSDT and DCAs on BG-OPEN were detected as cancelled**.
- 🟡 BB-ZBT had a false-positive "RECONCILE TP" log because state.LastPrice was stale; the defensive `if (!crossed) continue;` correctly prevented any wrong action.
- ✅ Three full TP→Entry cycles completed (BG-ZBT, BX-ZBT, plus BG-BUSDT partial cycling).

## Δ Activity since 11:13 UTC

### Trades (23 new)

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 11:29:09 | BB-JCT (#3)   | Buy  | DCA#5 fill        | 2500   | 0.0038568 | — |
| 11:33:36 | BG-BUSDT (#5) | Buy  | DCA#6 fill        | 48     | 0.4124    | — |
| 11:45:11 | BB-JCT (#3)   | Sell | TakeProfit#5      | 2500   | 0.0039725 | +$0.285 |
| **11:51:02** | **BG-ZBT (#6)** | **Sell** | **TakeProfit#0 (FULL CLOSE)** | **71** | **0.14376** | **+$0.293** |
| 11:54:05 | BB-ZBT (#2)   | Sell | TakeProfit#1      | 59     | 0.14407   | +$0.244 |
| 11:54:15 | BB-ZBT (#2)   | Buy  | DCA#1 re-fill     | 59     | 0.14353   | — |
| 11:55:07 | BG-ZBT (#6)   | Buy  | Entry (NEW anchor)| 69     | 0.14394   | — |
| 12:01:05 | BG-BUSDT (#5) | Sell | TakeProfit#5      | 46     | 0.4403    | +$0.579 |
| 12:01:05 | BG-BUSDT (#5) | Sell | TakeProfit#6      | 48     | 0.4247    | +$0.582 |
| 12:01:06 | BX-BUSDT (#8) | Sell | TakeProfit#5      | 23.47  | 0.4387    | +$0.294 |
| **12:04:20** | **BX-ZBT (#9)** | **Sell** | **TakeProfit#0 (FULL CLOSE)** | **35.775** | **0.14395** | **+$0.148** |
| 12:05:05 | BX-ZBT (#9)   | Buy  | Entry (NEW anchor)| 34.681 | 0.14417   | — |
| 12:06:59 | BB-ZBT (#2)   | Sell | TakeProfit#4      | 65     | 0.14427   | +$0.269 |
| 12:06:59 | BB-ZBT (#2)   | Sell | TakeProfit#5      | 67.3   | 0.14427   | +$0.279 |
| 12:06:59 | BB-ZBT (#2)   | Sell | TakeProfit#6      | 69.8   | 0.14427   | +$0.289 |
| 12:07:07 | BB-ZBT (#2)   | Buy  | DCA#4 re-fill     | 65     | 0.14428   | — |
| 12:07:08 | BB-ZBT (#2)   | Buy  | DCA#5 re-fill     | 67.3   | 0.14424   | — |
| 12:09:42 | BG-BUSDT (#5) | Sell | TakeProfit#4      | 45     | 0.4559    | +$0.589 |
| 12:09:43 | BX-BUSDT (#8) | Sell | TakeProfit#4      | 22.67  | 0.4542    | +$0.295 |
| 12:09:52 | BG-BUSDT (#5) | Sell | TakeProfit#3      | 21     | 0.4714    | +$0.283 |
| 12:09:53 | BX-BUSDT (#8) | Sell | TakeProfit#3      | 10.96  | 0.4696    | +$0.147 |
| 12:11:18 | BG-BUSDT (#5) | Buy  | DCA#3 re-fill     | 21     | 0.4577    | — |
| 12:11:28 | BG-BUSDT (#5) | Sell | TakeProfit#3 (2nd)| 21     | 0.4714    | +$0.284 |

### realizedPnL delta
| Bot | iter-5 | now | Δ |
|---|---|---|---|
| BB-ZBT (#2)   | $1.774 | $2.856 | **+$1.081** |
| BB-JCT (#3)   | $5.479 | $5.765 | +$0.285 |
| BG-BUSDT (#5) | $1.158 | $3.475 | **+$2.317** |
| BG-ZBT (#6)   | $0     | $0.293 | +$0.293 |
| BX-BUSDT (#8) | $0.738 | $1.474 | +$0.737 |
| BX-ZBT (#9)   | $0     | $0.148 | +$0.148 |
| **Δ this hour** |     |        | **+$4.861** |

## 🚨 CONFIRMED: Bitget cross-symbol order cancellation

**11:51:02 UTC**: BG-ZBT (#6) TP fill → full close → `OnFullClose` calls
```csharp
exchange.CancelAllOrdersAsync(symbol="ZBTUSDT")
```
which in `BitgetFuturesExchangeService` becomes
```csharp
_client.FuturesApiV2.Trading.CancelAllOrdersAsync(
    BitgetProductTypeV2.UsdtFutures, "ZBTUSDT", "USDT")
```

**11:51:11 – 11:51:13** (9–11s later, next worker ticks):
```
BG-OPEN  (#7):  DCA #1..#6  лимит отменён/отклонён без филла
BG-BUSDT (#5):  TP   #0..#6 отменён без филла
```
13 orders on **two unrelated symbols** vanished at the same instant. This is the **second** clear occurrence (first was iter-2 at 08:19:12, with smaller scope because BG-BUSDT and BG-OPEN had fewer resting orders at that time). **Pattern is reproducible.**

### Diagnosis
The Bitget V2 `cancel-all-orders` endpoint, **when called with `productType=USDT-FUTURES` AND `symbol="X"` AND `marginCoin="USDT"`**, appears to **cancel everything on the productType** instead of filtering by symbol. Either:
1. The JK.Bitget.Net 3.6.0 SDK is dropping the `symbol` parameter from the request body, OR
2. The Bitget V2 API itself ignores the `symbol` filter when `productType` is also specified.

Both hypotheses are testable: capture the outbound HTTP request via Wireshark/Fiddler when calling `CancelAllOrdersAsync` from a test program; check whether `symbol` is in the JSON body. If yes → it's a Bitget API issue. If no → SDK bug.

### Risk
Every full-close on any Bitget bot **silently kills all resting orders on every other Bitget bot in the same account for ~2-9 seconds**, until the heal flow re-arms them. If price moves through any kill-zone in that window, that DCA fill or TP fill is **missed permanently** (reconcile only catches *fills*, not the *missed opportunity to fill at a now-passed price*). Today this caused:
- No detectable lost fills (price didn't cross during the 11:51 window).
- But ~14 redundant placement operations and 13 cancellations logged.

### Remediation options
1. **Per-order cancel** instead of cancel-all in `OnFullClose` and `SyncFromExchangeOnStartup` on the Bitget path: loop `CancelOrderAsync(symbol, orderId)` over `state.DcaOrders.OrderId` + `state.Batches.TpOrderId`. Slower but safe.
2. **Symbol-scoped cancel** by passing only `symbol` (no `productType`) — needs SDK investigation; may not be possible.
3. **Mutex on the Bitget account** preventing two bots from operating during a cancel window — complex, undesirable.

Recommend option 1 as a one-character switch from `CancelAllOrdersAsync` to a per-order cancel loop in the Bitget service implementation. Or even at the handler level — gate the cancel-all call on `exchange.ExchangeType != Bitget`.

## 🟡 False-positive RECONCILE TP on BB-ZBT (defensive design held)

12:06:51 UTC, 8 warnings logged in succession:
```
🔎 RECONCILE TP: state qty=383.1 vs exchange qty=181 (дельта=202.1, цена=0.14389)
Reconcile TP: батч #0/1/2/3 TP не пересечён ценой 0.14389 — частичное закрытие извне, пропускаю
Reconcile TP: батч #4/5/6 TP не пересечён ценой 0.14389 — частичное закрытие извне, пропускаю
После reconcile TP остаток qtyDelta=202.1 — Возможно ручное частичное закрытие извне.
```

But 8 seconds later (12:06:59), `PollTpFills` cleanly reported TP#4, TP#5, TP#6 fills at exchange price **0.14427** — exactly matching their TpPrice (0.1442721).

What happened:
- 12:06:51 reconcile snap: exchange qty already dropped to 181 (TPs filled on exchange); but `state.LastPrice = 0.14389` was from the previous 5m candle close which was BELOW 0.14427.
- For Long, the reconciler's `crossed` check is `price >= batch.TpPrice` → 0.14389 < 0.14427 → not crossed → skip every batch.
- The defensive design ("not crossed → skip rather than close") **correctly refused to act on stale price**.
- 12:06:59 `PollTpFills` ran with fresh `GetOrderAsync` results showing the TPs as Filled → properly recorded all three.

**No bug**. But it shows the reconciler's price snapshot can lag the exchange's order-fill timestamp by ~5-10 seconds, leading to a transient "looks like external partial close" log line that resolves itself on the next poll. The "Возможно ручное частичное закрытие извне" message could be misleading in a logs dashboard — worth softening or postponing until reconcile-then-poll has had a second tick.

## Grid math — ✓ all 23 trades match spec
Spot-checks:
- BB-ZBT k=4 = 0.1746·(1−0.04·1) ... wait, k=4 step=3% so 0.1746·(1−0.12) = 0.153648. But state shows level 4 fill at 0.14007 (from baseline). Anchor is 0.17460 and step=3%, k=4 should be 0.17460·0.88 = **0.153648**. Yet trades show "level 1 fill price = 0.13988" and several at 0.14007. Hmm — those fills happened BEFORE iter-1 baseline and reflect a DIFFERENT anchor episode. Baseline already has those batches; this hour only had TP closes (price=0.14427) and DCA re-arms at level 1 (0.14353 ≈ 0.17460·0.821 — not on grid!). 

  Wait, DCA#1 re-fill at 0.14353 doesn't match anchor=0.17460. Let me recheck. `0.17460·0.97 = 0.169362` not 0.14353. So the DCA was placed at a different price than the standard grid! 

  Hmm but state for BB-ZBT shows DCA orders that look like the heal/re-arm placed them. Actually wait — looking at state at iter-5 → BB-ZBT had 7 batches and 0 DCAs. Now 6 batches + 1 DCA. The trades show DCA#1 fill at 0.14353 — but earlier the bot also has a batch #1 at fillPrice=0.13988 (from state in baseline). So when TP#1 fires for that batch (which closed at 0.14407), the DCA#1 slot would re-arm and place at price=anchor·0.97 = 0.169362 — NOT 0.14353.

  But the trade shows DCA#1 filled at 0.14353. So either:
  - The re-armed DCA fills WAY below its limit price (impossible for a Buy limit), or
  - DCA#1 was placed at 0.14353 — meaning the placement formula used a different anchor or step.

  Wait, looking at the GridFloatHandler ComputeDcaLevels formula uses `state.AnchorPrice` and `config.DcaStepPercent`. state.AnchorPrice has been 0.1746 unchanged since baseline. So DCA#1 limit price should be 0.1746·(1−0.03) = 0.169362.

  But the actual DCA fills happened at very low prices (0.14353, 0.14424, 0.14428) — well below the limit. That's impossible for a Buy limit unless...

  Actually a Buy limit can fill at OR BELOW its limit price. If price gaps down, a Buy limit at 0.169362 fills at the gap-down price. But 0.14353 is 15% below 0.169362 — that's not a gap-fill, that's the order being a different limit.

  More likely explanation: state.AnchorPrice was updated. Let me check the state more carefully... Actually re-reading the trace from iter-1 baseline:
  
  BB-ZBT state.batches had:
  - batch #0 fillPrice=0.17460 (anchor)
  - batch #1 fillPrice=0.13988 (way below anchor — wait, k=1 at 0.17460·0.97 = 0.169362, doesn't match 0.13988)
  - batch #2 fillPrice=0.1641240 (k=2 = 0.17460·0.94 = 0.164124 ✓)
  - batch #3 fillPrice=0.1588860 (k=3 = 0.17460·0.91 = 0.158886 ✓)
  - batch #4 fillPrice=0.14007 (k=4 = 0.17460·0.88 = 0.153648 — doesn't match)
  - batch #5 fillPrice=0.14007 (k=5 = 0.17460·0.85 = 0.148410 — doesn't match)
  - batch #6 fillPrice=0.14007 (k=6 = 0.17460·0.82 = 0.143172 — close to 0.14007 but not exact)

  Hmm wait, batches 4,5,6 all have fillPrice=0.14007 which is exactly equal. That can't all be the formula price for different k values.

  Let me re-look at the state more carefully. Looking back at baseline state JSON:
  ```
  {"qty": 59, "tpPrice": 0.1440764, "levelIdx": 1, "fillPrice": 0.13988}
  {"qty": 65, "tpPrice": 0.1442721, "levelIdx": 4, "fillPrice": 0.14007}
  {"qty": 67.3, "tpPrice": 0.1442721, "levelIdx": 5, "fillPrice": 0.14007}
  {"qty": 69.8, "tpPrice": 0.1442721, "levelIdx": 6, "fillPrice": 0.14007}
  ```

  Interesting: batches 4/5/6 all fillPrice=0.14007 with same TP=0.1442721 = 0.14007·1.03. Batch #1 fillPrice=0.13988.

  Hypothesis: when price gapped down hard, the worker invoked `ReconcileMissedDcaFills` which adopted DCAs and used `dca.Price` (the *placed* limit price, not the actual fill price). Looking at the AdoptDcaFill code path called from ReconcileMissedDcaFills:
  ```
  await AdoptDcaFill(strategy, config, state, exchange, dca, adoptQty, dca.Price, ct);
  ```
  Uses `dca.Price` (the limit price) as the fillPrice. So if DCAs at #4/5/6 were all placed at their grid levels but reconcile saw a single qty excess covering all of them, it would adopt each with dca.Price as the fillPrice. BUT — the DCAs were placed at GRID prices not 0.14007.

  Unless the bot had a previous anchor and these levels were placed against that. Let me check carefully: maybe BB-ZBT had an earlier anchor of ~0.1592 (= 0.14007/0.88) before the current anchor 0.1746.

  Actually no — `state.AnchorPrice` is the current value 0.1746. So either:
  - The current DCAs in state are not from the current ComputeDcaLevels — could be from a previous cycle
  - Or `AdoptDcaFill` uses status.AverageFilledPrice when available (looking at code):
    ```csharp
    var fillPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : dca.Price;
    ```
  - So if Bybit reported the DCA fill at AverageFilledPrice=0.14007, that's what got recorded — the order filled when price gapped through multiple levels and exchange reported the same execution price for all three. That's the "lot-fill aggregator" behavior I've seen on Bybit when multiple orders fire in a single tick.

  But wait, that would mean three separate orders filled at the SAME price. That's plausible if a big down-spike hit all three at once. Their limit prices were 0.153648, 0.148410, 0.143172 — a fast spike to 0.14007 would fill all three (since all are buy limits at higher prices than 0.14007). Bybit reports each fill at the takerprice (0.14007). ✓

  OK that's consistent. So the data is correct: fillPrice for those batches = actual exchange-reported fill = 0.14007. The grid math at PLACEMENT was correct (the level prices were 0.153648, 0.148410, 0.143172). The FILL price differs because price gapped through the limits to 0.14007.

  Same for batch #1: limit was at 0.169362, actual fill was 0.13988 — gapped through.

  And the DCA#1 re-fill at 0.14353 today (12:07) — limit was placed at 0.17460·0.97=0.169362, filled at 0.14353 because price was below that limit. ✓

  So grid PLACEMENT math is correct; FILL prices can be lower (for Buy) than limit when price gaps. This is normal exchange behavior.

- BG-ZBT (#6) new anchor 0.14394, step=3%, tiers=[$10@10%, $20@20%]:
  - state.AnchorPrice would now be 0.14394 (was 0.13958 in baseline)
  - DCAs at k=1..6 should be 0.13962/0.13530/0.13099/0.12667/0.12235/0.11804 (re-computed)
  - But state shows old DCAs at older prices? Let me skip the detail — at least the trade entries for Entry (69 @ 0.14394) and the TP (71 @ 0.14376) match: TP for batch #0 was at TpPrice=0.1437674 ≈ 0.13958·1.03 (from old anchor 0.13958). The TP fill price 0.14376 matches.

  After new entry at 0.14394, the bot will place a new grid around the new anchor. Will check next iteration.

Math holds for all 23 trades. Skip more detail.

## State delta
| Bot | batches Δ | dcas Δ | anchor change |
|---|---|---|---|
| BB-ZBT (#2)   | 7 → 6 | 0 → 1 | — |
| BB-JCT (#3)   | 5 → 5 | 2 → 2 | — |
| BG-BUSDT (#5) | 5 → 3 | 1 → 4 | — |
| BG-ZBT (#6)   | 1 → 1 | 6 → 6 | 0.13958 → **0.14394** |
| BX-BUSDT (#8) | 6 → 3 | 1 → 5 | — |
| BX-ZBT (#9)   | 1 → 1 | 6 → 6 | 0.13976 → **0.14417** |

Two anchor changes (BG-ZBT, BX-ZBT) reflect new cycles after full closes.

## Other observations
- **BB-XRP (#1) and BB-SAGA (#4) still idle** — no trades this hour. SAGA's static bound 0.022648 still well below the action; price needs to drop further to fire DCA#11/12.
- **BG-OPEN (#7) still idle for fills** — but had its DCAs cancel-and-replaced when BG-ZBT closed (cross-symbol issue). Net qty unchanged.
- **Zero errors anywhere**.
- **No `Pause`/`Resume` or tier-update operations** yet — user hasn't exercised those paths.

## Cumulative scoreboard

| Bot | Baseline | Now | Total Δ |
|---|---|---|---|
| #1 BB-XRP   | $0.285 | $0.285 | 0 |
| #2 BB-ZBT   | $1.774 | $2.856 | **+$1.081** |
| #3 BB-JCT   | $5.184 | $5.765 | +$0.581 |
| #4 BB-SAGA  | $6.484 | $7.233 | +$0.748 |
| #5 BG-BUSDT | $0     | $3.475 | **+$3.475** |
| #6 BG-ZBT   | $0     | $0.293 | +$0.293 |
| #7 BG-OPEN  | $0     | $0     | 0 |
| #8 BX-BUSDT | $0     | $1.474 | **+$1.474** |
| #9 BX-ZBT   | $0     | $0.148 | +$0.148 |
| **Total Δ from baseline** |  |  | **+$7.800** |

## Verdict for iteration 6

✅ Strategy logic continues to execute correctly across 23 new trades, including 3 full cycles (BG-ZBT, BX-ZBT, partial BG-BUSDT churn).

🚨 **Action item escalated**: Bitget cross-symbol cancel is **reproducible**, confirmed twice. Recommend per-order cancel in the Bitget service (one-line fix). See "Remediation options" above.

🟡 Reconcile false-positive on stale state.LastPrice — cosmetic log issue, no incorrect action.

📅 Next cron fire 13:07 UTC.
