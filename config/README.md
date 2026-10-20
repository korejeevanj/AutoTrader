# Configuration

Keep versioned non-secret configuration and future effective-date strategy, fee,
simulation and data-quality snapshots here. Values should identify their version
and validity period when those features are implemented.

The current synthetic index catalog and fixture seeds live in
`src/Trading.Api/appsettings.json` under `SyntheticDashboard:Indices`. They define
Bank Nifty, Nifty 50 and Sensex demonstrations and support additional configured
identities. They are separate from future per-index strategy settings and shared
portfolio cash/risk controls, and are not verified live instrument definitions.

`appsettings.Local.example.json` is an example override for the API. To use it,
copy its contents into `src/Trading.Api/appsettings.Local.json` and adjust paths.
The API does not automatically load files from this directory. The local override
is ignored by Git; its default paths are relative to the API content root.

The example defines storage locations; the API initializes the synthetic plan
journal lazily on its first read/save, independently of future recording/paper
execution. Keep credentials/tokens in backend user
secrets or environment variables. See the [backend guide](../src/Trading.Api/README.md#configuration-and-storage)
for settings precedence.
