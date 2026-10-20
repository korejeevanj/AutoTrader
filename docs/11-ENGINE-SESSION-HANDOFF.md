# Trading-engine session handoff

This session owns Swagger, the multi-index UI and related backend changes. The
engine session owns `src/Trading.Engine` and its dedicated test files. Existing
engine code may already be under development; inspect it before extending it.

Read the governing documents and actual implementation before choosing interfaces.
The dashboard now uses a configured catalog (`IIndexCatalog`, `IndexDefinition`),
index-scoped schema-2 fixtures, index-aware journal requests and a read-only bridge
for known schema-1 Bank Nifty evidence. These fixture view models do not establish
canonical live strategy inputs or complete fee/risk calculations.

## Copy-paste prompt

```text
Work in C:\Projects\AutoTrader on the trading-decision engine.
Call the Skill tool with "grilling" for unresolved strategy/interface decisions.

Read docs/08-MONDAY-LIVE-PAPER-SPRINT.md FIRST, current AGENTS.md,
docs/01-SCOPE-AND-DECISIONS.md, docs/02-ARCHITECTURE-1.md,
docs/03-DATA-AND-REPLAY.md, docs/05-SYSTEM-COMPONENTS-AND-PROJECTS.md,
docs/09-SYNTHETIC-DASHBOARD-INCREMENT.md,
docs/10-MULTI-INDEX-AND-OPENAPI-INCREMENT.md and current module source/READMEs.
Preserve all modified/untracked work, including any existing engine implementation.

Ownership: this engine session owns src/Trading.Engine and new dedicated files
under tests/Trading.Tests/Engine. Another session owns Swagger, the UI and related
backend work. Coordinate edits to Domain, Analytics, Risk, Api, Data, Portfolio,
configuration, shared project files and documentation before making them.
Do not duplicate shared financial calculations inside Engine.

The cockpit provides synthetic BANKNIFTY/NIFTY/SENSEX workspaces. Current candidate
decisions and positions are still illustrative; saved plans are not paper intents
or fills. Inspect IIndexCatalog and IndexDefinition for identity conventions, but
do not treat synthetic fixture contracts/weights as verified live inputs.

Design an index-agnostic deterministic policy module with explicit per-index
strategy configuration and separate portfolio-wide cash/risk controls.
Bank Nifty is the first specified Monday strategy. Do not silently apply its
weighted-bank confirmation to Nifty/Sensex or claim their strategies are validated.

Hosts supply the clock, available market evidence, valid point-in-time contracts,
configuration and portfolio/risk state. Engine must not call providers, access the
system clock directly, query databases during evaluation or submit orders.

Implement confirmed slices through public Engine interfaces with meaningful TDD:
- Completed-bar availability, future isolation and deterministic event ordering.
- Futures ORB/VWAP and explicit constituent confirmation with missing-weight disclosure.
- Contract/freshness/liquidity/cash/cost/risk gates before ranking; reject unknown
  or infeasible risk rather than manufacturing an estimate or relaxing a budget.
- Auditable ENTER/WATCH/SKIP/INSUFFICIENT_DATA decisions with versioned reasons.
- HOLD/WATCH_EXIT/EXIT policy; mandatory quote-driven cash stops independent of
  completed-bar entry evaluation, plus session/time exits and visibly pending exits.
- Per-index position/attempt limits and deterministic allocation of shared capital.

First report what exists, missing inputs and shared-contract changes required.
Grill unresolved choices and confirm scope before adding unsupported behavior.
Distinguish decision policy from the separately implemented paper broker, delayed
fresh bid/ask fills, fee accounting, persistent intents and restart recovery.
Real broker order/GTT/modify/cancel calls remain deferred. No commits unless asked.
```

## Integration checklist

When engine slices are ready, report their public interfaces, owned files and test
results. Coordinate the API host adapter separately: recording/position monitoring
must operate independently of UI refresh, and synthetic policy tests do not prove
live readiness or profitability. Engine decisions require real input provenance;
the current fixture dashboard should retain its labels until explicitly integrated.
