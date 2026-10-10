# Automated Trading System — Start Here
Updated: 9 October 2026 | Owner: Jeevan | Status: planning baseline; implementation has not started

## Starting point
Build the manual-trading dashboard first, with the live data pipeline and recorder it needs. The first useful result is a live Bank Nifty workspace showing market context, weighted bank participation, option quotes/costs, data freshness and a journal for manual decisions. Orders are placed manually in the broker app during this stage.

Then build historical replay and the explicit selection engine; show its reasons and decisions in the same dashboard. Advance to live shadow recommendations and only later automated execution. This restores the agreed data/dashboard → non-trading prediction → automated execution sequence and supersedes the earlier replay-first ordering.

See 05-SYSTEM-COMPONENTS-AND-PROJECTS.md for each project's responsibilities and 06-DASHBOARD-SPECIFICATION.md for the dashboard's panels, usability and rollout.

## Objective
Determine whether a repeatable intraday decision policy has positive net expectancy under realistic costs and adverse fill assumptions. Speed and automation help implement a policy; historical evidence must establish whether the policy is useful.

Latest capital assumption: ₹5 lakh. Initial markets: Bank Nifty first, then Nifty 50 and Sensex. Initial selection-research positions: long call or long put; debit spreads are a later comparison. No overnight exposure in the initial experiment.

The engine can examine a configurable set of 20–35 eligible option candidates. Those candidates are alternatives, not independent bets. Evaluate wider liquid strikes when appropriate, rather than automatically selecting only ATM.

## Build order
| Milestone | Deliverable | Completion evidence |
| --- | --- | --- |
| D0 — Dashboard design | React workspace using clearly labelled fixtures; linked chart, bank table, option comparison and plan drawer | User can compare two options and journal a plan; mode/freshness states are clear |
| D1 — Live manual dashboard | Kite adapter, current valid contracts/weights, shared analytics, API streaming, recorder, basic cost view and journal | One real session recorded; stale/gap states and actual field coverage documented |
| D2 — Position assistance | Read-only Upstox account state, indicative exit-net P&L, entry snapshots and informational alerts | Fills/quantity reconcile; manual alerts never claim an order was executed |
| M0 — Historical data proof | One session of index, futures, banks and options plus earlier warm-up history | Coverage report, valid contract metadata, known missing fields |
| M1 — Replay foundation | Normalized import, simulated clock, playback and dashboard replay mode | Same input/config yields identical decisions; future candles cannot affect earlier decisions |
| M2 — First selection policy | Opening-range breakout, futures VWAP and bank confirmation | Every accepted/rejected candidate has a recorded reason |
| M3 — Research simulator | Position lifecycle, costs, adverse fills and cash/risk checks | Ledger reconciles; stop/target ambiguity and overruns reported |
| M4 — September replay | All validated September 2026 sessions | Daily reports, drawdown, net expectancy and rejection breakdown |
| M5 — Unseen evaluation | Frozen policy on untouched chronological periods | Regime, cost and feature-ablation results |
| M6 — Live shadow mode | Recorded live recommendations in the existing dashboard, no orders | Freshness, latency and historical/live feature consistency |
| M7 — Execution project | Upstox order lifecycle, basket recovery and reconciliation | Separate scope after research/operational checks and agreement of daily risk budget |

Historical data acquisition can proceed alongside dashboard work, but a complete September dataset does not block the manual dashboard. One month verifies research integration; it is not enough to establish durable profitability. Do not tune on September and call it an independent test.

## Custom dashboard decision

Build our own React/TypeScript dashboard. Jeevan confirmed two purposes: showcase substantial frontend/UI engineering in the portfolio, and tailor information/layout/workflows to personal trading needs. Existing products are design references, not the planned replacement for our dashboard.

Task D00 is a focused inspiration review: record useful interaction patterns and translate them into our own design. It is not a build-versus-buy gate and does not block the first mock. See 07-MARKET-DASHBOARD-APPS-AND-BUILD-DECISION.md. Reuse suitable libraries rather than rebuilding every low-level chart primitive; retain our own composition, calculations and behaviour.

## First implementation task
Create the minimal C#/.NET backend and React workspace for D01–D03 in 04-DEVELOPMENT-BACKLOG.md. Define canonical contracts/quotes and mode/freshness fields, connect a labelled fixture, then perform the first authenticated live-feed smoke test. Add recording before declaring the dashboard live.

Use fabricated fixtures only for software/design verification and label them synthetic. They are not market-performance evidence. No automated entry or exit policy is needed to make the manual dashboard useful.

## Project documents
- 01-SCOPE-AND-DECISIONS.md — agreed constraints, hypotheses and open decisions.
- 02-ARCHITECTURE-1.md — C# modules, contracts and repository structure.
- 03-DATA-AND-REPLAY.md — acquisition, normalization and replay correctness.
- 04-DEVELOPMENT-BACKLOG.md — ordered tasks, acceptance criteria and evaluation.

- 05-SYSTEM-COMPONENTS-AND-PROJECTS.md — full component ownership and stage map.
- 06-DASHBOARD-SPECIFICATION.md — manual dashboard design, priorities and primary references.
- 07-MARKET-DASHBOARD-APPS-AND-BUILD-DECISION.md — app comparison and implementation-scope checkpoint.
- resources/00-RESEARCH-INDEX.md — earlier discussion summaries and reusable-project links.

## Current dependencies
Live credentials/entitlements, current constituent weights and quote/account integration are untested. Historical data has not been downloaded. September contract completeness has not been verified. Broker credentials, Upstox Plus entitlement, and historical quote/depth availability are not established. Dashboard development can begin using labelled fixtures while live access is arranged. Historical research follows the data proof.

These are project documents, not an existing application or GitHub repository. No conversations have been transferred into a ChatGPT Project.
