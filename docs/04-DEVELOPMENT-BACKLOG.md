# Development Backlog and Evaluation
Updated: 9 October 2026 | Tasks ready for implementation; none completed

## First work package — manual dashboard

Goal: make one live Bank Nifty workspace useful for manual analysis and monitoring, while recording data for later research. This is the latest user priority and replaces replay as the first deliverable. See 06-DASHBOARD-SPECIFICATION.md.

| ID | Task | Acceptance criteria |
| --- | --- | --- |
| D00 | Review dashboard inspiration and define our custom UI patterns | Record useful patterns, our adaptations and panel priorities; custom build is decided; does not block D01/D02 |
| D01 | Minimal .NET/React workspace, canonical quotes/contracts and labelled fixture | Mode, source/as-of and missing-data states explicit; no order route |
| D02 | Linked chart, bank table, option comparison and plan/journal drawer | User can inspect contributors, compare two options and save a reason |
| D03 | Authenticated live feed/contract smoke test | Returned fields, subscriptions and access failures documented; no invented Greeks/depth |
| D04 | Normalization, reconnect, bounded cache and recorder | Real session saved with receipt/source times; gaps and stale instruments visible |
| D05 | Shared VWAP/range/bank features and risk/cost estimates | Hand-checked fixture; whole lots, valid weights and missing coverage explicit |
| D06 | API snapshot/delta stream and frontend freshness | Browser reconnect restores consistent state; UI refresh does not control recorder |
| D07 | Read-only broker positions and reconciliation | Actual fills/quantity match broker examples; paper/plans remain separate |
| D08 | Exit-net estimate, entry thesis and condition alerts | Bid/ask mark and charge assumptions visible; deduplication; alerts do not claim exits |
| D09 | Verified/manual event rail and contextual observations | IST time, source/as-of and prior-day FII/DII labels; omissions visible |
| D10 | One-user workflow rehearsal and recorded live-session review | Mode/stale-state understood; compare task time and errors with existing workflow |
| D11 | Frontend portfolio evidence and reproducible demo | Responsive/keyboard/error-state workflows demonstrated; render/update performance measured; E2E evidence; demo data labelled |

Custom dashboard implementation is confirmed for personal trading and frontend portfolio use. D00 collects inspiration; it is not a build-versus-buy approval gate.

D01/D02 use clearly synthetic fixtures. Live usefulness depends on D03–D06; broker-position assistance depends on D07. Advanced Greeks/scenarios and automatic event ingestion can follow the useful core.

## Second work package — historical replay and selection research

Goal: replay one historical Bank Nifty session and inspect its complete decision trace. Reuse dashboard domain, analytics, risk and recording contracts.

| ID | Task | Acceptance criteria |
| --- | --- | --- |
| T01 | Extend dashboard solution with replay host, configuration and simulated clock | Builds locally; no broker-order dependency; UTC/session times explicit |
| T02 | File importer and validation report | Invalid/duplicate/unsynchronized data is identified; provenance retained |
| T03 | Historical data smoke test | One real session and warm-up retrieved; actual coverage documented |
| T04 | Deterministic replay and recorder | Two runs produce identical ordered decisions; future perturbation leaves past decisions unchanged |
| T05 | Opening range and candle-derived futures/bank VWAP | Hand-checkable values; no cash-index volume used; approximations labelled |
| T06 | Weighted bank confirmation and setup detector | Effective weights used; missing inputs produce WAIT/SKIP |
| T07 | Point-in-time option candidate generator | Valid strike/expiry/lot constraints; universe determined without future outcomes |
| T08 | Risk/cost feasibility gate and ranking | Incompatible lot risk rejected; score cannot override a hard gate |
| T09 | Simulated position policy and fills | Entry/hold/exit reasons recorded; ambiguity and stop overruns visible |
| T10 | Fees and trade accounting | Net ledger reconciles; charges parameterized by applicable date/sides/orders |
| T11 | One-session report | Accepted and rejected candidates, decision trace, P&L and assumptions available |

