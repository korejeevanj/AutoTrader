# Trading.Analytics

Ownership: shared financial/market features for the dashboard, engine and replay.
Future features must preserve completed-bar availability and missing-data semantics;
cash indices cannot supply traded-volume VWAP.

Current state: `DashboardAnalytics` exposes shared decimal calculations, referencing
Trading.Domain:

- Quote spread and observation age evaluated against a supplied as-of timestamp.
- Completed futures chart: exclude bars whose end or availability is after as-of;
  calculate the opening range only when every requested minute is present.
- Approximate cumulative futures VWAP from candle typical price × volume, rounded
  to two decimal places. Zero cumulative volume produces no VWAP point.
- Index-neutral constituent participation against the full 100% fixture denominator. Only fresh constituents
  with weight, price and VWAP contribute; missing/unusable weight stays disclosed.
  No usable observations produce null above/below weights, not zero observations.

The synthetic API uses a 09:15–09:30 Asia/Kolkata opening range. All supplied times
are UTC. React presents these calculations rather than maintaining another financial
implementation. These helpers have no system-clock, provider or strategy access.

`HistoricalSessionAnalytics` and its incremental accumulator supply unrounded
session candle VWAP, preceding completed-bar mean true range, complete-prefix coverage
and input IDs for replay. Only available completed bars contribute; zero cumulative
volume yields null, and gaps invalidate full-session features. Cash-index bars are
not used as traded-volume inputs by the replay adapter.
All configured index workspaces reuse these helpers; the participation output is
evidence, not a generic multi-index strategy confirmation rule.
