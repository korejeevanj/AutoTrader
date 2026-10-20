# Daily Kite token setup — AutoTrader

Open this guide from **Historical Replay → Daily Kite token setup**.
The browser never needs your API key, access token, API secret or browser cookies.
For decision logs, paper orders/positions and cost assumptions, see
[Replay paper trader](13-REPLAY-PAPER-TRADER.md).

## Once: fill in the local credential file

Open **`C:\Projects\AutoTrader\src\Trading.Api\kite.credentials.Local.json`**.
The blank file is already created for this checkout and explicitly gitignored.
On a fresh checkout, copy `kite.credentials.example.json` in the same directory to
`kite.credentials.Local.json`. Enter your values locally:

```json
{
  "Kite": {
    "ApiKey": "your-api-key",
    "AccessToken": "your-current-access-token"
  }
}
```

Set the API key once. Keep the file in the backend directory, not `wwwroot` or the
React project. It is not copied to build/publish output or returned by any endpoint.

## Every trading day: update the access token

Obtain today's access token through your existing Kite login flow, replace only
**`Kite.AccessToken`** in `kite.credentials.Local.json`, and save the JSON file.

Use the current login's token even when requesting an older historical date. Kite
documents expiration at 6 AM the next day, with possible earlier invalidation.
Token exchange/login UI is not part of this increment. Do not paste credentials into
chat, commit them, place them in frontend VITE settings or copy browser cookies.

The credential file reloads automatically; no rebuild or API restart is needed.
After saving, press **Retry preparation** in the Replay page. The status shows
configured or authentication-required, never the secret. This file is the **sole**
Kite credential source: environment variables, command-line settings, user-secrets
and other appsettings files do not override it or supply fallback values. Missing
or incomplete credentials appear as Missing. Keep the example file blank and share
only that example, never your local file.

## Normal use

Select one completed day on Historical Replay. Downloads start automatically and
valid cache entries are reused. Refresh from Kite creates a new revision. Preparation
is independent of browser refresh. Play/Pause/Step inspect the session; Run Full Day
finishes the current preset once at maximum throughput. **Run all presets** explicitly
starts a separate comparison from the first preset, with a visible preset counter;
its clock restarts for each preset. Future prices are hidden during playback.

## One-time historical metadata setup

The minute endpoint supplies candles, not historical index membership, weights, fees
or an expired-contract directory. Backend root defaults to `data/replay`, with:

```text
data/replay/
  catalogs/          dated full catalogue JSON documents
  calendars/2026.json
  weights/           dated HistoricalWeightSet JSON files
  masters/           automatically captured daily Kite instrument dumps
  exchange-contracts/ dated official NSE F&O masters and their parsed inventory
  candles/           immutable response revisions and checksums
  jobs/              persisted preparation state and versioned replay reports
```

`HistoricalReplay:RootDirectory` and `HistoricalReplay:ReplayConfigurationPath` may
be overridden in `src/Trading.Api/appsettings.Local.json`. Paths resolve relative to
API content root. Defaults are `../../data/replay` and
`../Trading.Replay/replay.example.json`. Strategy/research settings remain in the
existing Engine/Replay JSON profiles; they are frozen for each prepared dataset.

A dated catalogue file is:

```json
{
  "Source": "verified historical instrument/master reference",
  "IncludesAllOptions": true,
  "Manifest": { "...": "full HistoricalDatasetManifest, as documented in src/Trading.Replay" }
}
```

The ellipsis above is explanatory, not a runnable manifest. The manifest must contain
the selected date, verified cash/constituent definitions and all CE/PE strikes and
expiries valid that day (including expiry-day contracts), plus available futures
definitions. The backend selects the nearest non-expiry-day future. Normal requests
never substitute today's catalogue for a historical one. `IncludesAllOptions=false`
allows diagnostics but disables cost-qualified candle research.

Kite's current instrument CSV is captured when Replay is used, then at 08:30 IST on
following days while this API process remains running. Supply a valid daily token
before then. A snapshot captured before a day's opening may identify that day's
contracts; a later capture cannot establish an earlier catalogue. Missing historical
coverage is shown explicitly. The app now downloads the selected day's official NSE
F&O master automatically from
`https://nsearchives.nseindia.com/content/fo/NSE_FO_contract_DDMMYYYY.csv.gz`.
That day's file defines the actual expiries, strikes, exchange IDs, lot and tick sizes.
Reserved/deleted/ineligible records remain in the inventory but are not requested as
tradable candidates. A previous dated file is used conservatively to identify
pre-session-known definitions; daily files are not a complete intraday listing history.

Kite rows are only an exact contract-identity/token join (symbol, expiry, strike/type
and exchange ID). They never select a live universe for an older day, and current lot
or tick values never replace dated NSE values. Arithmetic token candidates without an
exact provider mapping are labelled unresolved and not requested. Already-expired
minute series may still be unavailable through Kite even when NSE identity is known.

Downloaded index prices are immediately replayable while this discovery/download
continues. Price-only mode has no futures VWAP inferred from the cash index, no
strategy trades and no P&L claims. Once further historical instrument candles are
ready, Restart replay to inspect them with the instrument selector. Existing replay
inputs stay frozen until that explicit restart.
Verified historical weights/membership and effective-date fees require their own
sources. Use the Replay fee profile documentation; absent fees never become zero.

Verified calendar file shape:

```json
{
  "Year": 2026,
  "Source": "official exchange calendar reference",
  "Verified": true,
  "ClosedDates": [],
  "SpecialSessions": []
}
```

Populate the actual dated holiday list before marking it verified. Special sessions
have `Date`, `Open`, `Close` fields. They override weekends/holidays. A nonstandard
session also needs an appropriate strategy profile; otherwise research is disabled.
An empty provider response is labelled NoData, not automatically a holiday. Without
a verified calendar, the UI says CalendarUnverified and checks returned session data.
Qualification additionally requires dated `PublicationEvidence` for the calendar
and bank weights. See the [readiness guide](15-HISTORICAL-DATA-READINESS.md) for the
evidence fields, 45-day weight limit and explicit diagnostic-only states.

## Troubleshooting

- **AuthenticationRequired/Missing:** edit `src/Trading.Api/kite.credentials.Local.json`, save the current token/key and retry.
- **Historical catalogue warning:** price playback is still available. Dated NSE
  discovery runs in the background; inspect its coverage and unresolved mappings.
- **NoData:** verify coverage/date/token mapping; do not assume the market was closed.
- **Failed request:** use Retry failed requests; completed files remain pinned.
- **Sparse/empty/invalid series:** use Refresh coverage gaps for an explicit new
  response revision. An empty response does not establish inactivity, and retries
  cannot guarantee the provider retains the history.
- **LOCAL_STORAGE_UNAVAILABLE:** restore write access/free storage and retry.
  Cached sources remain preserved; the UI exposes the failed checkpoint separately
  from provider/authentication failures.
- **Fees/weights unavailable:** inspect diagnostics; no cost-qualified research trades are claimed.
- **Interrupted after API restart:** Retry reuses cache and restarts replay deterministically.

Raw historical calls are centrally throttled below Kite's documented 3/second and
use bounded transient retries. Only read-only provider GET requests are implemented.
Research results are hypothetical minute-candle execution, not observed bid/ask fills.