T01/T02 can begin with clearly labelled software fixtures. T05 onward on real data depends on T03. If historical weights or quotes are missing, disable those claims and report the limitation rather than invent values.

## After the first session
| ID | Task | Acceptance criteria |
| --- | --- | --- |
| T12 | Full September dataset | Every requested session has a coverage status; gaps not hidden |
| T13 | Frozen baseline month replay | Strategy/config hash retained; failures, no-trade days and rejected candidates included |
| T14 | Cost/delay stress tests | Re-run with worse fills; report how much net result changes |
| T15 | Bank-feature ablation | Compare same policy with/without constituent features and record all trials |
| T16 | Untouched chronological evaluation | No retuning on final evaluation data; overlapping label windows excluded when applicable |
| T17 | Extend existing dashboard with replay/research views | Day/clock selector, feature trace, candidate table, reasons, assumptions, ledger |
| T18 | Extend existing live recorder with engine shadow mode | Freshness/receipt latency measured; no order submission capability |
| T19 | Execution design | Separate scope for Upstox fills, retries, risk controls and reconciliation |

## Verification that matters
- Future leakage: modifying later candles cannot change earlier decisions.
- Candle availability: a partial or unfinished candle cannot act like a completed candle.
- Determinism: input, config and seed fixed implies the same output.
- Quantity/risk: whole lots only; infeasible rupee stops reject rather than silently resize risk.
- Accounting: fees and cash movements reconcile across entries/exits.
- Same-bar ambiguity: conservative result and explicit flag.
- Stop gaps: exits may exceed trigger loss; report the actual simulated loss.
- Historical universe: no contract, constituent or weight appears before valid time.
- Candidate rejection: every failed gate is inspectable.

Use a small set of targeted tests and hand-checked fixtures. Do not write tests that merely repeat the implementation.

## Research report per run
Record:
- Sessions processed/rejected for data quality; opportunities and trades.
- Gross/net P&L, fee total and modeled execution loss.
- Net expectancy per trade, win/loss sizes, profit factor where defined.
- Maximum drawdown, worst trade/day, consecutive losses and stop-budget breaches.
- Holding time, adverse/favorable excursion when data resolution permits.
- Results by market regime and expiry proximity.
- No-trade/rejection reasons.
- Base/adverse-cost sensitivity and feature ablation.
- Strategy trials attempted, data/config/code versions and known gaps.

Do not use win rate alone as the success criterion. Do not declare a strategy profitable because it selected historical winners with future knowledge. Changing parameters after seeing results creates a new experiment and must be logged.

## Advancement gates
Replay correctness: timing, determinism, accounting and data-quality tests pass.

Research credibility: frozen rules evaluated on untouched periods; net results, drawdown and sensitivity documented. Passing software tests does not establish a market edge.

Shadow readiness: data freshness and decision latency work on the actual runtime; no missing inputs silently produce recommendations.

Execution readiness: separately agreed daily risk budget and tested order lifecycle/recovery. Backtest profit alone does not justify automatic deployment.

## Suggested first research development session
1. Inspect the existing dashboard solution and installed SDK; add the replay host.
2. Define ContractDefinition, MarketBar, ReplayClock and TradeDecision.
3. Import a tiny fixture and emit deterministic WAIT/SKIP records.
4. Prove future-candle isolation.
5. Connect the first authenticated historical-data smoke test.

Cloud infrastructure and model training are not prerequisites. Dashboard delivery comes first; the research host reuses its data contracts and calculations.

## Suggested first dashboard development session
1. Inspect local .NET/Node tooling and create the minimal backend/UI.
2. Define canonical contract/quote, mode/freshness and journal contracts.
3. Display a synthetic linked chart/table/plan fixture with missing/stale states.
4. Prepare live-feed subscription and recording smoke test.
5. Implement manual position assistance after account access is verified.

No application code has been created by this documentation update.
