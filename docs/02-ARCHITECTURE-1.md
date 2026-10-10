# Architecture
Updated: 9 October 2026 | Proposed design

## Stack
- Supported .NET SDK available in the development environment; pin it in global.json after checking local tooling.
- C# libraries for domain, data, shared analytics and risk; a .NET API host for the first dashboard. The recorder can initially run as a hosted service; replay/selection hosts follow.
- Append-only recorded observations and versioned files initially; durable journal/config storage. Add PostgreSQL when querying/accounting needs justify it, without making it a prerequisite for the first manual dashboard.
- React + TypeScript dashboard first. REST snapshots and SignalR updates connect it to the backend; replay and shadow views extend the same UI later.
- No Kafka, Redis, Kubernetes, GPU or LLM is required for the initial deterministic engine.
- No numerical performance promise yet: benchmark actual features and candidate counts before optimizing.

## Modules
| Module | Responsibility |
| --- | --- |
| Importer | Adapt provider data; preserve raw responses and provenance |
| DataValidator | Coverage, duplicates, contract validity and time consistency |
| ReplayClock / Feed | Emit only information available at simulated time |
| FeatureCalculator | Opening range, approximate VWAP, returns, volatility, bank participation |
| SetupDetector | Explicit bullish, bearish or no-setup result |
| CandidateGenerator | Eligible long options initially; bounded spread families later |
| ScenarioEvaluator | Favorable, flat, adverse and IV/time scenarios, with uncertainty |
| RiskCostGate | Cash, lot, risk, data and cost feasibility |
| CandidateRanker | Rank surviving candidates; WATCH/SKIP allowed |
| PositionPolicy | Hold, trail, invalidate, exit and session-close decisions |
| ResearchSimulator | Hypothetical fills, fees, position accounting |
| DecisionRecorder | Features, reasons, candidates, configuration and run lineage |
| Read API / Dashboard | Live manual analysis first; replay and shadow results later |
| Live recorder/cache | Normalize live observations, reconnect safely, track freshness and gaps |
| Shared analytics/risk | Calculate dashboard features and planning estimates reused by the engine |
| Portfolio / Journal | Reconcile read-only account state; preserve plans, reasons and actual/paper distinctions |
| Context / Alerts | Event provenance, informational condition changes, cooldown and acknowledgement |

Hard gates run before ranking. A high score must not override invalid data or risk limits. A score is not a calibrated probability.

The domain and selection libraries never call broker endpoints, use the system clock directly or submit orders. Hosts supply data and clock. The same decision library is used by replay and live shadow mode.

## Suggested repository layout
- docs/ — these project documents
- src/Trading.Domain/ — market IDs, contract definitions, bars, quotes, decisions, positions
- src/Trading.Analytics/ — shared market features
- src/Trading.Risk/ — shared cost, mark, quantity and scenario calculations
- src/Trading.Engine/ — later setup rules, selection and position policy
- src/Trading.Data/ — imports, validation, file persistence and provider adapters
- src/Trading.Replay/ — console entry point, simulated clock, research simulator
- src/Trading.Api/ — initial read API and streaming host; journal/config writes, no initial order route
- src/Trading.Recorder/ — live host, initially hosted inside API if simpler
- src/Trading.Portfolio/ — read-only broker reconciliation and manual journal
- src/Trading.Context/ and src/Trading.Alerts/ — event/context and alert modules, initially co-located if simpler
- src/Trading.Shadow/ and src/Trading.Execution/ — later-stage hosts only
- ui/trading-dashboard/ — later React application
- tests/Trading.Tests/ — meaningful domain, replay and accounting tests
- config/ — versioned non-secret strategy and simulation settings
- data/ — ignored private market data; raw/normalized/quality-reports
- runs/ — ignored generated ledgers and manifests

This layout is a plan; no solution or source project has been generated yet.

## Core contracts
Use decimal for prices, money and quantities where appropriate; use floating point for statistical calculations with explicit conversions.
- ContractDefinition: internal ID, provider IDs, exchange, underlying, type, expiry, strike, lot size, tick size, validity dates.
- MarketBar: contract ID, interval, start/end UTC, AvailableAtUtc, OHLC, volume, optional OI, source, quality flags.
- MarketQuote: event/receipt times, bid/ask and quantities when observed, source and quality flags.
- FeatureSnapshot: AsOfUtc, feature values, feature version, required-data status.
- TradeDecision: run ID, decision time, action, setup, candidate legs, reasons, estimated costs/risk, quality class.
- SimulatedFill: event time, contract, side, quantity, price, fee breakdown, assumption flags.
- RunManifest: data hashes, source IDs, strategy/config version, code version, interval, fill model, cost schedule, random seed if used.

## Important boundaries
Cash indices have no traded volume for VWAP. Use futures or individual bank trades; 1-minute typical-price × volume is only a candle approximation.

Historical option bars alone do not provide observed Greeks or spreads. Store estimates and fill assumptions separately from measured fields. An unavailable value remains null, not zero.

Inputs must distinguish BarStart, BarEnd and AvailableAt. The engine cannot access a completed bar before it becomes available.

Process all events available at a decision timestamp under one deterministic ordering. Never allow arbitrary file order to create inconsistent constituent confirmation.

## Reusable references
Evaluate small integrations rather than adopt an entire engine immediately:
- Stock Indicators for .NET: https://github.com/facioquo/stock-indicators-dotnet — indicator calculations; review installed API and licence.
- QLNet: https://github.com/amaggiulli/QLNet — optional valuation/Greeks; model assumptions still required.
- LEAN: https://github.com/QuantConnect/Lean — reference event-driven architecture and future research alternative; Indian derivatives data/adapters still need verification.
- pysystemtrade: https://github.com/pst-group/pysystemtrade — futures research/risk reference; not our ready-made intraday options strategy.

Start with the small dashboard backend and recorder, then the custom replay host. Do not make framework migration a prerequisite for a useful manual dashboard or proving one rule.

## Custom frontend and design references

The custom React/TypeScript dashboard is confirmed for personal usability and frontend portfolio evidence. Existing applications inform patterns such as linked charts, option comparison and position context; they do not replace the planned UI. Backend Domain, Data, Analytics and Risk remain shared with later replay/selection.

Organize the frontend around typed view models, chart/table components, connection/mode state, linked selection, trade planning, portfolio monitoring, alerts and journal/replay screens. Keep calculations in the backend and UI display state in React. Reuse suitable charting/data-table primitives while owning the application layout, interaction and domain behaviour. Add a labelled fixture-backed demo with the same view-model contracts as live mode.

## Dashboard-first boundaries

See 05-SYSTEM-COMPONENTS-AND-PROJECTS.md for component-to-project ownership and 06-DASHBOARD-SPECIFICATION.md for the user experience. Project names describe logical ownership; create them incrementally in a single application rather than deploying many services.

The first backend is read-only with respect to broker orders, but may persist journal/config changes. Keep provider secrets server-side. Market recording must continue independently of UI refreshes. Share calculated features and risk values with replay; do not recalculate them differently in JavaScript.

Extend contracts with source/receipt/quote-as-of timestamps, snapshot sequence and quality coverage; PositionSnapshot with source/reconciled-at and actual/paper identity; JournalEntry with entry thesis and as-of snapshot; EventRecord with source, IST display time and verification status; AlertRecord with observed condition, rule version, cooldown and acknowledgement. Upstox account fills are authoritative for actual positions; quote-derived marks are estimates.
