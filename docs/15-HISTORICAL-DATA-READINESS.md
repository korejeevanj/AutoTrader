# Historical data reliability and readiness

Agreed: 19 October 2026. Historical preparation/replay is the first increment;
live feed and quote recording remain a subsequent deliverable.

## Completion contract

Three states are separate: diagnostic playback available, acquisition processed,
and historical input evidence qualified. `CanReplay` and `PreparationComplete`
do not certify strategy evidence. A completed playback may be unqualified.

`HistoricalReadiness` supplies the same rules to preparation/data health and replay.
At every configured one-minute entry decision time (including publication lag), it
checks calendar evidence, the declared full option universe, applicable fees, bank
weights, required completed session prefixes and each potentially eligible option.
Full-session required-series integrity is also checked before dataset qualification.

- Acquire cash-index context, the selected future and all required banks before
  the full date-valid option catalogue. Candidate batch size is not a universe cap.
- Missing observations stay unknown; no forward filling or weight renormalization.
- A current completed option candle is usable evidence for the candle model only.
  Observed zero volume is an explicit candle-policy exclusion. Expiry-policy
  exclusions are recorded. Empty/sparse/failed responses do not prove inactivity.
- Catalogue preparation preserves unresolved option definitions. An unqualified
  expiry, validity interval or end-of-day-only identity cannot silently remove
  a contract before readiness checks. Provenance is checked before exclusions.
- The qualification audit conservatively covers both strategy-permitted option
  rights throughout the entry window; it does not select a favourable subset based
  on whichever contracts were returned.
- Any unresolved potentially eligible option makes selection evidence incomplete.
  Observed contracts may still produce labelled diagnostic model outcomes, but
  the session is excluded from qualified aggregate performance.
- Later observations cannot repair an earlier decision's evidence. Qualification
  uses the supplied replay clock, never the machine clock.
- A fully evidenced zero-trade session is valid. A data gap is not evidence that
  no opportunity existed.

`DATA_READINESS` events retain reason codes and the unknown instrument identities
in the durable decision journal. Step/result readiness includes option usability,
exclusions and required-prefix details. `quality.json`, `results.json` and the
summary retain qualification. Run-list `complete` means playback finished;
`qualified` is separate and null for older records without this evidence.

## Reference-data qualification

`ReplayConfiguration.MaximumWeightSnapshotAgeDays` defaults to **45 calendar days**.
The boundary is inclusive: 45 days is allowed, 46 days is not. It is a configurable
research threshold, not an exchange rule. Monthly weights remain labelled as
carried forward, not actual daily weights. The downloader stops looking when a
prior-month snapshot would exceed the configured age.

Record a known constituent change by ending the old weight set's `EffectiveToUtc`
at the actual effective boundary and providing its dated replacement. An ended
snapshot cannot qualify later confirmation; overlapping/missing/invalid weight
sets remain insufficient evidence. Preserve the source of the change in the
versioned reference-data document rather than editing an already frozen run.

Weights have optional `PublicationEvidence`. Calendar documents have the same
field, copied to manifest `CalendarPublicationEvidence`. Its shape is:

```text
Method: OfficialPublication | ContemporaneousCapture | HttpLastModified
Source: location/reference of the preserved supporting evidence
EstablishedAtUtc: earliest availability established by that evidence
Sha256: 64-hex SHA-256 of the supporting artifact
```

Fully qualified evidence requires `OfficialPublication` or a trustworthy
`ContemporaneousCapture`, a source/hash and an established time no later than the
decision. Weight `AvailableAtUtc` cannot precede that established time. The code
validates the declared evidence contract; obtaining/verifying a real source
artifact is still necessary. Do not manufacture publication timestamps or hashes.

An HTTP `Last-Modified` header alone does not establish original publication.
The automatic monthly-weight importer preserves it as `HttpLastModified` and
therefore permits **diagnostic research only** until stronger evidence is supplied.
Legacy reference documents lacking evidence remain readable but unqualified.
For a CLI manifest, also supply `IncludesAllOptions`, `CalendarVerified` and
`CalendarSource`; placeholders in the example remain explicitly unqualified.

## Recovery workflow

1. Start/restart the updated API and prepare the selected completed historical day.
2. Inspect **Data health**, including reference-data reason codes, incomplete
   decision-time counts, unknown-option counts and per-instrument recovery actions.
3. **Retry preparation** resumes the pinned acquisition/configuration. It does not
   silently replace successful empty/partial option responses.
4. **Retry failed requests** refetches request failures only.
5. **Refresh coverage gaps** (`refreshGaps`) explicitly refetches failed, sparse,
   empty or invalid series in a new acquisition attempt. Other pinned inputs and
   earlier attempt/run files remain unchanged. Pause playback first.
6. Use **Refresh from Kite** to create a new job/revision after changing dated
   metadata or publication evidence. Recovery of an old job intentionally keeps
   its frozen reference data/configuration.

