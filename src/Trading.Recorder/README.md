# Trading.Recorder

Ownership: bounded received-event recording buffers and recording-health reporting,
hosted inside the single API process.
Recording and position monitoring must be independent of UI delivery; compromised
recording completeness must expose degraded state and inhibit entries.

`BufferedQuoteRecorder` accepts an ordered stream of raw market/control messages,
persists small batches through `SqliteQuoteStore`, and exposes committed sequence,
pending count, oldest queued age and latched failure. Queue saturation is explicit;
the host continues protective monitoring while blocking entries and disclosing loss.
Dispose drains healthy queues; persistent storage failures bound shutdown waiting.

The agreed [live increment](../../docs/22-LIVE-QUOTE-PAPER-TRADING.md) uses
`LivePaper:RootDirectory/market-YYYY-MM-DD.sqlite`, replacing the earlier raw-event-file design.
`Storage:MarketEventRoot` remains unused by this path. Feed decoding/transport and
SQLite are in Data; quote-derived bars are in Analytics; clock-driven execution and
engine coordination are reused through Replay; the API owns hosting/subscriptions.
