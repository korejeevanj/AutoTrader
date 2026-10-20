# Trading.Portfolio

Ownership: synthetic plan journal behavior and quote-qualified paper accounting.
References Trading.Data and Trading.Risk.

`SyntheticPlanJournal` resolves market evidence through `IDashboardSnapshots`, validates
index/scenario/snapshot/contract identity atomically, and derives checked whole-lot units with the shared
risk helper. Preview returns an indicative ask only for a fresh selected quote, plus
the unavailable planning estimates; it does not save anything.

Save accepts considered/planned/skipped status and plain-text plan fields, generates a
journal identity, and persists the complete server-resolved snapshot via `IJournalStore`.
The host supplies `savedAtUtc`; the library never reads a clock. The service returns
success only after storage completes. Client-supplied market evidence is not consumed.
`IIndexCatalog` validates selection/filter identities; every saved entry indexId matches
its snapshot and selected contract underlying. Lists optionally filter by index,
with an omitted filter returning all entries.

Reads conservatively project entry indexId=BANKNIFTY only for known schema-1
`synthetic-dashboard-v1`, synthetic Bank Nifty underlying/context evidence. Snapshot
additions remain null and original banks/version/as-of/evidence remain intact. No
stored JSON is rewritten. Unknown versions or inconsistent underlying/identity fail
instead of silently becoming Bank Nifty. Current entries require matching schema-2
entry/snapshot/contract identities, and saved reads do not regenerate fixtures.

Text limits: scenario ID 64 characters; snapshot/contract IDs 128; thesis, stopPlan and
invalidation 2,000 each; note 4,000. Plan text may be empty but cannot be null. Invalid
selection, quantities (including derived-unit overflow), status or text produce a
`PlanValidationException`, mapped to HTTP 400 Problem Details by the host.

Journal entries are plans, not paper-order intents or fills. Preset positions remain
dashboard fixtures.

`QuotePaperBroker` is the separate local simulator. It accepts trusted engine-host
approvals, reserves the one pending slot, rechecks risk/cash at a later fresh quote,
buys at ask+one tick and sells at bid-one tick with adverse rounding and whole-lot
observed depth. Cash, actual fees, pending exits, attempts and completed trades are
serializable for durable host recovery. The host commits mutations before publishing
them. The broker has no provider transport or system clock. `QuoteTradingSession`
adds the shared engine's entry/position policy; historical candle fills remain a
separate explicitly hypothetical path. Actual-account integration remains deferred.
