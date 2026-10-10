# Reference Projects and Reuse Questions

Compiled: 9 October 2026 | Public documentation checked; repositories not cloned or executed

## Rob Carver — pysystemtrade

Recovered discussion: 6 October 2026. This is the UK futures trader/project Jeevan remembered.

- [Repository](https://github.com/pst-group/pysystemtrade)
- [Introduction](https://github.com/pst-group/pysystemtrade/blob/develop/docs/introduction.md)
- [Backtesting guide](https://github.com/pst-group/pysystemtrade/blob/develop/docs/backtesting.md)
- [EWMAC rule](https://github.com/pst-group/pysystemtrade/blob/develop/systems/provided/rules/ewmac.py)

The repository and documentation were retrieved. The direct EWMAC source page could not be fetched in this check; retain it as a review pointer and verify its path/content when reviewing a pinned commit.

The README describes Rob Carver's futures backtesting/trading engine and Interactive Brokers integration. It declares GPL-3.0. Useful review entry points visible in the repository are systems, sysquant, sysdata, sysexecution and sysproduction.

Review forecasts, volatility normalization, position sizing, portfolio risk, data boundaries and order-state handling. Translate the ideas into requirements before adopting code. Indian intraday option selection, contract data and our broker adapters remain our own work. Its framework is not evidence for our requested small-profit trades.

Earlier discussion of EWMAC concerned a fast/slow exponentially weighted moving-average difference normalized by volatility. Treat it as a trend-feature hypothesis, not a probability of intraday profit. Our first explicit research strategy remains opening-range breakout with futures VWAP and bank confirmation.

## Bank Nifty dashboard

Recovered discussion: 6 October 2026.

- [Repository](https://github.com/chandan016/bnf-dashboard)
- [Backend review entry](https://github.com/chandan016/bnf-dashboard/blob/main/server.py)
- [Frontend review entry](https://github.com/chandan016/bnf-dashboard/blob/main/app.js)
- [Constituent definitions](https://github.com/chandan016/bnf-dashboard/blob/main/bnf_constituents.json)

The README describes opening-range/SMA strategies and a constituent-strength model using gap magnitude, breakout breadth, heavyweight alignment and sector agreement. It lists 12 constituent stocks. Review the calculation and UI as references; verify membership/weights for every relevant date.

Prior discussion flagged optimistic target-before-stop treatment. That is a review lead, not a fresh code-audit result. When auditing, inspect event granularity, stop/target ordering, entry timestamps, costs and whether results are underlying points or real option cash P&L. Do not reuse advertised win rates as validated evidence.

## C# framework and calculation references

| Project | Link | Review target | Fit/limitation |
| --- | --- | --- | --- |
| QuantConnect LEAN | https://github.com/QuantConnect/Lean | Event-driven engine, pluggable models, data/algorithm boundaries and tests | C# architecture reference; our Indian market data and brokerage coverage need separate verification |
| Stock Indicators for .NET | https://github.com/facioquo/stock-indicators-dotnet | Indicator API, warm-up behaviour and calculation conventions | Candidate library for shared analytics; does not choose profitable trades |
| QLNet | https://github.com/amaggiulli/QLNet | Option valuation inputs, Greeks and numerical methods | Later risk/scenario reference; model outputs depend on input/model assumptions |

LEAN's README describes modular event-driven backtesting/live trading. Stock Indicators supplies technical-indicator calculations. QLNet is a C# quantitative-finance library. Those capabilities support investigation; none supplies verified AutoTrader profitability.

## Review worksheet for each repository

Record repository and pinned commit; useful file/module; input/output contract; assumptions; data/market dependencies; licence; correctness checks; what to adopt, adapt or reject; effort saved; and the AutoTrader component affected.

First reviews should focus on one useful component at a time. Keep the manual dashboard useful while evaluating external engines. No framework migration is a prerequisite for data recording, linked views or research.
