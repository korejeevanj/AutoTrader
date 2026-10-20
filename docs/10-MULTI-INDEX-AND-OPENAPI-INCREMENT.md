# Multi-index cockpit and OpenAPI increment

Confirmed user scope: this session owns Swagger, UI improvements and related
backend changes. A separate session owns `Trading.Engine` and dedicated engine
tests. Coordinate shared Domain/Analytics/Risk changes; preserve existing work.

This extends `09-SYNTHETIC-DASHBOARD-INCREMENT.md`. The Monday live-paper scope
still comes from `08-MONDAY-LIVE-PAPER-SPRINT.md`. Three synthetic workspaces do
not imply three implemented live strategies or providers.

## Swagger

- .NET 10 built-in OpenAPI generation plus Swagger UI.
- Development only: `/swagger` and `/openapi/v1.json` on the API origin.
- Explicit response/request schemas, operation IDs, tags, summaries and validation
  errors for every REST operation. SignalR is not a REST trading operation.
- Schema descriptions identify fixtures, missing estimates, whole-lot validation,
  journal plans versus intents/fills, supported identifiers and enum-like values.
- Keep existing health behavior. Avoid deprecated `.WithOpenApi()`.
- Verify Development visibility and Production absence, and that the generated
  document contains actual success/error schemas, not just untyped route entries.

## Index catalog and fixtures

- Initial identities: `BANKNIFTY`, `NIFTY`, `SENSEX`.
- `GET /api/dashboard/indices` returns `IndexDefinition[]`, supplied by backend
  configuration rather than a frontend hard-coded enumeration. The frontend accepts
  additional catalog identities without source changes.
- `IndexDefinition` wire properties:

  ```typescript
  interface IndexDefinition {
    id: string; displayName: string; exchange: string;
    timeZone: string; constituentLabel: string; definitionVersion: string;
    effectiveFromUtc: string; effectiveToUtc: string | null; isSynthetic: true;
    capabilities: {
      futuresChart: boolean; optionComparison: boolean; weightedConstituents: boolean;
    };
  }
  ```

- `exchange` identifies the index venue; fixture contract exchange remains
  `SYNTHETIC`. Catalog dates/capabilities/weights are illustrative fixture metadata,
  not verified instrument-master or historical constituent evidence.
- Each index has its own configured fixture price/strike/lot/tick seeds, futures
  symbol, contract IDs and illustrative constituent data. Shared fixture generation
  and shared .NET analytics avoid symbol-specific copies of the workflow.
- All five existing scenarios work for each index. Stable contract IDs across an
  index's scenarios; different IDs across indices. All snapshot/position identities
  include the index. Preserve completed-bar/VWAP alignment and missing-weight rules.
- Lot/tick/expiry validity remains on contracts. Index definitions, per-index
  strategy configuration and portfolio-wide controls are separate concerns.
  A Bank Nifty confirmation rule is not automatically a Nifty/Sensex strategy.
- Capability flags describe actual fixture availability. A disabled futures chart
  has no bars/VWAP/opening range or current futures value; disabled option comparison
  has no options or preset held-position evidence and cannot preview/save a contract.
  Disabled weighted constituents retain observations but expose null weights/aggregate
  participation and explicit unavailable coverage. The UI displays unavailable panels
  and validates these conditions against catalog metadata. Initial three fixtures
  enable all capabilities; other configured identities may disable them independently.

## Extended public contract

- `GET /api/dashboard/snapshot?indexId=NIFTY&scenarioId=normal` selects both.
  Omitted index selects `BANKNIFTY` for existing dashboard links; unknown index or
  scenario returns HTTP 404 Problem Details.
- Current `DashboardSnapshot` adds `indexId: string` and
  `constituents: ConstituentRow[]`. `ConstituentRow` has the existing BankRow fields.
  Current snapshots use schema version `2`, index-scoped fixture/snapshot identities.
- Retain `banks` as a deprecated compatibility field: the Bank Nifty constituent
  alias, empty for other current indices. New UI uses `constituents` and the catalog's
  `constituentLabel`; no generic module requires BANKNIFTY.
- Both preview and save requests add **required `indexId: string`** to the existing
  selection fields. Resolve index + scenario + snapshot + contract consistently;
  reject cross-index evidence even if the client sends valid individual identifiers.
- New journal entries include `indexId: string`; their snapshots include it too.
- `GET /api/journal/plans?indexId=NIFTY` filters by index; omitting it returns all
  indices. Unknown filter identifiers return HTTP 400 Problem Details.
- Existing success statuses, routes, explicit null estimates and clock semantics
  remain. Plain-text saved notes and server-resolved market evidence remain immutable.

## Saved-journal compatibility

- Never reset/delete the development SQLite file or rewrite old plan evidence.
- Existing schema-1 Bank Nifty entries lack indexId/constituents. Their known Bank
  Nifty identity can be projected for list/filter/display without changing stored JSON.
  Legacy snapshots retain their schema/version/as-of/contract/quote/bank evidence.
- Backend snapshot additions must deserialize legacy JSON: nullable/optional new
  snapshot fields are acceptable internally. Returned legacy journal entries must
  identify BANKNIFTY explicitly; the UI can adapt legacy `banks` to the constituent
  view while preserving the original payload for evidence inspection.
- Do not infer BANKNIFTY for arbitrary unknown versions/underlyings. The bridge is
  specific to known schema-1 synthetic Bank Nifty records.
- Current snapshot reads require explicit index metadata and validate response
  identity against the requested catalog index. Saved-entry reads support the known
  legacy version as well as the new version.
- Verify legacy compatibility through HTTP using pre-increment persisted JSON,
  and re-open the same database with a new backend host.

## UI behavior

- One selected-index cockpit, initially Bank Nifty, with a backend-driven selector.
- Generic constituent panel; Bank Nifty may retain its specialized bank label.
- Switching indices clears active selection, pins and frozen comparison; briefly
  show that the comparison was cleared. Scenarios may remain selected consistently.
- Preserve unsaved drafts keyed by index + contract in the browser tab; reopening
  that contract restores edits. Disable index switching while a save is pending.
- Obsolete async responses cannot replace a newer index/scenario/preview/filter.
- Shared journal defaults to all indices, provides catalog-driven filtering and
  labels every entry/evidence with its index. Filtering does not erase stored entries.
- Chart, candidates, selection, preview, position states and saved evidence belong
  to their own index. Frozen observations never silently carry into another workspace.
- All visible market data remains `SYNTHETIC TEST`; financial estimates remain null
  where unimplemented. No aggregate cash/risk/position claims or execution controls
  are introduced by showing multiple workspaces.

## Verification and completion

Agreed seams: real public HTTP catalog/snapshot/journal/OpenAPI, and public browser
workflow. Use meaningful vertical red/green slices at these seams.

1. Swagger/OpenAPI available in Development only, documenting exact responses,
   required request index identity, errors and every current REST operation.
2. Distinct catalog-driven workspaces for all three indices, deterministic fixtures,
   whole-lot preview and rejected cross-index save selections.
3. Saved records for multiple indices, all-index/per-index reads, legacy Bank Nifty
   evidence unchanged in storage and readable after restart.
4. Index-switch selection/comparison reset, per-index draft restoration, disabled
   switching during save and obsolete-response isolation.
5. Existing synthetic/stale/failure/recovery/accessibility workflows still pass on
   desktop/mobile. Run locked restore, backend build/tests, frontend typecheck/build
   and browser E2E with free ports and isolated journal storage.
6. Update startup/API/UI documentation, then run the local API/UI and verify the
   dashboard and Swagger URLs. Do not change engine-session files or commit.
