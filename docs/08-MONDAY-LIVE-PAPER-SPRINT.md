# AutoTrader — Monday Live Paper-Trading Sprint

Prepared: 10 October 2026. Target live session: Monday, 12 October 2026. All schedule times are Asia/Kolkata.
Status: implementation plan, not a completed application.

## Outcome and scope change

Deliver a local Bank Nifty application that receives real market data, displays a dashboard, identifies candidates using one explicit policy, simulates entries and exits, monitors paper positions, and records decisions and estimated net P&L.

The application must be ready for rehearsal by Sunday night and connected before Monday's opening range. Monday evening is for reviewing the live run and fixing defects, not starting the live integration.

This sprint brings experimental selection and paper simulation forward from the 9 October baseline. It does not advance real execution or replace historical and unseen evaluation. Keep the baseline documents for the wider project; this plan governs the Monday milestone. No application has been implemented by this planning task. The baseline documents state implementation has not started; inspect the actual development repository before scaffolding to avoid duplicating subsequent work.

Feasibility is conditional on an active data-enabled Kite app, working authentication, a development computer available through the market session, and roughly 20–30 focused implementation hours with AI assistance. This is an estimate, not a delivery guarantee. Account activation or missing required data can block Monday live testing even if the software is ready.

## Required before building

| Dependency | Required evidence | Deadline / fallback |
| --- | --- | --- |
| Active Zerodha account and Kite developer app | Login and profile request succeed | Saturday; pending onboarding blocks genuine live mode |
| Data entitlement | Quotes and historical requests accepted | Saturday; free Personal API lacks these data services |
| Login redirect and token exchange | Backend completes session exchange without exposing secrets to UI/logs | Saturday; renew login Monday morning |
| Local tooling and repository | Supported .NET SDK pinned, Node/package lock, Git, browser, clean build | Saturday |
| Contract definitions | Provider master supplies current expiry, strike, lot size, tick size, symbol/token mapping | Refresh Monday before the session; never hardcode old quantities |
| Bank membership and weights | Verified source, publication/effective date, coverage disclosed | Sunday; missing weights disable weighted strategy entries, while dashboard/recommendations show insufficient evidence |
| Fee/configuration snapshot | Effective-date schedule, brokerage model, quantity, slippage and risk preferences stored | Sunday; missing fee assumptions block cost-qualified paper recommendations |

Keep API key/secret and tokens in server-side local secrets. Do not paste credentials into chat or commit them. A weekend response may contain Friday's last quote: label it cached/market closed, not live. Postman validates authentication, instruments, quote snapshots and historical responses; the application backend supplies the continuous WebSocket integration.

Kite's documented paid plan is ₹500/month per API key and includes live and historical data. Its standard access token expires at 6 AM the next day, so Sunday authentication does not cover Monday. Live prices come from WebSocket streaming, not webhooks or the instrument-master last_price field. Full streaming packets provide market depth; cash-index packets have different fields.

## Minimum implementation

Use a single local .NET API process containing feed, recorder, analytics, engine and paper broker modules; React/TypeScript with SignalR for the UI. Retain separate domain boundaries without deploying separate services. Use SQLite for journal/config/paper state and append-only versioned files for recorded market events. PostgreSQL is optional only if already installed and uncomplicated. No database reads in each candidate evaluation.

| Module | Deliverable |
| --- | --- |
| Kite data adapter | Authentication, daily instruments, REST smoke tests, full-mode WebSocket, reconnect/backoff |
| Market state and recorder | Bounded in-memory latest quotes, event/receipt timestamps, instrument freshness, bars, append-only recording |
| Analytics | Completed one-minute futures bars, 09:15–09:30 opening range, session futures/bank VWAP approximation, bank confirmation |
| Selection engine | Setup detection, bounded candidate generation, hard gates, deterministic ranking, recorded reasons |
| Position policy | HOLD, WATCH_EXIT, EXIT; independent quote-driven cash-stop evaluation |
| Local paper broker | Idempotent entry/exit intents, delayed quote-based fills, fees, lifecycle, persistent ledger |
| Dashboard | Context chart, bank table, candidate table, open position, decision reasons, journal, connection/freshness |
| Session report | Net/gross P&L, fees, rejected trades, gaps, latency, loss overruns, version/config provenance |

Required subscriptions: Bank Nifty index, selected valid futures contract, verified current constituent banks, and a configurable 20–35-option universe. Select the universe using current moneyness, valid non-expiry-day contracts and observed liquidity; freeze/log each universe version. A current-only universe can include wider liquid strikes. Defer Greek-based selection when credible Greek estimates are unavailable; log that change from the baseline hypothesis.

The UI must always show PAPER + LIVE DATA, PAPER + REPLAY, or SYNTHETIC TEST. It must distinguish a recommendation, pending paper intent and simulated fill. Provide paper-auto enable/pause and manual paper-entry/exit controls. Manual entries bypass setup detection only; they still pass contract, freshness, cash and liquidity checks and are labelled MANUAL_TEST separately from strategy trades.

