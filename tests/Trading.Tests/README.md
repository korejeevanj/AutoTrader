# Trading.Tests

Ownership: verification through agreed public HTTP interfaces. Tests use the real
ASP.NET Core pipeline through WebApplicationFactory and real temporary SQLite files.

- Startup/health returns exactly the synthetic, paper-auto-paused contract. This
  verifies application connectivity and honest scaffold status, not trading logic
  or feed health. The test was run red (404) before mapping the endpoint.
- Public SignalR negotiation returns a connection identity/token and advertises
  WebSockets with text transfer. It does not establish market-message delivery.
- `SyntheticDashboardTests`: repeatable snapshot/provenance, 24 stable synthetic
  contracts across exactly five scenarios, completed-bar availability, hand-checked
  opening-range/VWAP values, missing weight without renormalization, and explicit
  missing/stale/disconnected/unpriced states.
- `JournalTests`: server-derived units, immutable original evidence (even when a
  client submits a fake snapshot), newest-first listing, read-after-save and recovery
  using a new backend host against the same SQLite file; unknown read IDs return 404.
  Invalid IDs/lots/status/text return useful 400 Problem Details and save nothing.
  Preview derives units and withholds missing/stale/disconnected ask references,
  preserving unavailable planning fields and creating no entries.
- `MultiIndexTests`: exact backend catalog fields, distinct prices/strikes/lot/tick
  seeds, all 15 index/scenario combinations, stable synthetic contracts, completed
  overlays, missing weight, default Bank compatibility, index-safe preview/save and
  per-index/all-index journal recovery. MIDCAP is added via options alone through HTTP.
  Independent and combined disabled capability flags are exercised across all five
  scenarios: empty chart/options, null weighted aggregates with retained observations,
  rejected formerly valid option selections, and no durable plans after restart.
- `JournalTests` also insert literal pre-increment schema-1 JSON at the database
  setup boundary, read/filter it through HTTP alongside a new Nifty plan, reopen the
  same database in another host, and inspect raw JSON only to assert no mutation.
  Unknown legacy fixture versions or Nifty underlying are never relabelled as Bank.
- `OpenApiTests`: Development Swagger/document availability, actual typed success
  and Problem Details schemas for all eight REST operations, required selection/text
  fields, whole-lot/status descriptions, deprecated banks, named health DTO, and
  Production document/UI 404 with health still available.
  Fixed response enums/constants are asserted through the public document, while
  catalog/underlying/index identities remain extensible strings without enum values.

Journal tests use unique `autotrader-*.db` paths under
`Path.Combine(Path.GetTempPath(), "opencode")` (the approved temporary directory on
this machine), remove files after disposing all hosts, and override settings through
`PostConfigure<JournalStorageOptions>` in ConfigureServices. A fixed TimeProvider is
injected only at the clock boundary to verify the host supplies saved timestamps.
Fixtures retain their own fixed as-of. No private methods are tested. Direct SQL is
limited to legacy setup and the no-mutation invariant; behavior assertions use HTTP.

### Observed vertical red/green slices

Each behavior test was added and run red before its implementation:

| Slice | Observed red | Green |
| --- | --- | --- |
| Deterministic snapshot/chart | Snapshot endpoint 404 | 1 passed |
| Scenario evidence states | missing-inputs reported fresh rather than partial | 2 snapshot tests passed |
| Save/read/restart | Save endpoint 404 | Recovery test passed |
| Validation | Unsupported executed status returned 201 | 2 journal tests passed |
| Preview | POST preview returned 405 | All 5 increment tests passed |
| Strict numeric lots | Quoted numeric lots returned 201 | Validation test passed |
| Multi-index catalog/snapshots | Catalog endpoint 404 | All configured index/scenario checks passed |
| Index-aware preview/save/filter | MIDCAP preview 400; saved entry indexId absent | Catalog-added preview and cross-index/restart checks passed |
| Legacy projection/no rewrite | Legacy identity absent; unknown records returned 200 | HTTP legacy/restart/no-mutation and unknown-identity checks passed |
| Development OpenAPI/UI | Swagger returned 404 | Concrete metadata/schema and Production absence checks passed |
| Disabled capabilities | Disabled chart/options still returned data; disabled weights remained non-null | Four configuration cases passed across all scenarios, including rejected save/preview and empty journal recovery |
| Response fixed values | Quote.state had no schema enum | Public response enums/constants and extensible identity assertions passed |

Exact stable direct packages: Microsoft.NET.Test.Sdk 18.10.1,
Microsoft.AspNetCore.Mvc.Testing 10.0.12, xunit 2.9.3 and
xunit.runner.visualstudio 4.0.1. The only project reference is Trading.Api.
See `src/Trading.Api/README.md` for targeted commands and the reference graph.

Future trading tests require approved implementation and the sprint verification
seams (timing/future isolation, deterministic replay, accounting, delayed bid/ask
fills, stale/restart recovery and real-order isolation). Those capabilities are
not implemented or proven by this synthetic dashboard increment.
