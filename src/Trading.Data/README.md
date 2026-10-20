# Trading.Data

## Live quote boundary

`KiteStreamingClient` connects only to the fixed Kite WebSocket endpoint and sends
subscription/full-mode commands. `KiteStreamingDecoder` preserves packet order,
separate exchange/trade/receipt timestamps and all five bid/ask levels. Heartbeats
never become quotes; original binary/text messages are retained independently.
`LiveBankNiftyReferences` joins current Kite definitions with official captured bank
membership and the agreed <=45-day published month-end weight convention.

`SqliteQuoteStore` uses WAL, explicit FULL synchronous transactions, immutable run
manifests and append-only ordered BLOB events. Identical retries are idempotent;
conflicting sequence evidence fails. Mutable paper checkpoints use a separate
database file so high-volume market writes do not contend with the paper ledger.
`BufferedPaperCheckpoints` serializes state/decision commits off the protection path;
its callback publishes acknowledged state only. Candidate/position history is stored
as compressed append-only batches. Quote-replay result writes use a third file.
Raw events preserve actual receipt and separate host admission/health/commit evidence.
Original master, membership and monthly weight ZIP evidence is retained in SQLite.
This does not imply recording throughput or power-loss guarantees have been measured.
See the [live runbook](../../docs/22-LIVE-QUOTE-PAPER-TRADING.md).

## Historical and synthetic persistence

`KiteCandleImporter` now imports local minute JSON plus `HistoricalDatasetManifest`,
normalizes offset timestamps/start/end/availability, preserves optional OI, reports
invalid rows/duplicates/gaps and retains file/manifest hashes. It makes no provider
requests. Source identity is manifest-bound; duplicate tokens/source-file aliases are
rejected. Current SQLite journal behavior below remains separate from replay reports.

`HistoricalAcquisitionRevision` owns a job's frozen inputs, download-pin reuse/refetch
rules, essential-first acquisition order and immutable attempt publication. Its
`AcquireAsync` interface accepts the existing download adapter and a progress observer;
the caller receives a completed manifest path without coordinating storage files.
The existing `acquisition.json`, `download-bindings.json` and `attempt-N` formats remain
readable. Pins are published before attempt files and progress, so an interruption
cannot silently rebind recovery to a newer shared response. File-publication failures
surface as `HistoricalStorageException`; earlier attempts remain preserved.
One acquisition worker owns a revision at a time. `FrozenAcquisition` preserves the
catalogue and opaque replay/strategy settings; this module does not interpret policy
or determine historical qualification. Temporary-filesystem tests exercise recovery
through the same interface used by the host.

`AcquisitionAttemptEvidence` owns immutable attempt-scoped progress, normalized
coverage, health totals and failed-only/gap-refetch classification. Progress and
normalization updates carry the attempt identity; obsolete attempts are rejected.
Completed/partial responses are `AwaitingValidation` until their own attempt is
normalized, with expected/missing counts and intervals unavailable rather than borrowed
from retained playback data. Empty responses can already be identified as unavailable.
`HistoricalCoverageAnalysis` supplies the same session/verified-validity/continuous-close
minute windows to both import coverage and attempt evidence. Qualification remains
with the shared replay readiness module. Existing dataset/coverage JSON is unchanged.

`NseHistoricalContractClient` retrieves official dated F&O contract masters, preserving
raw gzip/hash and the full Bank Nifty inventory. NSE epoch dates, paise strikes/ticks,
regular lot sizes and reserved/deletion/market-status fields are parsed explicitly.
Historical contract selection comes from that date, not the live Kite universe.
Exact provider joins supply tokens only; unverified arithmetic candidates are never
queried. See MSD60315/MSD55276 and the runtime setup guide for source limitations.

`BoundedSourceRead` concentrates response streaming, request/body deadlines and archive
expansion behind one internal interface used by the three source adapters. Requests
use `ResponseHeadersRead`, so declared lengths are checked before buffering and
unknown-length bodies stop after one detection byte beyond the limit. The same budgeted
stream reader handles gzip expansion and the selected weight-PDF entry.

| Source input | Response limit | Expanded limit |
| --- | --- | --- |
| Kite candles/master/error body | 64 MiB | Transport-decoded response limit |
| NSE F&O contract master | 32 MiB | 128 MiB CSV |
| Published monthly bank-weight ZIP | 64 MiB | 8 MiB selected PDF |
| NSE CM equity master | 32 MiB | 128 MiB CSV |

Kite retains its configured per-attempt deadline, spacing, retries, credentials and
code-only errors. Public reference adapters apply their `HttpClient.Timeout` across
headers and body using the supplied host clock; an infinite client timeout falls back
to 30 seconds. Caller cancellation remains cancellation. Public status handling,
publication provenance, archive interpretation and cache publication remain with each
adapter. The module does not choose historical sources or replace unavailable evidence.
Tests exercise the existing adapter interfaces with declared/unknown sizes, stalled
bodies, cancellation, compressed limits and valid cached reference fixtures.

The replay journal's `paper_run_openings` stage atomically binds a run/input identity
to its frozen manifest bytes and comparison scope before session attachment. It remains
until the immutable manifest and host checkpoint are durable, so a crash cannot lose
an opening merely because `job.json` still names the preceding run/preset. Conflicting
opening bytes are rejected; current scope can be updated independently of the frozen
manifest. Acknowledgement removes only a stage whose final manifest exists. Prior run
manifests, batches and published reports are not rewritten.
Stage acknowledgement is retryable cleanup after the manifest and host checkpoint
are durable. A cleanup failure safely retains the stage for a later checkpoint.

Ownership: SQLite journal access and future provider/persistence adaptation.
References Trading.Domain and exact Microsoft.Data.Sqlite 10.0.12.

`SqliteJournalStore` implements append-only `IJournalStore` with the minimal
`journal_plans` table: primary-key `id`, indexed `saved_at_utc`, and complete immutable
`entry_json`. JSON includes every original snapshot field and schema/fixture provenance.
There are no update/delete/reset operations. Reads deserialize stored evidence rather
than regenerate it from the current fixtures. Lists sort saved UTC time descending,
then insertion order descending to resolve equal save times.

Schema-2 entries carry explicit index identity inside complete entry JSON. The SQLite
table remains unchanged: no identity column migration or historical JSON rewrite is
needed. Optional Domain fields deserialize schema-1 evidence; Portfolio owns the
conservative known-legacy identity projection/filter. HTTP restart tests insert literal
pre-increment JSON and assert its raw stored value remains byte-for-byte unchanged.

Schema initialization is lazy on the first journal save/list/read. Schema creation and
each save use transactions. Connections/commands/readers are disposed per operation;
pooling is disabled, and SQLite's 30-second busy timeout coordinates concurrent writers.
An initialization semaphore prevents overlapping setup within a store instance.

`JournalStorageOptions.SqliteJournalPath` is bound by the API, which resolves it against
its content root (`src/Trading.Api` locally). The default is `../../data/autotrader.db`;
absolute overrides work. The store receives the resolved absolute path and creates its
parent directory only on journal access. Startup, health, snapshots and preview do not
open SQLite. Provider adapters and paper-order state remain future work.
