# Replay lab: controls, data health and research evidence

The [historical readiness contract](15-HISTORICAL-DATA-READINESS.md) adds unified
input qualification, essential-first acquisition and explicit coverage-gap refresh.

## Workflow

1. Select a completed historical date. Preparation returns a job immediately and
   resolves/downloads/validates in the background. Existing journal indexes may need
   initialization on the first launch after an upgrade; this is shown as preparation,
   rather than blocking the date request.
2. Inspect the persistent **data-health strip**. Processed requests are not necessarily
   complete or usable series. Open **Data health** for counts, instrument errors,
   missing intervals and their impact on setup/bank/candidate gates.
3. Use **Play** for paced replay, **Step** for exactly the next scheduled event, or
   **Run Full Day** to finish the current preset once at maximum throughput. Use
   **Run all presets** explicitly for a fresh comparison across configured presets;
   the active preset and counter make each deliberate clock restart visible.
4. Use **Decisions → Recent snapshot** for grouped highlights and blockers, or **Full
   run journal** to browse/search every committed assessment and inspect its full gates.
5. Open **Run evidence** for operational errors, downloads, earlier runs and comparison.

The workspace has four keyboard-accessible tabs: Overview, Decisions, Data health and
Run evidence. Tables/feed windows are bounded and paginated. Journal pages remain frozen
while inspecting them; **Refresh latest events** explicitly returns to the newest page.
The chart instance survives polling, preserving manual zoom/pan. **Fit replay chart**
restores a fitted view. Chart labels and time axes use Asia/Kolkata.

The browser's replay display module owns selection and response acceptance for polls
and commands together. Retired-server or older-revision responses cannot replace
accepted playback; health must match the acquisition, and lifecycle rows must match
the replay run. Changing selections cancels obsolete reads/commands; a preparation
response is still observed so an obsolete remote preparation can be cancelled by ID.
Rendering consumes accepted evidence without coordinating those protocols.

## Timing contract

- The host owns a monotonic pacing clock. The first event is immediate; subsequent
  events are due at their historical offset divided by requested speed.
- A one-minute interval at **1×** takes sixty seconds. There is no five-second cap.
- Processing time is subtracted from remaining wait; it is not added to every interval.
- Speed changes are acknowledged by `setSpeed` immediately and wake an outstanding
  wait. Pause/resume and speed changes re-anchor timing; paused time is not caught up.
- Requested speed, achieved speed, processing duration and lag have separate labels.
  Achieved speed is simulated seconds per wall second for the current pacing segment.
  An unsupported throughput target is disclosed as lag, never implemented by skipping
  historical events or dropping decisions.
- Maximum throughput is a separate mode; the paced-speed selector is disabled during
  it. UI snapshot delivery is independent of event evaluation/persistence.
- Speed mode and preset scope are independent. `run` finishes only the current
  preset and publishes its report. `runAllPresets` explicitly starts from the first
  preset (opening a new run if playback has already progressed), advances each preset
  once and publishes the combined report. Pausing/resuming with Play preserves an
  explicitly requested comparison; Restart clears it. No preset advances occur after
  ordinary Run Full Day. `presetProgress` exposes ID, one-based number, total and scope.
  If recovery finds the active preset already committed as complete, Play continues
  any remaining comparison presets within that same run instead of restarting it.
  Manual stepping shares that progression: its final event can install the next
  comparison preset paused, without evaluating that preset's first event yet.
- The host's `DurableReplaySession` adapter persists full decision batches before
  releasing in-memory decision history. Policy state is unchanged by releasing that
  history. CLI/library callers retain the normal complete trace unless they opt into
  the durable adapter or explicitly release committed history themselves.

## Data health

Complete, sparse/partial, empty, failed, invalid and pending are distinct. Per-instrument
rows include returned/expected minute counts, missing intervals (end exclusive), cache
origin, error codes, validation issues and decision impact. Expected coverage honors
session bounds and bank continuous-trading close times.

- Missing futures/bank observations block dependent decisions; missing bank weight is
  not renormalized.
- Missing option observations block the affected contract. Other contracts can be
  inspected diagnostically; unresolved options make the selection assessment
  incomplete and exclude the session from qualified aggregate performance.
- Empty successful responses remain source limitations, not proof of inactivity or a
  holiday, and are not automatically retried as transport failures.
- **Retry failed requests** downloads only failed instruments, reusing successful
  pinned responses including partial required series. It requires paused playback and
  creates a new acquisition attempt/run boundary. Existing run evidence is immutable.
- **Refresh coverage gaps** explicitly requests new responses for failed, sparse,
  empty and invalid series, preserving other pins and all earlier attempt/run files.
  It does not guarantee unavailable provider history exists. Dated reference-data
  repairs require a new preparation revision, preserving old frozen provenance.
- Data health shows the shared qualification result, incomplete decision-time counts,
  unknown-option counts and reference-data blockers. Complete candle rows alone do
  not establish readiness. Run-list playback completion and data qualification are
  separate; old records without qualification remain unestablished.
- Job/attempt identity prevents a response from an older acquisition from qualifying
  the current one. Failed data-health reads clear the previous positive UI state.
  `serverInstanceId` scopes snapshot revisions across API restarts and prevents
  delayed responses from a retired process from overwriting recovered state.
  Completed playback with unresolved evidence is labelled explicitly; human-readable
  reference blockers explain why a candle refresh cannot repair publication evidence.
