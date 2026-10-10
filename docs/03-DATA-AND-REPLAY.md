# Data and Replay Specification
Updated: 9 October 2026 | Availability researched; authenticated coverage not yet tested

## Live dashboard data comes first

Deliver a live manual dashboard and recorder before selection research. Proposed live source is Kite; proposed actual account/position source is Upstox. Historical acquisition remains a parallel research dependency. No authenticated feed/account smoke test has been completed.

Record raw provider events where permitted and normalized contract identity, source event time, receipt time, source-specific quote age and quality. Keep last trade age distinct from bid/ask freshness and transport heartbeat. Preserve reconnect gaps; recover an account snapshot and deduplicate updates rather than assuming missed events did not occur.

Subscribe to Bank Nifty, eligible futures, verified current banks and a configurable liquid option universe. Nifty/Sensex/VIX are optional context when available. Current and historical weights are separate datasets. Journal/actual/paper states stay separate. Record IV/Greeks enrichment independently with provider/model provenance; neither market-depth quantities nor day-total volume establishes aggressor order flow.

The browser receives snapshots/deltas; its display throttling must not stop recording. Archive schema, mapping and configuration versions. Report retained versus omitted event fields and file retention/disk requirements after measurement.

Primary live references: https://kite.trade/docs/connect/v3/websocket/ ; https://kite.trade/docs/connect/v3/market-quotes/ ; https://upstox.com/developer/api-documentation/get-portfolio-stream-feed/ . Optional enrichment: https://upstox.com/developer/api-documentation/get-pc-option-chain/ . Verify access before depending on fields.

## Historical acquisition plan
Target: all trading sessions in September 2026, plus earlier warm-up sessions. Begin with one sample day, then a small mix of trend, range and volatile days, then the full month.

| Input | Proposed source | Verification required |
| --- | --- | --- |
| Bank Nifty index and bank-stock minute bars | Kite historical API or Upstox historical API | Exact instruments, timestamps, completeness |
| Expired Bank Nifty futures/options minute bars | Upstox expired-instrument APIs | Plus entitlement, contracts, per-day returned coverage |
| Contracts still active today but traded in September | Normal historical API and current instrument definitions | Active/expired routing and historical validity |
| Historical constituent membership/weights | Official historical index records | Effective date; do not substitute current weights |
| Historical bid/ask and order events | Separate licensed data source if required | Coverage, format and acquisition cost |
| Applicable charges | Broker/exchange schedules for trade date | Order count, sides, lot quantity and taxes |

Upstox documents one-minute expired OHLC, volume and OI and requires Plus. Its Get Expiries documents up to six months of historical expiry discovery. September is within that discovery window as of this document, but per-contract completeness is unverified.

Kite does not supply expired-option history; continuous expired-futures history is daily. Do not use it as the sole source for the derivatives replay.

## Required data pack
1. All underlying/futures/bank inputs used by the rule.
2. Options across the predefined eligible universe, including contracts expiring after September.
3. Point-in-time contract definitions: exchange, expiry, strike, lot/tick size.
4. Enough earlier sessions for each feature's warm-up.
5. Raw provider responses, normalization mapping and download timestamps.
6. A quality report and immutable checksums.
7. Event/expiry calendar where the policy uses it.

Never choose the option universe after observing which contracts won. A missing record can mean no trading or a feed gap; classify it using evidence. Do not automatically create zero-volume candles or forward-fill a tradable price.

## Normalized files
For the first historical replay use contracts.json, bars.csv, constituent-weights.csv and dataset-manifest.json. Convert to more efficient storage if measurements justify it.

bars.csv fields:
contract_id, start_utc, end_utc, available_at_utc, interval_seconds, open, high, low, close, volume, oi, source, quality_flags

Use UTC internally and Asia/Kolkata for session configuration/display. Provider timestamps may label the beginning of a candle; normalize this explicitly. A completed 09:30–09:31 bar is available no earlier than 09:31.

dataset-manifest.json records requested/returned periods, instruments, source, interval, downloaded_at, data hashes, schema version, known omissions and permissions relevant to retention/use.

## Quality gates
- OHLC relationships valid; no impossible negative volume or quantity.
- Stable duplicate detection with conflicting duplicates quarantined.
- Dates and instrument metadata correspond to actual contract validity.
- Session and holiday alignment checked.
- Warm-up coverage sufficient; incomplete features cannot trigger trades.
- Bank inputs synchronized; missing constituent weight is reported.
- No future data used for weights, volumes, contract universe or indicators.
- Missing option quotes/Greeks explicitly identified.

## Replay contract
At simulated time t, make visible only events with AvailableAt <= t. Historical labels and later outcomes belong to the evaluator, never to the engine's decision inputs.

Support run-to-end and step/pause modes. Playback speed changes wall-clock duration only; it must not change decisions.

For candle-based entries, use an explicitly documented next-bar fill model. Account for opening gaps and insufficient data. Do not fill at the close that created the signal unless an independently justified executable event is available afterward.

When one candle touches both stop and target, flag unknown order and apply the conservative scenario. One-minute replay cannot validate sub-second protective exits.

For multi-leg research later, allow asynchronous/failed fills; simultaneous perfect fills are an optimistic scenario, not the default.

## Profit estimation
Use actual historical option prices when available. Apply an explicit spread/slippage model because trade candles are not executable bid/ask quotes. Deduct brokerage and applicable taxes/charges; avoid counting the same spread twice.

Report base and adverse-cost outcomes. Keep estimated IV/Greeks separate from observations. Where liquidity is unmeasured, report the result as candle-based research, not executable profitability.

## Sources checked
- Upstox expired candles: https://upstox.com/developer/api-documentation/get-expired-historical-candle-data/
- Upstox expiry discovery: https://upstox.com/developer/api-documentation/get-expiries/
- Upstox expired option definitions: https://upstox.com/developer/api-documentation/get-expired-option-contracts/
- Kite historical API: https://kite.trade/docs/connect/v3/historical/
- Kite expired-options limitation: https://kite.trade/forum/discussion/15733/historical-options-data
- NSE historical order/trade data: https://www.nseindia.com/static/market-data/eod-historical-data-subscription

Data access and prices may change. Verify the required endpoints before purchasing or depending on a subscription. No market data has been acquired as part of this planning task.
