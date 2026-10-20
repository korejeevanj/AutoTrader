# Trading.Domain

Ownership: shared synthetic dashboard, catalog/constituent/option/quote/bar, plan and journal contracts.

`DashboardContracts.cs` implements the wire records agreed in
[`09-SYNTHETIC-DASHBOARD-INCREMENT.md`](../../docs/09-SYNTHETIC-DASHBOARD-INCREMENT.md),
extended by [`10`](../../docs/10-MULTI-INDEX-AND-OPENAPI-INCREMENT.md).
The API serializes camelCase properties, including nullable evidence. Prices and
weights are decimals; observations carry distinct event, receipt and last-trade times.
Fixture contracts identify `SYNTHETIC` exchange and explicit fixture validity/lot/tick data.

`IndexDefinition` supplies exact catalog metadata/capabilities with string identities,
not a closed enum. `ConstituentRow`/`ConstituentParticipation` are index-neutral.
Schema-2 snapshots add indexId/constituents and retain deprecated banks (Bank Nifty
alias only). New selection/save requests require indexId. Snapshot additions and
journal entry indexId are optional internally to deserialize old immutable JSON;
public journal reads explicitly project the known legacy Bank Nifty identity.

`JournalBoundaries.cs` supplies `IIndexCatalog`, `IDashboardSnapshots` and `IJournalStore` for server-side
snapshot resolution and durable immutable plan entries. A journal plan has no order,
intent or fill identity. Preset position records are synthetic dashboard evidence.

No project or package dependencies. Domain code receives data/time from its hosts;
it has no clock access, broker calls or financial calculations.