Provider transient retries remain bounded. A successful empty response is not
automatically retried indefinitely. Re-requesting unavailable provider history
may return the same gap; it remains unknown and cannot gain a qualification label.
Unverified mappings require dated identity repair, not repeated arithmetic-token
requests. Authentication and entitlement failures retain their distinct codes.

Checkpoint readers allow atomic replacement. Publication retries short-lived
Windows reader/sharing conflicts for a bounded period while preserving the old
checkpoint. Persistent failures expose `LOCAL_STORAGE_UNAVAILABLE`, pause the
affected job and leave the acquisition worker available for recovery/other jobs.
Restore storage/write access and retry; source data already cached is reused.
A retry received after failure is published but before acquisition cleanup finishes
is retained and queued once that worker stops. The same handoff applies to retries
during cancellation; observing a failed state does not require waiting for an idle
worker before requesting recovery.

Data health belongs to the acquisition attempt rather than the retained playback
dataset. Fresh complete/partial responses remain `AwaitingValidation` (counted under
pending) until that attempt's normalization finishes. Expected/missing counts, issues
and intervals stay unavailable during this phase; previous-attempt evidence cannot
appear under the current attempt tag. Empty responses remain explicitly unavailable.
Import and health share the same verified validity/continuous-close coverage rules;
gap-refetch classification uses that matched evidence, while qualification stays separate.

## Why preserve data returned by Kite?

Kite is a source, while local storage preserves exactly what each experiment used.
Content-addressed raw responses, attempt manifests, reference provenance, frozen
configuration and durable evaluation batches make changes reproducible and
downloads resumable. Unchanged raw revisions can be shared between runs.

Kite historical candles do not contain historical bid/ask/depth sequences. Its
current instrument master also drops expired contracts. Preserving definitions
helps identify past contracts but does not guarantee retained candle history.
Local storage cannot manufacture missing provider data. Live quote recording is
needed to preserve future observed quote sequences for later replay.

Recovery reads an existing acquisition's frozen calendar before consulting mutable
calendar files. A failed retry-command checkpoint exposes a failed state rather
than leaving an unqueued `Queued` job. Dated quality errors are scoped to their
own trading session; undated source-integrity errors remain dataset-wide.

References: [Kite historical data](https://kite.trade/docs/connect/v3/historical/),
[instruments/quotes](https://kite.trade/docs/connect/v3/market-quotes/),
[WebSocket fields](https://kite.trade/docs/connect/v3/websocket/).

## Verification boundaries

### Workflow consistency follow-up

- Completed playback is labelled separately from incomplete evidence. Reference
  blockers have plain-language causes and recovery guidance; completed failures
  are not described as merely pending validation.
- Data-health responses carry job/attempt identity. Failed reads and responses
  from an earlier acquisition cannot retain a green current qualification.
- Snapshots and data health also carry a server-instance identity. Revisions are
  ordered within that instance; restart resets are accepted, while delayed
  responses from a retired instance cannot overwrite recovered state.
- Closed sessions and authentication blocks have explicit readiness states.
  Price-only playback does not claim that strategy evidence was evaluated.
- Completed-run recovery retains immutable report availability and completion;
  idle cancellation cannot relabel a published run as cancelled. Preview promotion
  opens a coherently ready, paused strategy session. Explicit comparison scope
  is persisted and restored alongside the active preset/checkpoint.
- A refetch verifies an existing content-addressed file. Corrupt bytes are
  quarantined before repair and `CACHE_SOURCE_REPAIRED` is recorded. Earlier
  attempt/report evidence is not rewritten.
- Exact provider mappings are searched across preserved master archives, scoped
  to requested symbols. A newer master dropping an expired contract no longer
  hides an older exact join; dated exchange definitions still own the universe.
- Intraday weight replacements remain in the frozen catalogue and are admitted
  only at their effective/availability time. Required banks follow that dated
  membership: unused bank definitions are not required inputs, and a departing
  bank's prefix ends at its membership boundary. No missing weight is renormalized.
- Option quality is scoped to the current completed assessment candle and actual
  trade interval; an unrelated earlier option conflict does not poison later clean
  evidence. Required index/futures/bank prefixes remain complete, and errors in a
  future bar cannot affect an earlier decision, including with publication lag.
- Missing-interval displays use the same verified validity-window exclusions as
  import coverage. Reports show aggregate performance **unavailable** when no
  session is qualified, rather than an apparent zero-profit result.

These changes do not supply missing official publication evidence or unavailable
provider history. The underlying source blockers remain explicit until resolved.

Acquisition/store API tests cover essential-first ordering, transient checkpoint
locks, persistent storage recovery, targeted refetch and immutable earlier attempts.
Replay/session/report tests cover missing prefixes/options, later-data isolation,
zero-volume exclusions, the 45-day boundary, ended weights, publication provenance,
calendar/universe qualification and aggregate exclusion. Browser checks cover the
qualification display and gap-refresh command on desktop/mobile. Simulated provider
responses verify software behavior; they do not establish actual Kite coverage.