- Provider attempts record sanitized status, attempt number and elapsed duration.
  Each attempt has an overall deadline including response-body reads and a 64 MiB
  streaming response bound; provider bodies/authorization headers are not logged.
  Public NSE/published-bank adapters also stream with a deadline covering headers
  and body. Contract/equity responses are limited to 32 MiB, weight archives to
  64 MiB, expanded CSV to 128 MiB and the selected weight PDF to 8 MiB. Status and
  publication-evidence rules remain source-specific; resource bounds do not qualify
  missing or unverified historical evidence.

## Durable records and downloads

`data/replay/paper-replay.sqlite` remains authoritative. It contains:

- immutable run manifests with date/preset, full contract/weight manifest, source file
  hashes, code assembly versions, frozen configurations, quality and input fingerprint;
- compressed complete evaluation batches and indexed events with event-to-batch mappings;
- account/position checkpoints, entry-control history and operational records tagged
  with job, run, preset, wall UTC, simulated UTC, severity and code.

New batch identities hash the full transaction envelope, including checkpoint, controls
and indexed events. Conflicting batches/event identities are rejected. A failed step
commit remains pending and must be committed before advancing. A failed entry-control
commit restores the previous setting and pauses playback before another event advances.
Operational-log failure is exposed and pauses replay rather than silently losing logs.

Run opening and preset progression share a run-lifecycle owner. A new session's
manifest must be durable before evaluation or paper-control writes. Failed manifest
publication leaves a paused, visibly pending run; retrying Step/Play publishes that
same frozen manifest before advancing, without allocating a replacement run. Report
eligibility is based on the requested preset scope, independently of input qualification.
The opening is staged with its run/input identity in the journal before session
attachment, then acknowledged after the host checkpoint succeeds. Its frozen bytes
and comparison scope survive process restart even if both final-manifest publication
and the host checkpoint failed. Recovery excludes incompatible input/policy variants.
First-open failure leaves the prepared dataset paused and retryable. Retrying preparation
also recovers compatible completed playback/reports within the same process. Every new
run clears the preceding run's report-availability flag.
Acknowledgement cleanup failure retains the safe stage and is retried by a later
checkpoint, without stopping the worker or preventing an already-durable run advancing.
Explicit refetch intent remains distinct from preparation recovery: a refetch opens
a new attempt/run even when its content hash is unchanged. A staged opening for that
new run can itself be recovered without allocating a further replacement.

**Download evidence** streams `.jsonl.gz` for one run, including its manifest/checkpoint,
operations and complete evaluation batches. The export captures a durable high-water
mark, so a concurrent replay cannot append later batches beyond the exported checkpoint.
Evaluation payloads preserve the existing PascalCase .NET batch schema; their outer
record uses `type: "evaluationBatch"` and `evidence`. Decision records retain rank and
model ranking inputs where available, as well as detailed gates. Rank is not probability.

Acquisition/runtime log downloads work before strategy readiness, and evidence remains
available after cancellation/failure and API restart. The UI shows the latest 100
operations; downloads retain the whole history. Final reports additionally contain
`runtime.jsonl.gz`. Reports stream decisions from the journal and stream CSV output,
without retaining the full-universe decision graph or CSV rows in memory.

Publication uses a unique staging directory and a completion marker before an atomic
directory move. Incomplete prior output is preserved under an `.incomplete-*` name;
completed evidence is never overwritten. **Retry report publication** can republish a
completed replay without replaying its decisions or duplicating fills.

Old reports remain downloadable as originally published. Earlier indexed events without
event-to-batch mappings may lack on-demand detail; their complete report remains the
fallback. Historical runs without the new manifest metadata are not given invented
dataset identities for comparison.

## Research comparison

Compare completed runs from the same date and identical frozen dataset hash, including
runs from different jobs/configurations. The page shows preset/input-version differences,
paper outcome summaries and the first 100 changed action/reason records. It distinguishes
events present in only one run. Download both evidence files for all gates, ranking
inputs and outcomes; this view does not claim a full numeric gate-by-gate diff.

## API additions

| Route | Purpose |
| --- | --- |
| `GET /api/replay/jobs/{id}?light=true` | Immutable revisioned snapshot without instrument rows or detailed gate payloads |
| `POST /api/replay/jobs/{id}/commands` | Adds `setSpeed`, `retryFailed`, `refreshGaps`, `runAllPresets`, `setProfitExit`, `publish` |
| `GET /api/replay/jobs/{id}/data-health` | Validated counts, source limitations, instrument issues and missing intervals |
| `GET /api/replay/jobs/{id}/diagnostics` | Recent operational logs and run summaries |
| `GET /api/replay/jobs/{id}/operations` | Complete acquisition/runtime `.jsonl.gz` |
| `GET /api/replay/runs?date=yyyy-MM-dd` | Latest 200 run summaries for a date |
| `GET /api/replay/jobs/{id}/events?runId=...&before=...&limit=25&category=...&search=...` | Stable descending sequence cursor over full committed history |
| `GET /api/replay/jobs/{id}/events/{sequence}?runId=...` | Full decision and gates from its durable batch |
| `GET /api/replay/jobs/{id}/evidence?runId=...` | Snapshot-consistent compressed run evidence |
| `GET /api/replay/jobs/{id}/compare?left=...&right=...` | Same-dataset completed-run action/reason comparison |

## Acceptance checks

Backend checks cover literal one-minute/1× timing, compensation/re-anchoring, immediate
speed changes and interrupted waits, failed-control rollback, coherent pending-step
recovery, full cursor pages/details across restart, failed-only downloads, empty coverage,
bounded stalled-body handling, conflicting evidence rejection and equal manual/maximum
decision histories. Browser checks cover desktop/mobile layout, speed acknowledgements,
data-health impacts, stable journal inspection, tab navigation and downloads.

The research mode remains historical candle simulation; strict execution qualification
does not acquire fabricated quote/depth evidence. All provider calls remain read-only.
