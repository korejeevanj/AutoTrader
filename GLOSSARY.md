# AutoTrader

Historical preparation and paper replay preserve the evidence used by each experiment.

## Language

**Frozen acquisition**:
The selected historical contract catalogue, reference evidence and replay/strategy settings preserved for a preparation job. Recovery uses these original inputs rather than later replacements.

**Acquisition revision**:
A preparation job's frozen inputs together with its selected raw-response revisions. Explicit gap refresh may replace selected responses for a later attempt while preserving earlier evidence.

**Acquisition attempt**:
One pass through a frozen acquisition's instruments, retaining its own responses and dataset description. An attempt being processed does not establish that its historical evidence is qualified.

**Download pin**:
The identity and content hash of the exact raw response selected for an instrument in an acquisition revision. A newer shared response does not silently replace a download pin.

**Durable replay evidence**:
The committed evaluation batches, indexed decisions, paper-state checkpoints and entry-control history belonging to one replay run. An evaluation awaiting commitment remains pending and must be committed before the replay advances again.

**Replay run**:
One execution of a frozen dataset and policy under a selected execution preset. Its identity distinguishes it from restarts, policy variants and other presets, even when their historical dates match.

**Replay display**:
The selected historical day's accepted playback, data-health and paper-event evidence. Evidence from an obsolete selection, retired server, earlier acquisition attempt or different replay run is not current display evidence.

**Run manifest**:
The preserved dataset, policy, execution-preset and code provenance identifying a replay run. A manifest identifies the experiment separately from its evaluation batches and results.

**Run identity**:
The input hash and code versions derived from a replay run's frozen dataset, replay and engine settings and the replay-behaviour assemblies. A saved replay run is recoverable only while its run identity still matches; host-only code does not contribute to it.

**Preparation verdict**:
The outcome of preparing one historical day: the replay and engine settings after the preparation gates (incomplete option coverage and special sessions disable candle research and strategies), the loaded dataset, its qualification and every warning. A day is Ready only when it has no warnings and its qualification holds; hosts record the verdict rather than re-deriving it.

**Attempt evidence**:
Response progress and normalized coverage belonging to one acquisition attempt. A processed response is distinct from validated coverage and qualified strategy evidence.

**Pending run opening**:
A replay run's preserved identity, frozen manifest and requested comparison scope awaiting completed publication/checkpoint acknowledgement. It remains distinct from a pending evaluation batch or paper entry intent.
