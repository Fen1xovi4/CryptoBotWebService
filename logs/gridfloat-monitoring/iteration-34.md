# GridFloat monitoring — Iteration 34

**Captured**: 2026-05-15 17:11 UTC (19:11 Warsaw)
**Δ from iteration-33**: ~118 min (iter-33 fired at 15:13 UTC, this iteration restarts the hourly loop)
**Cron**: new session — `13 * * * *` (HH:13 UTC)
**Workspace**: `GRID-BB-M-Algida+BG-S-Insider+BX-M-IJKL+BB-SAS-Short` (`ad651349-…`)

## Roster (12 bots)

| Acc | Exch | Bot | Symbol | Dir | Anchor | Batches | DCAs | Realized $ |
|---|---|---|---|---|---|---|---|---|
| BB-M-Algida | Bybit | #3 | JCTUSDT | Long | 0.004695 | 5 | 6 | 9.167 |
| BB-M-Algida | Bybit | #c6 | SAGAUSDT | Long | 0.03683 | 12 | 10 | 12.340 |
| BB-M-Algida | Bybit | #e7 | XRPUSDT | Long | 1.5378 | 7 | 4 | 1.233 |
| BB-M-Algida | Bybit | #67 | ZBTUSDT | Long | 0.17460 | 5 | 6 | 5.955 |
| BG-SASH-Insider | Bitget | #3f | BUSDT | Long | 0.5273 | 5 | 9 | 10.131 |
| BG-SASH-Insider | Bitget | #b3 | OPENUSDT | Long | 0.1817 | 2 | 5 | 0 |
| BG-SASH-Insider | Bitget | #9d | ZBTUSDT | Long | 0.15621 | 2 | 5 | 2.058 |
| BX-M-IJKL | BingX | #1c | BUSDT | Long | 0.5356 | 6 | 5 | 5.159 |
| BX-M-IJKL | BingX | #0a | ZBTUSDT | Long | 0.15840 | 2 | 5 | 0.887 |
| BB-SASH-ShortSMA | Bybit | #4f | BANANAS31USDT | Long | 0.01184 | 1 | 25 | 0 |
| BB-SASH-ShortSMA | Bybit | #46 | FFUSDT | Long | 0.08959 | 1 | 15 | 0 |
| BB-SASH-ShortSMA | Bybit | #7e | ZBTUSDT | Long | 0.15451 | 1 | 15 | 0.096 |

**Total realized (state-side)**: $47.126

## Δ Activity since iter-33 (15:13 → 17:11 UTC, ~118 min)

### Trades (27 records — including 17 phantom JCT DCA#5 dupes)

**Real (non-phantom) trades — 10:**

| Time UTC | Bot | Side | Status | Qty | Price | PnL |
|---|---|---|---|---|---|---|
| 16:14:14 | BG-BUSDT (#3f) | Buy | DCA#5 fill | 44 | 0.44820 | — |
| 16:21:24 | BB-SAGA (#c6) | Sell | TP#12 | 848.4 | 0.02427 | +$0.585 |
| 16:29:37 | BB-XRP (#e7) | Sell | TP#7 | 6.9 | 1.44440 | +$0.095 |
| 16:48:20 | BB-ZBT (#67) | Sell | TP#5 | 67.3 | 0.15286 | +$0.295 |
| 16:49:11 | BX-ZBT (#0a) | Sell | TP#2 | 33.58 | 0.15335 | +$0.148 |
| 16:50:12 | BB-BANANAS (#4f) | Buy | **Entry** | 800 | 0.01184 | — |
| 16:50:43 | BB-ZBT-SASH (#7e) | Buy | **Entry** | 65.3 | 0.15309 | — |
| 16:57:53 | BG-BUSDT (#3f) | Sell | TP#5 | 44 | 0.46160 | +$0.581 |
| 17:01:19 | BB-FF (#46) | Buy | **Entry** | 111 | 0.08959 | — |
| 17:02:28 | BB-ZBT-SASH (#7e) | Sell | TP#0 | 65.3 | 0.15462 | +$0.096 |
| 17:05:10 | BB-ZBT-SASH (#7e) | Buy | **Entry** | 64.7 | 0.15451 | — |

**TP-fill total this window: +$1.800**

### Log level counts since iter-33

| Acc | Error | Warning | Info |
|---|---|---|---|
| BB-M-Algida (Bybit, 4 bots) | **221** | 36 | 41 |
| BB-SASH-ShortSMA (Bybit, 3 bots) | 0 | 0 | 94 |
| BG-SASH-Insider (Bitget, 3 bots) | 0 | 1 | 5 |
| BX-M-IJKL (BingX, 2 bots) | 0 | 0 | 2 |

## 🚨 Issue tracker

### 🚨 BB-JCT (#3) — DCA#5 phantom re-fill loop (17 trades, 16:05-16:08 UTC)

Between 16:05:10 and 16:08:19 UTC the strategy recorded **17 identical Buy DCA#5 trades**: qty=59.47, price=0.00399084. All within ~3 minutes. Then it stopped.

This is a regression from iter-32/33's "Qty 0 < min 100" loop — Fix #5 (sub-min batch cleanup in `PlaceBatchTpLimit`, [GridFloatHandler.cs:396-407](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L396-L407)) appears deployed (errors dropped from 310 → 221), but a new path is now writing the same `Trade` row repeatedly when adopting the same DCA fill via `AdoptDcaFill`.

**Likely root cause**: `ReconcileMissedDcaFills` ([GridFloatHandler.cs:817-861](backend/src/CryptoBotWeb.Infrastructure/Strategies/GridFloatHandler.cs#L817-L861)) sees exchange qty > state qty, adopts the DCA → adds a Batch → next tick the same DCA is still in the pending list because the batch was dropped sub-min by `PlaceBatchTpLimit`. Loop continues until the price moves enough to push past noise floor or the exchange order vanishes.

**Impact**: 17 phantom Trade rows but realized PnL unchanged ($9.167) — `AdoptDcaFill` doesn't touch `RealizedPnlDollar`. So PnL stats stay correct, but `trades` table has 17 fake DCA entries that would inflate any trade-count metric.

### 🟡 BB-M-Algida — 221 errors / 118 min

Down from 310/hr in iter-33 to ~112/hr now. Same "TP not placed: Qty 0 < min N" family but the cleanup loop is partially clearing them. Need next iteration to confirm trend.

## Cross-exchange health snapshot

- **Bybit** (BB-M-Algida + BB-SASH-ShortSMA): mostly OK; JCT bot remains noisy. 4 TP fills + 3 new entries.
- **Bitget** (BG-SASH-Insider): clean — 1 DCA fill, 1 TP fill, +$0.58 realized.
- **BingX** (BX-M-IJKL): clean — 1 TP fill, +$0.15 realized.

## Verdict for iteration 34

✅ Three exchanges executing TP fills as expected — +$1.80 across 5 TPs this 2-hour window.
✅ Bitget and BingX log-clean.
🚨 **Bybit JCT bot has shifted from a TP-placement loop into a DCA-re-adoption loop** producing 17 phantom trade records. Action: deploy diagnostic on next iteration — capture the exact log sequence around 16:05-16:08 UTC and trace whether `state.DcaOrders` still contains the level-5 order after `AdoptDcaFill` drops the batch.

📅 New hourly cron armed at HH:13 UTC — next fire 18:13 UTC.
