# AutoTrader — Manual Trading Dashboard Specification

Updated: 9 October 2026 | Owner: Jeevan | Design proposal informed by primary product/API documentation

## Purpose and priorities

The dashboard is the first product. Help one user answer: What is happening? Which banks support it? Which option can express my view at an acceptable cost? What could invalidate the trade? What is my position worth if I exit? What changed since entry?

Bank Nifty is the primary workspace. Nifty 50, Sensex and India VIX provide context when available. Start with long calls/puts; add debit spreads later. Latest capital assumption is ₹5 lakh. Requested ₹200 gross loss and roughly ₹500 net profit are configurable preferences, not guaranteed fills or fixed fees. Keep actual fees, gross triggers, net limits and planned structural risk separate. Daily loss budget is not yet agreed.

All numerical examples in this specification are illustrative interface copy, not current quotes, market recommendations or performance claims. Recommendations below are our design judgment; product documentation supports the cited patterns, not evidence that they produce trading profits.

## Suggested main screen

| Region | Default content | Decision supported |
| --- | --- | --- |
| Persistent header | LIVE / REPLAY / PAPER / SHADOW mode; IST clock; selected expiry; feed/quote age; actual/paper account label; daily net result and trade attempts | Can I trust this view and what mode am I in? |
| Market strip | Bank Nifty change, futures basis/VWAP distance, Nifty/Sensex context, VIX; each with as-of/source | What is the immediate context? |
| Main chart | Bank Nifty or selected futures; opening range, prior high/low/close, VWAP only on traded instruments; event markers | Where is price relative to important levels? |
| Bank panel | Weight, return, estimated contribution, own-VWAP distance, momentum and synchronized mini-trends | Is the move broad or driven by a few heavyweights? |
| Options table | 20–35 configurable eligible candidates; bid/ask, spread cost, depth, volume/OI, available IV/delta and freshness | Which contracts are worth comparing? |
| Trade-plan drawer | Selected contract, whole lots, entry assumption, fees, cash needed, scenarios, stop plan, invalidation and note | Does my intended trade fit my constraints? |
| Position panel | Real broker quantity/fills, estimated exit-net P&L, remaining planned risk, entry thesis and what changed | Should I review my current position? |
| Event/alert rail | Upcoming high-impact events, condition changes, stale data and acknowledged alerts | What needs my attention now? |

Before entry, emphasize chart, banks and option comparison. During an open position, expand the position monitor and thesis-change timeline. Daily review has a separate journal/replay screen. Keep context available without making every metric equally prominent.

## Improvements tailored to our concerns

### 1. Bank contribution rather than counting green cells

Use current verified constituent membership and weights. Display both unweighted breadth and weighted participation. For a bank i, a quick estimated return contribution is weight_i × return_i using explicitly aligned reference times. It is not an exact index-point decomposition unless the index methodology/divisor is implemented.

Show top positive/negative contributors, heavyweight disagreement, and the fraction of valid weight above/below each stock's own VWAP. Include coverage: for example, “91% of constituent weight has fresh inputs.” Never silently renormalize missing weight and imply full coverage. Do not compute VWAP for the cash index, which has no traded volume. Label candle-derived VWAP as approximate.

### 2. Separate market facts from judgment

Use factual statements such as “futures above VWAP; bank participation mixed.” Any trend/setup label must name its rule, horizon and version. If inputs conflict, show MIXED. Show unavailable fields as unavailable. Scores are not win probabilities; an unvalidated score must not be presented as “80% chance of profit.”

### 3. Compare costs in rupees

For quantity Q, show spread = ask − bid and immediate quoted spread cost = spread × Q. Compare that with the user's intended net profit and loss budget. Do not count spread again inside an all-in slippage assumption.

Show estimated entry at ask for a purchase and exit at bid, plus depth adequacy and the uncertainty of filling the entire quantity. The displayed top price is not a guarantee. Keep LTP alongside it as reference, not the sole executable mark.

### 4. Show exit-net P&L and thesis failure together

For a long option, a simple indicative exit-net mark is (current bid − actual average entry) × quantity − paid entry charges − estimated exit charges. For short legs use ask to mark close; group multi-leg exposure once spreads are supported. Display realized and unrealized results separately, with source/time and any account reconciliation warning.

A “loss threshold reached” alert is informational in manual mode. It does not claim an exit happened. Broker fills confirm the result. Show whether the user's loss budget is already largely consumed by spread/fees, and whether one lot makes it infeasible.

Show daily realized/unrealized P&L, estimated charges, used cash/margin and trade-attempt count. Display “daily loss budget not configured” until it is agreed; do not invent a remaining allowance. Surface an existing position on the selected index before planning another. The earlier three-attempt daily preference is configurable, not a target that must be reached.

### 5. Explain what changed since entry

