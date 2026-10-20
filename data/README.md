# Private local data

This directory is reserved for the local SQLite journal/config/paper database and
market datasets. Generated/private contents are ignored; folder documentation is
tracked. The first journal read/save creates `autotrader.db` and its synthetic plan
schema. Startup/health/snapshot requests do not initialize storage; market datasets
and the future paper ledger are not populated yet.

- `raw/`: append-only versioned observations and original provider responses.
- `normalized/`: validated canonical datasets for replay.
- `quality-reports/`: coverage, gap, duplicate and validation reports.

Preserve source, event/receipt timestamps and dataset provenance when implementing
storage. See [data and replay rules](../docs/03-DATA-AND-REPLAY.md).