## First strategy and outputs

Reuse the baseline opening-range-breakout hypothesis:

1. Collect the complete 09:15–09:30 futures opening range.
2. Between 09:30 and 11:30, use completed one-minute futures closes for a breakout in agreement with futures VWAP.
3. Require at least 65% of verified constituent weight above each bank's own VWAP for bullish, or below for bearish. Missing required observations produce insufficient evidence, not a denominator renormalization that hides missing weight.
4. Evaluate long calls for bullish or long puts for bearish from the eligible universe.
5. Reject invalid contracts, stale/unsynchronized quotes, excessive spread, insufficient observed depth, inadequate cash, or infeasible costs/risk.
6. Rank surviving options using explicitly logged liquidity/cost/moneyness rules. Do not report a ranking score as probability or forecast profit.
7. Create at most one Bank Nifty paper position, initially one lot. Limit strategy trade attempts to three per session; no minimum trade count.

Each candidate decision includes time, instrument/expiry/quantity, setup, ENTER/WATCH/SKIP/INSUFFICIENT_DATA, source quote age, assumed fill, estimated round-trip fees, spread/slippage assumptions, stop basis, reason codes and strategy/config version. A price target is a scenario, not predicted realized P&L. If structural risk cannot be credibly mapped to option cash risk, label it unknown and reject budget-qualified auto entry rather than invent a risk estimate.

For open paper positions output HOLD, WATCH_EXIT or EXIT with net liquidation estimate and the precise condition observed. WATCH_EXIT applies only to soft thesis weakening. A configured cash stop, invalidation or session exit triggers EXIT without waiting for soft confirmation. Use the baseline two completed closes against futures VWAP plus adverse bank confirmation for thesis failure, 20 minutes without 0.5R underlying progress for time exit, and a 15:15 session exit. Trailing remains optional after core exit correctness works; any enabled trail can tighten only.

Previous preferences are retained as configurable experimental inputs: roughly ₹200 gross loss trigger and approximately ₹500 net profit milestone. These are not guaranteed fill outcomes or a fixed ₹200 fee assumption. Define gross price risk, fees and net loss separately; reject infeasible one-lot setups. Daily real-execution loss budget remains unresolved. For this paper session, use a labelled provisional ₹1,200 net session pause threshold; this is simulation configuration, not a user's approved live-money limit. Stop new entries at that threshold and request closure of an open paper position, reporting any overrun. Simulated capital starts at the baseline ₹5 lakh; it does not justify larger quantity.

## Evaluation cadence

| Work | Initial target |
| --- | --- |
| Ingest/cache/record | Every received event; preserve duplicates/gaps according to documented rules |
| Entry setup | Completed one-minute bars only |
| Candidate quotes/ranking | Coalesce relevant changes; approximately every 250–500 ms when dirty |
| Open-position quote/cash-stop policy | Every relevant quote; priority over scanning |
| Freshness and time exits | Approximately 250 ms timer, even when no price update arrives |
| UI delivery | Approximately 250–500 ms batches plus immediate critical state changes |

Targets are engineering choices to benchmark, not measured guarantees or broker/exchange latency. Initial benchmark goal: p95 internal evaluation below 50 ms for 35 candidates on the actual computer. Report feed-to-decision age, queue depth and dropped/coalesced events separately. A heartbeat proves transport activity, not fresh bid/ask. Slow UI must not slow recording or position monitoring. Recorder pressure that compromises event completeness must raise degraded state and inhibit new entries.

## Honest paper execution

- Create fills only from a fresh quote received after the intent and configured execution delay. Begin with a declared 250 ms simulated delay and stress at 1 second; neither is an observed broker delay.
- For a long option, buy at observed ask plus adverse slippage; sell at bid minus adverse slippage. Round adversely to tick size. Do not deduct spread again when fills already cross bid/ask.
- Require sufficient relevant observed depth for a whole lot; for the first model, wait/reject when unavailable rather than assume unlimited volume or invent partial fills. Cancel pending entries after a declared timeout; keep exit intents pending/visible until an executable fresh quote arrives.
- Gross unrealized P&L uses an indicative sell-side liquidation mark. Net liquidation estimate deducts actual simulated entry fees and projected exit fees. Realized net P&L uses both simulated fills and their fees.
- On feed loss, block entries and show stale/unpriced open-position risk. Never fill at an old favorable quote or claim a missing stop was executed. On recovery, process the pending exit against a later fresh quote and report overruns.
- Persist open state, order IDs and intent IDs. Restart/reconnect must not create duplicate positions/fills. Restart begins paused until feed and state recover.
- Recorded Monday quotes permit later deterministic replay under the same declared fill model; candle-only weekend replay cannot validate subsecond fills. Even observed depth does not prove actual fill probability.

