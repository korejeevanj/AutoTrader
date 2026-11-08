# Algo trading kickoff (prompt for a new session)

Paste everything below the line into a new Claude Code session opened in `C:\Projects\AutoTrader`.

---

We are starting a new line of work in this repository: **systematic algo trading**, after closing an intraday options project that found no
edge. Read this whole prompt before doing anything, then follow "First steps".

## Who I am and what I want

- Individual trader in India, Zerodha Kite account (Kite Connect API with historical data). Capital about **Rs 5 lakh**.
- Goal: a systematic, automated strategy that makes money on average after costs, run with discipline and risk limits. Losing days are
  acceptable; the average matters. I am not trying to compete with high-frequency firms.
- I previously hoped for an average of about Rs 1,000 a day on Rs 5 lakh (about 50% a year). Our own data showed this is not realistic:
  a Nifty index fund averaged about Rs 353 a day (10.5% a year, 2016-2026) with 46% losing days and a Rs 2.98 lakh deepest fall. Help me
  set an honest target before building anything.
- I prefer short, plain explanations, evidence over opinion, and independent Opus subagent reviews for big decisions. Do not waste time or
  tokens re-testing ideas already killed (list below).

## What already exists (read, do not rebuild)

- `AGENTS.md` and `CLAUDE.md`: binding project rules, build commands (pinned .NET 10 SDK in the user profile), paper-only rules.
- `docs/25-RESEARCH-PROTOCOL.md`: the full record of the previous work, every pre-registered test, results, and the closing summary.
  **Read the closing summary first.**
- `docs/24-SCALP-WHAT-IF-21-OCT.md`, `docs/23-LIVE-RUNBOOK.md` (suspended), `docs/22-LIVE-QUOTE-PAPER-TRADING.md`: the live paper platform.
- Research tools in `scripts/research/` (Python standard library only, run with `python -I`): `hypotheses.py`, `trend.py`, `breadth.py`,
  `candles.py`, `signal_search.py` + `option_sim.py` (modelled option trades), `postmortem.py`, `chart_game.py`, `ledger_score.py`,
  `extract_features.py`, each with plumbing tests (`test_*.py`) that check noise is rejected and a planted effect is found.
- Data under `data/research/` (gitignored, never commit it):
  - `index-minute/`: minute candles Jan 2022 to Oct 2026 for NIFTY_BANK, NIFTY_50, SENSEX, INDIA_VIX and the 14 Bank Nifty banks.
  - `index-day/`: daily candles 2005-2026 for NIFTY_BANK, NIFTY_50, SENSEX; INDIA_VIX from 2009; MCX crude continuous 2015-2026;
    USD/INR continuous (only Oct 2021 to Jun 2023).
  - `features-2026-10-21/22.sqlite`: two recorded live sessions, fully decoded (depth, open interest, order-book totals).
- Fetching more history: run the Research API (`scripts\live\start-research.ps1`, port 5081) and call
  `POST /api/research/archive/index-history?from=&to=&interval=minute|day&symbols=...` (NSE/BSE symbols, or `FUT:EXCH:NAME` for continuous
  futures). The Kite access token in `src/Trading.Api/kite.credentials.Local.json` must be renewed daily by me (see `docs/12-DAILY-KITE-SETUP.md`).
  Kite cannot supply expired option contracts.
- Git: branch `feature/live-robustness`, not yet pushed or merged into `main`. Ask me before pushing or opening a PR.

## What was already tested and killed (do not repeat)

Intraday directional rules on Bank Nifty (momentum, breakouts, opening range, VWAP), a 108-rule walk-forward long-option search with trailing
stops, bank breadth lead-lag, a multi-timeframe candle model over the index and 14 banks, coin-flip entries with loss cuts and profit trails,
entry filters (gap, VIX, range, time of day, crude), outside forces as direction signals (overnight gap, crude, rupee, a hand-built macro forecast
ledger), and three single-index trend rules (55-day breakout with ATR stop, 12-month momentum, 50/200-day filter) on 21 years of Bank Nifty.
Root causes: entries carried no information, about Rs 112 per option round trip, option time decay, and a single index being a thin base.

## Rules for this work

- **Paper only.** Never call any broker order, GTT, modify or cancel endpoint. Retail algos that go live in India must follow SEBI's retail algo
  framework (Feb 2025 circular, broker-approved); flag this before anything live.
- **Pre-register every test** in a new doc (`docs/27-...`) before looking at results: data, rule, costs (real Zerodha fees and taxes from
  April 2026, slippage), sample split, success and kill criteria, and the multiple-testing correction. Judge on out-of-sample data only.
- Check plumbing on synthetic data first (noise must fail, a planted effect must pass).
- Avoid survivorship bias: stock universes need point-in-time constituent lists; say so if we do not have them.
- Benchmark everything against a Nifty index fund and a liquid or arbitrage fund, net of costs and taxes.
- Commit in small steps with tests; keep `dotnet test AutoTrader.slnx` green if any .NET code changes.

## First steps

1. Read the closing summary in `docs/25-RESEARCH-PROTOCOL.md` and confirm in a few lines what it means for this work.
2. Ask me at most four short questions to pin down what "algo trading" means for me: instruments (stocks, ETFs, index futures, options),
   holding period (days, weeks, months), how much drawdown I can accept, and how hands-off it must be.
3. Then propose 2-3 candidate strategy families that have real published evidence and fit Rs 5 lakh at retail latency (for example a
   multi-factor stock or ETF momentum/quality portfolio rebalanced monthly, trend-filtered index or ETF allocation, or defined-risk premium
   selling tested on real option prices), each with expected return and drawdown ranges, the data needed (and whether we have it), and the
   cheapest decisive first test. Get an independent Opus review of the shortlist before building.
4. Only after I choose: pre-register the first test, build it, run it once, and report.
