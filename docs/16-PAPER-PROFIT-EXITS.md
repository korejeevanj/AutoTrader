# Paper profit-exit policies

Approved 19 October 2026 after inspection of the 14–16 October replay logs.
The existing ₹500 milestone is informational; it is not a take-profit rule.

## Agreed behavior

- Offer independently selectable baseline, fixed-net-target and trailing-net-profit
  policies. Baseline keeps automatic profit exits disabled for comparison.
- Fixed target: request EXIT at known net liquidation ≥ ₹500 by default.
- Trailing: activate at known net liquidation ≥ ₹500; start with a ₹300 net floor;
  the threshold is max(₹300, highest known net liquidation − ₹200) and only tightens.
  Request EXIT when a later known net liquidation reaches or falls below the threshold.
- All rupee values are configurable experimental paper settings. Net liquidation
  already includes actual entry fees and projected exit costs from shared Risk.
- Keep cash stops, structural invalidation, portfolio pauses and mandatory exits
  effective and higher priority. Do not invent marks or fill missing quotes.
- Exit requests latch; the existing delayed adverse execution model supplies fills.
  A trigger is not a guaranteed fill price/profit. Candle highs cannot ratchet a
  profit stop before their information is available.
- Freeze policy with each position. Changing a selected replay policy opens a new
  paused run and preserves earlier evidence. Policy changes require no open/pending
  positions and cannot mutate active positions or already-published results.
- Record net marks, observed peaks, trail activation/threshold, trigger and fill
  evidence. Fix milestone logging parity for the candle-research lane.
- Persist selection and deterministically reconstruct the trailing state on restart.
  Compare completed variants through the existing identical-dataset comparison.

## Integration ownership and verification

This user-approved increment extends Engine configuration/position state and policy,
Replay lifecycle/reports, API run configuration, and React policy controls. It uses
existing Domain and shared Risk valuations; React performs no financial calculations.
No real-order adapter or live feed is introduced.

Approved test boundaries: public Engine position evaluation, replay lifecycle/report,
and API/browser configuration flow. Cover net/gross distinction, ratchet monotonicity,
missing/future marks, frozen settings, mandatory-stop priority, delayed fills,
restart recovery and separate same-dataset policy variants.

## Dashboard workflow

1. Prepare/recover a historical strategy dataset, then pause playback with no open
   or pending positions. Existing positions retain their frozen policy.
2. In **Profit protection**, select **Baseline — no profit exit**, **Fixed net
   take-profit**, or **Trailing net-profit lock**. Edit the net rupee parameters.
3. **Apply policy and restart** opens a new paused run from the frozen dataset.
   It changes neither the acquisition files nor earlier reports. It does not
   redownload prices or modify the global Engine profile.
4. **Run Full Day** runs the current execution preset once. **Run all presets**
   explicitly stresses the selected profit policy across configured fill models.
5. Open **Run evidence** and compare completed variants on the identical dataset.
   Run lists include the actual profit policy/settings from their frozen manifest;
   older runs without this metadata remain labelled unrecorded.

The open-position table shows peak net mark and current profit-exit threshold.
The trade ledger's **Profit protection** details distinguish trigger time/net mark,
trigger threshold and realized shortfall. A ₹500 trigger can realize less: the
delayed-fill fixture reaches ₹566, fills later at ₹466 and records ₹34 shortfall
against its ₹500 threshold. This is software-test evidence, not a live fill claim.

`trades.csv` adds `profitExitMode`, `peakNetLiquidationPnl`, `profitTriggeredAtUtc`,
`profitTriggerNetPnl`, `profitTriggerThreshold` and `profitExitShortfall`. Detailed
policy gates include `NET_LIQUIDATION`, `PEAK_NET_LIQUIDATION`, `PROFIT_EXIT_POLICY`,
`NET_PROFIT_TARGET`, `NET_PROFIT_TRAIL_ACTIVATION` and `NET_PROFIT_TRAIL_THRESHOLD`.
The informational milestone is logged in quote and candle lanes alike.

## Configuration and API

The complete editable example is `src/Trading.Engine/engine.paper.example.json`.
`Position.ProfitExit` contains:

```json
{
  "Version": "profit-exit-v1",
  "Mode": "TrailingNetProfit",
  "NetTarget": 500,
  "TrailActivation": 500,
  "TrailGiveback": 200,
  "MinimumLockedNetProfit": 300
}
```

Only the selected mode applies. Defaults preserve baseline behavior until a profit
mode is selected. Numeric/unknown modes, nonpositive targets/activation/giveback and
floors outside `[0, activation)` are rejected. The full values enter the frozen
configuration fingerprint, including changes that keep the same version label.

`POST /api/replay/jobs/{id}/commands` accepts:

```json
{
  "action": "setProfitExit",
  "profitExit": {
    "version": "profit-exit-v1",
    "mode": "FixedNetTarget",
    "netTarget": 500,
    "trailActivation": 500,
    "trailGiveback": 200,
    "minimumLockedNetProfit": 300
  }
}
```

The host derives an effective version from the acquisition's frozen baseline and
the selected policy, persists the override with the job, and opens a new run.
Restart reconstructs the peak, ratchet and latched exit by replaying the same inputs.
Selection is rejected during playback, acquisition/reporting, pending journal commit,
or while a position/entry intent is open. Storage failure blocks playback until
preparation recovers. The original acquisition configuration remains immutable.
New run numbers exceed the durable journal high-water mark, including an orphaned
variant whose job checkpoint could not publish. Changing profit controls preserves
effective non-profit settings and special-session strategy restrictions.

Shared Risk valuations and fees remain authoritative; the only added accounting
helper reports nonnegative realized shortfall against a triggered net threshold.
Market/reference-data qualification remains independent of the exit policy; an
unqualified dataset still yields diagnostic outcomes only.