Persist a timestamped snapshot when the user journals a plan and when actual entry fills are observed. Compare bank participation, VWAP relation, option IV/spread, quote quality and event proximity. Display reasons such as “two heavyweight banks changed direction” or “spread widened.” Attach the underlying values so each reason is inspectable.

### 6. Pre-expiry scenarios as well as expiry payoff

Expiry payoff is insufficient for a 15–30 minute hold. Later add favorable/flat/adverse underlying moves with time and IV assumptions and cost ranges. These are model scenarios, not promised outcomes. If IV/Greeks/valuation inputs are missing, show the option-price break-even and risk budget first; do not fabricate a scenario surface.

For spreads, show each leg's quote age, depth, cash/margin estimate and asynchronous-fill risk. Maximum expiry loss is different from a user's intended intraday stop.

### 7. Event countdown with provenance

Support a verified/manual event list first: RBI announcements/speeches, major Indian releases, relevant US releases, known government announcements, key bank earnings and contract expiry. Show IST time, source, verification time, scheduled versus unscheduled status and a configurable review window. Never imply a complete event calendar or a guaranteed directional effect.

FII/DII observations must display trade date, publication/as-of time and provisional/final status when known. During market hours, yesterday's published number must remain visibly yesterday's. Global indices, crude, FX and yields are optional context with independent licensed sources and timestamps; they are not assumed to be included in Kite.

### 8. Alerts that require attention without flooding the user

Initially support level crosses, spread/risk threshold changes, event reminders and stale/disconnected feeds. Deduplicate alerts for the same episode and require reset/hysteresis before retriggering. Let the user acknowledge, snooze or disable a rule. Sounds are optional and higher-severity alerts have distinct text/icons. Alerts must distinguish observed threshold, estimated risk and actual broker action.

### 9. Stable comparison and linked navigation

Pin selected candidates and keep rows stable during interaction; make re-ranking explicit. Freeze a timestamped comparison view without stopping data recording. A selected option opens its chart and plan without losing the bank/index context. Provide 1-minute and 5-minute views with synchronized time windows where practical.

Use short numbers, tabular digits, sticky labels and clearly named units. Use red/green plus text/arrows rather than colour alone. Keep advanced columns such as Greeks, PCR scopes and diagnostics behind a drawer. Desktop is the first full workspace; mobile focuses on positions, alerts and a compact market summary.

### 10. Journal and review built into the workflow

Record considered, taken and skipped opportunities with reasons and as-of snapshots. A user can log a hypothesis before entering. Reconcile actual fills rather than recording a quote as a real trade. Paper trades, actual trades and shadow recommendations remain distinct. Review costs, adverse/favourable movement where available, holding time and why a trade ended.

Later, the same dashboard loads recorded data in REPLAY mode and overlays engine decisions. Manual decisions can inform research, but evaluating only the user's chosen winners creates selection bias. Keep rejected/no-trade periods and unseen evaluation.

## Field availability and honest display

| Field | Proposed acquisition | Display rule/dependency |
| --- | --- | --- |
| Live index/futures/bank/option prices | Kite streaming | Verify subscriptions; show per-instrument freshness and receipt time |
| Option depth and OI | Kite full-mode tradable-instrument packets | Indices have a different packet shape; order quantities are not executed trade flow |
| IV and Greeks | Optional Upstox enrichment or a documented local model | Provider/model estimate; provider-specific age; no silent mixing of contracts |
| PCR | Explicitly aggregate available option OI | State expiry, strike universe, OI/volume type and coverage; avoid mixing per-strike and whole-chain ratios |
| MaxPain | Optional calculation on a specified expiry's complete OI universe | Context metric with as-of/coverage, not a reliable target or hard entry rule |
| Stock OI | Stock futures where subscribed | Equity cash stocks have no OI; identify futures expiry/basis |
| Actual positions/fills/funds | Upstox read-only APIs/portfolio stream | Account truth from broker; reconcile snapshots after reconnect |
| Events/news/macro | Verified official sources or manual entry | Separate sources and update rates; label omissions |
| Constituent weights | Official current/historical index definitions | Effective date; never hardcode 12/14/15 banks or old weights |

Mixed-provider quotes need a canonical contract key: exchange, underlying, expiry, strike and option type plus validity/lot metadata. Reject mismatched mappings. Retain which provider produced each metric.

## Custom UI and inspiration review

Jeevan confirmed the custom dashboard for two purposes: a substantial UI portfolio project and an interface tailored to personal trading concerns. The market comparison in 07-MARKET-DASHBOARD-APPS-AND-BUILD-DECISION.md now serves as design-reference research. D00 records useful patterns and how we adapt them; it does not decide whether to build or block D0.

Own the layout, panel priorities, interactions and domain behaviour. Suitable chart/table libraries are acceptable. Start with our manual workflow and extend the same interface into replay, shadow recommendations and execution.

## Frontend portfolio evidence

