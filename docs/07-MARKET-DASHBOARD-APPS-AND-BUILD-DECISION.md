# AutoTrader — Existing Dashboard Apps and Build Decision

Checked: 9 October 2026 | Scope: public first-party documentation; no authenticated trial, subscription or broker connection

## Finding

Existing applications provide useful references for charts, option chains, payoff tools and portfolio views. Jeevan subsequently confirmed that we will build our own dashboard for personal trading and frontend portfolio evidence. The earlier build-versus-use choice is settled; the comparison now informs our design.

The closest initial comparison is Sensibull for option/position analysis plus NiftyTrader for Bank Nifty contribution, with Upstox Pro Web as the actual-account baseline. GoCharting is a possible more integrated charting/options workspace. Opstra is a research/analytics alternative. This prioritization is our judgment based on the documented capabilities below, not an independent performance ranking.

No reviewed product was documented as providing our entire combination of weighted bank-thesis tracking, per-contract freshness, ₹200 gross risk feasibility, approximately ₹500 net objectives, 20–35 candidate evaluation, recording/replay lineage and our own future execution policy. Missing documentation is not proof a feature does not exist; these are trial questions.

## Application shortlist

| Application | Documented capabilities relevant to us | Role to test | Unresolved fit |
| --- | --- | --- | --- |
| [Sensibull](https://sensibull.com/) | Option chains, Greeks/IV/OI, strategy analysis, position grouping, draft portfolios with notes; Zerodha and Upstox login options | First option/position cockpit | Weighted bank confirmation beside positions, explicit exit-net marking assumptions, custom risk alerts, far-strike coverage and export/replay |
| [NiftyTrader](https://www.niftytrader.in/) | Bank Nifty contributor page with weight/contribution columns; option chain and options tools; premium screeners/alerts | First constituent/contribution companion | Signed-in live contributors, effective membership/weights, synchronized chart/bank inputs, refresh cadence and broker-position workflow |
| [Upstox Pro Web](https://upstox.com/upstox-pro-web-online-trading/?hl=en_IN) | Tick-based charts, watchlists, option chain, Greeks, OI, PCR/MaxPain and trading tools | Baseline for actual account and manual execution | Dedicated weighted bank panel, before/after-entry thesis timeline and research-grade recording |
| [GoCharting](https://gocharting.com/features/options) | Charting, option analytics, strategy/payoff tools, multi-strike charts and selected-broker integrations | Potential chart/options workspace | Current connector entitlement, actual account sync, bank-contribution features, source freshness and custom rules |
| [Opstra](https://www.definedgesecurities.com/products/opstra/) | Options/futures dashboards, strategy builder, OI/PCR tools, heatmaps and research/simulation features | Options research and analytics alternative | Data timing on chosen product/plan, actual broker sync and whether relevant intraday records can be exported |
| [TradingView](https://www.tradingview.com/) | Linked chart layouts, watchlist alerts and economic calendar filters | Chart/event companion | Our complete Indian options/account/bank workflow was not established by the reviewed chart documentation |

These are advertised capabilities, not proof of correctness, freshness or profitability. A heatmap is not automatically our weighted contributor/VWAP panel. A payoff chart is not automatically an executable 15-minute net-P&L estimate. A simulator is not automatically a downloadable dataset or deterministic engine replay.

Sensibull's coverage help specifically lists strikes far away from the current price as unsupported. That deserves a trial check because our candidate universe should not be permanently restricted to ATM.

## Cost and timing observations

| Product | What the first-party source says | What to confirm before relying on it |
| --- | --- | --- |
| Sensibull | Help page modified 23 July 2026 says free for Zerodha users; free and Pro tiers for other brokers. A separate help page distinguishes broker-login real-time chain prices from a one-minute Google-login delay. | Features and total price for the actual execution account; position-analysis coverage. Zerodha access does not mean Upstox positions are synchronized in that session. |
| NiftyTrader | Prime pricing currently advertises ₹5,390/year inclusive of GST, shown as roughly ₹449/month equivalent. Public contributors render demo/empty values until sign-in. Option-chain copy inconsistently describes seconds versus minutes. | Billing amount and plan, signed-in refresh behaviour, underlying data timestamp and actual bank coverage. Do not infer measured latency from marketing text. |
| GoCharting | Pricing page lists free/paid tiers with different chart and alert limits; options feature page advertises basic and premium features. | Exact Indian-market feature/data entitlements and current price; some public descriptions differ on what is free. |
| Opstra | Search-indexed pricing FAQ describes one-minute data plus computation delay, OI every three minutes, and 15-minute updates for various basic-plan tools. The directly opened pricing page did not expose its full table. | Current latency and entitlement on the chosen Opstra site/plan. Treat the indexed FAQ as a disclosed timing caveat, not a measured current latency. |
| iCharts, secondary option | Subscription page advertises ₹599/month including tax for delayed tools: price/volume updates about 90 seconds, OI about 270 seconds; live tools are separate via broker login. | Actual broker-login access and live-tool timing. Delayed subscription figures must not be mistaken for a live feed. |

No subscription has been purchased. Prices are observations from public pages on the check date, not a promise about checkout. Precise account-specific costs can be checked by the user during a trial.

## Requirements that remain to be demonstrated

| Our requirement | Closest starting point | Required demonstration |
| --- | --- | --- |
| Weighted banks explain Bank Nifty | NiftyTrader contributors; heatmaps as context elsewhere | Valid membership/weights, contribution method and quote age |
| Option comparison and strategy scenarios | Sensibull, GoCharting, Opstra | Broader eligible strike coverage, spread/depth, intraday scenarios versus expiry payoff |
| Actual position monitoring | Upstox; Sensibull with matching broker session | Known quantity/fills, read-only truth, exit-net estimate and fee assumptions |
| Risk preferences | Option strategy/position tools | Requested small rupee thresholds, lot infeasibility and informational alerts without claiming guaranteed exits |
| Thesis change since entry | Draft notes, alerts and chart views | A stored entry snapshot and inspectable changes in bank/VWAP/spread conditions |
| Upcoming event context | TradingView calendar; product event tools | Relevant announcements/speeches, IST time and source/as-of |
| Replay and future engine | Product simulators plus our recorder | Raw/normalized inputs, availability timestamps, data rights and deterministic outputs |

Use these references to study useful patterns and friction, including screen switching. Our own UI remains the deliverable. Do not assign a coverage or usability-improvement percentage without performing the relevant tasks.

## Optional reference evaluation for design inspiration

This is optional design-reference work, not a prerequisite to custom implementation and not work already performed:

1. Start with existing free/account access: Upstox Pro Web, Sensibull with the appropriate broker, and the NiftyTrader contributor view if accessible.
2. Use a fixture or known closed trade for account/fee checks; do not place a new trade solely to test a dashboard.
3. Rehearse identifying the top bank contributors, comparing two option contracts, locating position risk/P&L and finding the next important event.
4. During a normal market session compare displayed timestamps and quote/depth observations against the broker view. Record screen-switching time and misleading/missing fields.
5. Record findings against 06-DASHBOARD-SPECIFICATION.md, including missing evidence. Avoid a paid annual commitment before the core workflow is demonstrated.
6. Record what to adapt into our dashboard and what to omit. The custom implementation is confirmed; reference review must not reopen that decision or delay the first useful mock.

Our custom dashboard, recorder and domain model support the later engine. An application subscription is not equivalent to licensed API data, expired-option coverage or tick-history export. Do not plan to scrape product screens as the trading system's feed; use documented permitted data routes.

## Plan impact

D00 is a focused inspiration review, not a build-versus-buy gate. Build our own React/TypeScript dashboard on the shared .NET data/analytics/risk foundation. Preserve the single-user workflow and add frontend portfolio evidence: responsive interaction, accessibility, stable live updates, resilient states, measured performance and E2E checks. Maintain independent broker-state reconciliation and eventual execution gates.

## Primary sources

- Sensibull: https://sensibull.com/
- Sensibull pricing help: https://sensibull.freshdesk.com/support/solutions/articles/43000586718-what-are-the-charges-of-sensibull-
- Sensibull timing/units: https://sensibull.freshdesk.com/support/solutions/articles/43000733665-why-is-the-volume-and-open-interest-on-sensibull-s-option-chain-different-from-that-on-nse-
- Sensibull coverage limitation, including far-away strikes: https://sensibull.freshdesk.com/support/solutions/articles/43000622281-can-i-analyse-all-trades-and-positions-
- NiftyTrader contributors: https://www.niftytrader.in/nifty-bank-contributors
- NiftyTrader chain: https://www.niftytrader.in/nse-option-chain
- NiftyTrader plans: https://www.niftytrader.in/primeplans
- Upstox Pro Web: https://upstox.com/upstox-pro-web-online-trading/?hl=en_IN
- Upstox option chain: https://upstox.com/option-chain/
- GoCharting options: https://gocharting.com/features/options
- GoCharting plans: https://gocharting.com/pricing
- Opstra product: https://www.definedgesecurities.com/products/opstra/
- Opstra guide: https://www.definedgesecurities.com/user-manuals/opstra-user-guide/
- Opstra pricing/FAQ, indexed content caveat above: https://opstra.definedge.com/pricing
- iCharts delayed-tool plans: https://www.icharts.in/opt/SubscriptionDetails.php
- TradingView chart layouts: https://www.tradingview.com/support/solutions/43000629990-leveraging-multi-chart-layouts-in-your-analysis/
- TradingView calendar: https://www.tradingview.com/support/solutions/43000759911-economic-calendar-track-all-major-market-events/

## Document clarification

The user also mentioned an NDA document. No matching file was located by NDA/non-disclosure/confidential title searches, and no NDA is among the supplied files. Markdown project documents were updated. A separate legal agreement has not been created or modified; the exact intended file is needed to assess that request.
