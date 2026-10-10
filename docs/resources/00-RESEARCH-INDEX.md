# AutoTrader — Research Resource Index

Compiled: 9 October 2026 | Status: topic summaries and primary-source links

## How to use these resources

These notes preserve relevant reasoning recovered from earlier conversations and the supplied planning documents. They are not verbatim transcripts or an exhaustive export of all chats. Prior assistant estimates are not independently established facts. This pack omits unsupported historical percentages and performance promises rather than turning them into project evidence.

The current project decisions live in [00-START-HERE.md](../00-START-HERE.md) and [01-SCOPE-AND-DECISIONS.md](../01-SCOPE-AND-DECISIONS.md). The latest user instruction is dashboard first: manual use and recording before selection research and automation. Older alternatives below remain dated research, not replacements for current scope.

| Resource | Contents | Project connection |
| --- | --- | --- |
| [01-REFERENCE-PROJECTS.md](01-REFERENCE-PROJECTS.md) | Rob Carver, Bank Nifty dashboard, LEAN and .NET libraries | Architecture, reusable calculations and review targets |
| [02-TRADE-SELECTION-DEEP-DIVE.md](02-TRADE-SELECTION-DEEP-DIVE.md) | Setup detection, 20–35 option alternatives, gates and ranking | Trading.Engine and candidate dashboard |
| [03-BANK-NIFTY-SIGNAL-RESEARCH.md](03-BANK-NIFTY-SIGNAL-RESEARCH.md) | Weighted banks, VWAP, breadth and signal disagreement | Trading.Analytics and bank panel |
| [04-OPTIONS-AND-EXECUTION-RESEARCH.md](04-OPTIONS-AND-EXECUTION-RESEARCH.md) | Premium mechanics, liquidity, fees, stops and lifecycle | Trading.Risk, Portfolio and later Execution |
| [05-REPLAY-AND-VALIDATION-RESEARCH.md](05-REPLAY-AND-VALIDATION-RESEARCH.md) | Data coverage, timing, fills and unseen evaluation | Trading.Data, Replay and Research |
| [06-HEDGING-EVENTS-AND-ALTERNATIVES.md](06-HEDGING-EVENTS-AND-ALTERNATIVES.md) | Cross-index hedges, overnight risk, macro events and AI | Context panels and deferred research |

## New market-app check

[07-MARKET-DASHBOARD-APPS-AND-BUILD-DECISION.md](../07-MARKET-DASHBOARD-APPS-AND-BUILD-DECISION.md) adds the 9 October comparison of existing manual dashboards. It is new external research, not a recovered earlier discussion. Product capability claims are separated from unperformed authenticated trials and project-specific requirements. Jeevan subsequently confirmed a custom dashboard for personal use and frontend portfolio evidence; the product comparison now serves design inspiration.

## Recovered discussion map

| Discussion period | Subjects recovered | Status |
| --- | --- | --- |
| 28 September 2026 | Small-win automation, weighted bank analysis, option execution/liquidity, directional baskets and cross-index hedging | Earlier exploration; basket structures not initial implementation |
| 29 September–1 October 2026 | Analysis speed, risk-first selection, monitoring, multiple option alternatives, data/dashboard → prediction → automation | Core motivation; dashboard-first intent reaffirmed on 9 October |
| 2–5 October 2026 | RBI/macro reasoning, announcement risk, market forecast/audit context | Event-context research; scheduled jobs are a separate workflow |
| 6 October 2026 | Decision engine deep dive, Rob Carver/pysystemtrade, EWMAC, constituent dashboard and historical replay | Main reference-project and research discussion recovered |
| 9 October 2026 | Supplied planning baseline, resource organization, component map and dashboard usability | Current documented direction |

The map combines retrieved conversation summaries and the visible recent-history context. It does not prove every exchange in these periods has been recovered.

## Evidence labels

- Current decision: explicitly accepted project direction.
- Research hypothesis: rule or feature requiring testing.
- Source capability: described in linked primary documentation; authenticated access still needs verification.
- Prior discussion: recovered conversational conclusion; not independently audited.
- Deferred: retained alternative outside the first build.

For future additions record the conversation/date, primary link, what was verified, remaining uncertainty, affected module and experiment needed. Pin repository commits when a real code review begins. Do not copy whole articles or claim a repository's reported results are ours.
