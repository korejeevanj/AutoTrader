# Hedging, Events and Deferred Alternatives

Compiled: 9 October 2026 | Historical exploration; current implementation scope takes precedence

## Cross-index hedges

Earlier discussions explored Bank Nifty as the main profit source with an opposite Nifty position, reversing that relationship, and Nifty–Sensex pairs. Preserve these as research alternatives. Shared constituents/correlation do not eliminate basis, timing, volatility, expiry or execution differences.

Test joint underlying/option paths, adverse divergence, total costs and basket-level risk. A hedge may reduce one loss while consuming the desired small profit or adding a new loss scenario. The current first research policy uses long calls/puts on Bank Nifty, with debit spreads later; cross-index combinations are deferred.

## Overnight exposure

Jeevan explored overnight spreads to capture gaps while limiting losses. The current initial experiment is intraday with no overnight exposure. Gap, fill and multi-leg risks remain later research topics. Earlier overnight loss preferences are not automatically current execution settings.

## Events and daily context

Related discussions asked for RBI/government speeches, policy decisions, inflation, US yields, crude/geopolitical developments and FII/DII context. The dashboard should show event time in IST, verification/source time and observation trade date. Separate an announced event from speculation and explain that its price effect depends on expectations and the actual outcome.

FII/DII is a published observation, not assumed live order flow. Display previous published values honestly; do not claim today's final number before it exists. The market forecast/audit automations are separate workflows; no new scheduled task is created by these documents.

## AI and statistical models

Earlier discussion questioned whether prediction could become token-heavy without adding value. Keep numerical features, fees, risk checks and deterministic policies in code. No LLM call is required on every tick. AI can later help summarize sourced news, explain recorded reasons and support research; its outputs need evaluation and must not bypass risk gates.

Machine learning is a later hypothesis if a baseline and sufficiently credible data exist. Define horizon, outcome labels, chronological splits and net-of-cost comparison before adding a model. A provider's probability field is not proof of our intraday trade success rate.

## Current decision versus preserved alternatives

| Topic | Current plan | Preserved alternative |
| --- | --- | --- |
| First product | Manual dashboard with recording | Earlier replay-first ordering, now superseded |
| First market | Bank Nifty | Earlier Nifty-first comparisons |
| First selection research | Explicit opening-range/VWAP/bank hypothesis | EWMAC, ML and more strategy families |
| Initial structures | Long calls/puts; debit spreads later | Multi-leg non-directional and cross-index baskets |
| Holding | Intraday | Overnight gap capture |
| Execution | Broker app for manual phase; Upstox automation later | Earlier monitoring-first automation proposal |

These retained ideas should become separate experiments when justified, without silently expanding the dashboard MVP.