The paper broker is our local simulator. Do not call any real broker order, GTT, modify or cancel endpoint. Omit the real execution adapter entirely and test the broker HTTP boundary. Upstox execution and actual account monitoring stay deferred; the paper fee profile is explicitly identified and need not claim Upstox equivalence.

## Schedule and checkpoint evidence

| Time | Work | Exit condition |
| --- | --- | --- |
| Saturday 10 Oct, first 1–2 hours | Inspect repository/tooling; verify account/API; login + profile + instruments + snapshots + historical smoke tests | Data access proven or live blocker explicitly recorded |
| Saturday, next 6–8 hours | Backend/UI skeleton, typed models, feed/cache/recorder, dashboard panels, fixture transport | UI renders the shared contract; closed/stale/synthetic labels correct; weekend connectivity checked |
| Sunday 11 Oct, first 5–6 hours | Bars/analytics, setup detector, candidate gates, reason records | Hand-checked decisions; missing data cannot trigger qualified entry |
| Sunday, next 5–7 hours | Paper lifecycle, fees, exits, persistence, position dashboard and report | Delayed entry → hold → exit → reconciled net ledger works |
| Sunday by 20:00 | End-to-end rehearsal, failure injection, restart and benchmark | Core checks pass; feature freeze; Monday login/run instructions ready |
| Monday 12 Oct, 08:30–09:10 | New login, refresh instruments, verify weights/config, start recording, verify system clock | App armed for PAPER mode before opening range |
| Monday 09:15–09:30 | Observe/record opening range and data coverage | No premature breakout entries; warm-up status visible |
| Monday 09:30–11:30 | Run strategy auto-paper selection with active position monitoring | Accept or reject candidates with reasons; zero trades permitted |
| Monday 11:30–15:15 | Stop new strategy entries; monitor exits, health and recorder | Position lifecycle completes or unfilled exits explicitly reported |
| Monday 15:15–15:30 | Session-close paper policy and complete recording | No hidden open state; gap/exit limitations logged |
| Monday evening | Session review, accounting/latency report, essential fixes, recorded replay | Deliver run evidence and unresolved defects by end of day |

If not started before 09:15, do not fabricate the opening range. Use verified complete current-session history if available with declared approximation and coverage; otherwise run observation/manual diagnostic paper testing and mark strategy test incomplete. Changes to strategy/risk during Monday create a new run/config version; do not mix outcomes under one policy label.

## Verification and completion

Targeted tests: completed-bar timing and future isolation; deterministic replay; valid whole lots; cash/risk/fee reconciliation; buy-at-ask/sell-at-bid fill timing; no duplicated intents; spread/slippage accounting; stop overrun; stale/gap entry block; restart recovery; absence of real order requests. One browser E2E follows feed → candidate → paper fill → position → exit → journal. Exercise disconnect/reconnect and process restart independently of browser refresh.

Monday complete means:

1. Real session data shown and recorded with field coverage and source times.
2. Engine recommendations/rejections auditable, even if no strategy entry qualifies.
3. Paper lifecycle demonstrated with automated policy where signals exist; otherwise a separately labelled, liquidity/risk-checked diagnostic paper trade or recorded fixture proves the software path without claiming a strategy trade occurred.
4. Net ledger reconciles and operational gaps/stop breaches are disclosed.
5. UI state stays honest through stale feed, reconnect and restart.
6. Broker-order call count is zero, with a boundary test supporting that claim.
7. Session report and reproducible start/run instructions delivered.

One session proves integration and gives preliminary behavior evidence. It does not validate a profitable policy. Continue historical/unseen evaluation from the baseline after this milestone.

## Deferred to protect the deadline

Nifty/Sensex strategy execution, spreads/condors, cross-index hedges, partial exits, Greeks/IV modeling, exhaustive PCR/MaxPain, automated FII/DII/news ingestion, ML/LLM prediction, September-wide data acquisition, cloud infrastructure, actual positions, and real broker execution. Optional widgets must not block core feed, fill, risk or accounting behavior. Missing feature values display unavailable; do not synthesize market facts.

## Verified reference links

- API plan: https://support.zerodha.com/category/trading-and-markets/general-kite/kite-api/articles/historical-data-and-live-market-data-payment-plan
- Authentication/session expiry: https://kite.trade/docs/connect/v3/user/
- Streaming/depth: https://kite.trade/docs/connect/v3/websocket/
- Instruments/snapshots: https://kite.trade/docs/connect/v3/market-quotes/
- Historical warm-up: https://kite.trade/docs/connect/v3/historical/
- Fee schedule: https://zerodha.com/charges/
- Exchange timings/holidays: https://www.nseindia.com/resources/exchange-communication-holidays?apiversion=v2

References checked 10 October 2026. Validate authenticated coverage on the actual account. Monday 12 October is not listed as a scheduled NSE equity holiday in the published 2026 calendar; monitor any exceptional closure notice.
