# Trading.Risk

Ownership: shared fees, whole-lot/cash feasibility, liquidation marks, scenario
estimates and risk/cost gates. Gross stop triggers, fees and net risk remain distinct.

Current state: `IndicativePlanning` supplies checked positive whole-lot units and
decimal indicative long gross P&L using a supplied sell-side bid. A missing current
bid produces null gross P&L. The API withholds current bids for stale/disconnected
preset positions; this is an indicative mark, not an executable fill.

References Trading.Domain. Live fee/cash/net-risk adapters and credible option valuation
are still pending; dashboard fixture planning estimates and position net
P&L remain null with explanations. No clock or provider access exists here.

`HistoricalResearchCalculations` now supplies the explicitly declared candle-research
fee model, adverse tick fills, structural stops/R, progress, whole-lot model risk,
cash/fee accounting and drawdown. These are reusable .NET calculations with sourced
effective-date fee profiles; they do not supply credible structural-to-option
valuation or qualify observed execution. See Trading.Replay's guide for assumptions.
