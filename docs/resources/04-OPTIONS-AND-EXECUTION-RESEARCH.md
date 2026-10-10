# Options, Costs and Position Monitoring — Research Summary

Compiled: 9 October 2026 | Related discussions: 28–30 September and 1 October

## Price, profitability and execution

A favourable index move does not guarantee option profit. Time elapsed, volatility, spread, quantity, fees and actual fills all matter. Separate expiry payoff from a position's price before expiry and from realized broker P&L.

For purchases compare ask entry and bid exit; for closing shorts use ask. Show quoted depth and estimated costs rather than treating LTP as an immediately executable price. Market orders can fill away from the displayed mark; limit orders may remain unfilled or partially filled. Submission of several legs does not create an atomic basket.

## User preferences to preserve

Requested gross loss trigger around ₹200; approximately ₹500 net profit as a typical milestone; hold a winner while the policy remains valid; reject candidates whose lot/spread/cost makes the budget infeasible. Latest capital assumption is ₹5 lakh. The daily loss budget remains unresolved.

Historical examples of roughly ₹200 in charges are not a universal fee schedule. Store actual trade-date order counts, quantity, sides and charge parameters. A trigger is different from a guaranteed final loss. Report adverse fills and breaches explicitly.

## OI, PCR, IV and order-book context

Keep OI change, price change and volume as separate facts. OI by itself does not reveal a participant's intent or guarantee support/resistance. Define PCR's expiry, universe and whether it uses OI or volume. MaxPain is an optional context calculation, not a guaranteed settlement target. Label provider/model Greeks and IV with provenance.

Displayed pending bid/ask quantities are not the percentage of executed buys/sells, nor a reliable institutional/retail classification. Do not present a “70% selling” claim from a pending-order snapshot.

## Monitoring and records

Track actual fills/quantity separately from proposed and paper positions. Record entry reasons and changes in bank agreement, futures VWAP, option spreads/IV and event proximity. Manual dashboard alerts request attention; later execution rules perform actions and reconcile the result.

Useful stages: plan → actual/paper entry → monitor → exit request when execution exists → fills → reconciliation → review. Preserve partial fills and unresolved state rather than assuming a perfect all-leg close.

Primary references: [Kite quote/depth documentation](https://kite.trade/docs/connect/v3/market-quotes/), [Upstox option chain](https://upstox.com/developer/api-documentation/get-pc-option-chain/) and [Upstox portfolio stream](https://upstox.com/developer/api-documentation/get-portfolio-stream-feed/). Access and account coverage have not been tested.
