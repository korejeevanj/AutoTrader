# Historical Replay and Validation — Research Summary

Compiled: 9 October 2026 | Related discussions: 1 and 6 October; supplied data/replay plan

## Relationship to the dashboard

Build live recording with the manual dashboard. The resulting observations support later replay without reconstructing unobserved depth or quotes. September 2026 remains a historical research target; its complete data pack has not been obtained. Live recording does not retroactively provide September ticks.

## Source capabilities versus actual coverage

- [Upstox expired candles](https://upstox.com/developer/api-documentation/get-expired-historical-candle-data/): documented expired-derivative research route; entitlement and per-contract coverage require testing.
- [Upstox expiry discovery](https://upstox.com/developer/api-documentation/get-expiries/): discover eligible historical expiries and contracts before requests.
- [Kite historical](https://kite.trade/docs/connect/v3/historical/): use appropriate active instruments; continuous expired-futures history does not supply expired-option quote replay.
- [Kite discussion of expired options](https://kite.trade/forum/discussion/15733/historical-options-data): limitation linked in the supplied plan.
- [NSE historical order/trade subscription](https://www.nseindia.com/static/market-data/eod-historical-data-subscription): possible additional acquisition route if candle-only research cannot support execution claims.

Latest endpoint documentation should be checked before a purchase; no market data or paid subscription has been acquired here. See the full [data specification](../03-DATA-AND-REPLAY.md) for the pack and schema.

## Correctness before performance

Make events visible only when AvailableAt <= simulated time. A bar beginning 09:30 and ending 09:31 cannot trigger a completed-bar rule at 09:30. Synchronize bank inputs and weights. The option universe must be chosen without knowledge of later winners.

Use an explicit next-bar entry/fill model for candle-only tests. When the same candle reaches stop and target, flag unknown ordering and apply a conservative scenario. Never infer sub-second stop behaviour from one-minute bars. Multi-leg simulation later must model failed/asynchronous fills.

## Research evaluation

Record the complete trade ledger, no-trade days, rejected candidates, fees, net expectancy, drawdown, worst trades/days and stop overruns. Compare base/adverse costs, latency and feature ablations. Keep raw inputs, config/code versions and hashes.

One month verifies integration and an initial hypothesis; it cannot establish durable profitability. Freeze parameters before chronological unseen evaluation. Avoid overlapping future-outcome windows between training/tuning and evaluation where relevant.

Synthetic data proves software behaviour only. Repository example performance and earlier conversational statistics are not AutoTrader results. Real market evidence begins when a validated dataset is processed under recorded assumptions.
