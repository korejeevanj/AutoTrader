# Scope and Decisions
Updated: 9 October 2026

## Agreed direction
- Personal Indian intraday trading system, latest capital assumption ₹5 lakh.
- Backend C#/.NET; frontend React with TypeScript.
- Zerodha Kite remains the planned live market-data source. Upstox remains the planned execution and actual account-state source.
- Upstox can also be used for historical research data, particularly expired derivatives.
- Build the manual-trading dashboard first with its live data foundation and recorder. Add selection research/replay, live shadow recommendations and automated execution afterward. The initial product operates locally, with no cloud infrastructure required.
- During the dashboard phase, orders remain manual in the broker app. Read-only account monitoring and informational alerts are useful without an automated strategy.
- Share analytics/risk calculations between the dashboard and future engine; do not duplicate financial logic in the UI.
- Start with Bank Nifty. Preserve market/contract abstractions for Nifty 50 and Sensex without implementing all three immediately.
- One active position per index; previous working limit of three trade attempts across the day. Keep this configurable and explicit.
- Zero trades is a valid result when evidence, liquidity or risk requirements fail.

## User risk preferences
Previous preference: exit around ₹200 gross loss, approximately ₹400 loss including charges in the examples; approximately ₹500 net profit is a milestone, not a compulsory ceiling. Hold a winner only while the policy remains valid.

These rupee thresholds are requested operating preferences, not guaranteed outcomes. Costs vary by orders and quantity. A stop trigger cannot guarantee its fill price. If realistic noise, spread and lot size make a candidate incompatible with the requested budget, reject it. Do not silently widen the loss budget.

MVP configuration stores gross stop threshold, fee assumptions and net risk budget separately. Simulated exits can exceed their triggers under gaps or adverse fills; report these breaches.

The daily loss budget has not been agreed. Keep it unresolved for live execution; retrospective simulations can run with documented experimental limits.

## Dashboard scope and stage boundary

Primary manual workspace: Bank Nifty context, weighted bank contribution, option comparison, spread/fee feasibility, trade-plan journal, quote freshness and alerts. Read-only positions are added once broker access works. Nifty/Sensex/VIX context can appear without implementing their selection strategies. Automated exits remain later execution work.

See 05-SYSTEM-COMPONENTS-AND-PROJECTS.md and 06-DASHBOARD-SPECIFICATION.md. The following strategy settings apply to the research stage, not a prerequisite for dashboard delivery.

## First strategy hypothesis
Bank Nifty opening-range breakout confirmed by futures VWAP and weighted constituent-bank participation. Test direction from underlying/futures; then evaluate eligible option expressions.

The following are starting experimental settings, not validated optimal rules:
| Setting | Initial hypothesis |
| --- | --- |
| Opening range | 09:15–09:30 Asia/Kolkata |
| Signal | Completed 1-minute futures bar breaks the defined futures opening range, in agreement with futures VWAP |
| Bank confirmation | At least 65% of point-in-time constituent weight above each bank's own VWAP for bullish; below for bearish |
| Entry window | 09:30–11:30 |
| Option candidates | Configurable liquid-strike universe; absolute delta 0.45–0.70 when a credible estimate is available |
| Expiry policy | Exclude expiry-day contracts in the first experiment |
| Initial structural stop | Breakout candle low/high plus documented volatility buffer |
| Trailing hypothesis | After 1R progress, last three completed 1-minute lows/highs; tighten only |
| Thesis failure | Two completed closes on wrong side of futures VWAP plus bank confirmation against |
| Time exit | After 20 minutes without 0.5R progress |
| Session exit | 15:15 |
| Cash risk | Requested loss threshold overrides structural holding logic; feasibility checked before acceptance |

Define R as initial planned price risk in the underlying for signal management; record option cash risk separately. The option can lose money despite the underlying moving correctly because of IV, timing, spread and costs.

Candle-derived VWAP and Greeks are estimated features; identify their method and provenance. Do not label them exact historical quote-derived values.

## Selection gates
Reject or mark insufficient evidence when:
- Required data is missing, stale, unsynchronized or invalid.
- A contract was not eligible at that historical time.
- Signal is conflicting or absent.
- Cash, quantity or risk checks fail.
- Conservative costs consume the plausible opportunity.
- Quote liquidity is unknown for an execution-qualified decision.

For candle-only research, distinguish hypothetical candidates from execution-qualified recommendations. Do not invent bid/ask depth.

## Deferred work
Machine learning, non-directional structures, exhaustive multi-leg combinations, cross-index hedges, cloud deployment, live execution and broker UI automation are outside the first implementation milestone.

A separate later execution module must handle acknowledgements, partial fills, exits, failed legs and reconciliation. Batch submission is not an atomic basket fill.

## Confirmed custom-dashboard decision

On 9 October 2026 Jeevan confirmed that we will build our own dashboard. It serves personal manual trading first and provides a portfolio project demonstrating frontend/UI engineering. We control panel relevance, layouts, option comparison, risk information and the evolution into replay/shadow/automated trading.

Sensibull, NiftyTrader, GoCharting, Opstra and the broker workspace are design references. The earlier open choice between an external dashboard, a small companion and a full custom UI is superseded. D00 now gathers inspiration rather than deciding whether to build. Appropriate chart/table libraries remain compatible with owning the dashboard.

Portfolio evidence should include reusable typed components, responsive layouts, accessibility, live-update behaviour, honest loading/error/stale states, frontend performance measurements and meaningful end-to-end checks. A shareable demo uses clearly labelled fixtures or permitted sanitized recorded data; demo results are not trading-performance evidence.

## Open decisions
1. Confirm historical access and actual coverage for September contracts.
2. Select candidate strikes using a documented point-in-time rule.
3. Agree daily risk budget before live orders.
4. Choose additional chronological evaluation periods after coverage is known.
5. Decide whether paid historical quote/order data is needed for small-profit execution claims.