| Capability | What our UI should demonstrate | Evidence to retain |
| --- | --- | --- |
| Component design | Reusable typed chart, quote, table, status and plan components | Component structure and clear view-model contracts |
| Real-time interaction | Stable rows, linked selection and independent recording/display cadence | Profiling under realistic update load; measured render/update behaviour |
| Responsive UX | Full desktop workspace and focused mobile positions/alerts | Screenshots and task rehearsal across viewport sizes |
| Accessibility | Keyboard use, focus behaviour, readable contrast and non-colour status cues | Checks and verified key workflows |
| Resilience | Loading, unavailable, stale, disconnect and reconnect states | Reproducible fixtures and walkthrough |
| Verification | Compare options, save a plan, acknowledge alerts and recover connection | Meaningful frontend/E2E checks |
| Demo storytelling | Explain the user problem and show our decisions | Short walkthrough with clearly labelled fixtures; no fabricated live results |

Measure performance and usability before publishing numerical claims. Advanced polish should support the manual workflow, not delay the first usable dashboard.

## MVP and later enhancements

| Release | Include | Completion check |
| --- | --- | --- |
| D0: usable mock | Linked chart/table/drawer, sample states, clear synthetic label | User can find a bank, compare two options and journal a plan |
| D1: live manual | Live overview, bank panel, basic option quotes/costs, freshness, recording, manual event list and journal | Record a real session and demonstrate gap/stale-data behaviour |
| D2: position assistance | Read-only broker sync, exit-net estimate, thesis snapshots and deduplicated alerts | Reconcile a known fill/account example; alerts do not imply execution |
| D3: richer context | Credible Greeks/IV, intraday scenarios, scoped OI/PCR views and context ingestion | Missing/delayed fields remain visible; calculations hand-check |
| D4: replay/shadow | Date/clock controls, research decisions/reasons and manual-versus-engine comparison | Replay timing and research evaluation gates pass |
| D5: execution | Orders and automated protective actions under a separate reviewed execution design | Risk configuration, lifecycle and failure recovery verified |

Do not delay D1 to implement every option statistic, global market panel, ML forecast or basket combination. Keep an expandable candidate universe; ATM-only should not become a permanent UI constraint.

## Usability verification for one user

Use a short rehearsal with synthetic fixtures, then a recorded real session. Measure time to identify the main bank contributors, compare two contracts, locate a position's exit-net estimate, and discover a stale feed. Record mistakes and unnecessary navigation. The first acceptance criterion is that freshness, mode and actual-versus-paper state are understood correctly; compare task times against the user's existing broker workflow before making speed claims.

Test disconnected/stale states, missing weights, absent Greeks, row changes while selecting a contract, browser reconnect, partial fills and expiry switching. Keyboard access and readable contrast are part of the initial design.

## Internet references checked on 9 October 2026

These are pattern and capability references. They do not imply a partnership, free entitlement, scraping permission, or a need to adopt those products.

- [TradingView multi-chart layouts](https://www.tradingview.com/support/solutions/43000629990-leveraging-multi-chart-layouts-in-your-analysis/): synchronized chart settings inform our linked workspace.
- [TradingView watchlist alerts](https://www.tradingview.com/support/solutions/43000739708-watchlist-alerts-your-trading-edge/): condition tracking across symbols informs our alert design.
- [TradingView economic calendar](https://www.tradingview.com/support/solutions/43000759911-economic-calendar-track-all-major-market-events/): time zone and event-importance filters inform the event rail.
- [Sensibull](https://sensibull.com/) and [positions on option chain](https://blog.sensibull.com/2023/05/05/analyse-live-positions-on-option-chain/): strategy analysis, journal-style drafts and positions within option context inform our plan/position view.
- [NN/g dashboard design](https://www.nngroup.com/articles/dashboards-preattentive/?lm=contrast-charts&pt=youtubevideo), [progressive disclosure](https://www.nngroup.com/articles/progressive-disclosure/) and [data tables](https://www.nngroup.com/articles/data-tables/): fast scanning, expandable detail and side-by-side comparison inform UI layout.
- [Kite streaming](https://kite.trade/docs/connect/v3/websocket/) and [market quotes](https://kite.trade/docs/connect/v3/market-quotes/): source capabilities and packet distinctions.
- [Upstox option chain](https://upstox.com/developer/api-documentation/get-pc-option-chain/), [market feed V3](https://upstox.com/developer/api-documentation/v3/get-market-data-feed/) and [portfolio stream](https://upstox.com/developer/api-documentation/get-portfolio-stream-feed/): optional enrichment and account-state capabilities; access untested.
- [RBI press releases](https://www.rbi.org.in/Scripts/BS_PressReleaseDisplay.aspx): one official source to verify monetary-policy events; a feed integration is not implemented.

See [05-SYSTEM-COMPONENTS-AND-PROJECTS.md](05-SYSTEM-COMPONENTS-AND-PROJECTS.md) for ownership and reuse of each calculation.
