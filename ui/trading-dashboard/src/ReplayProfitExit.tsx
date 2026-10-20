import { useEffect, useState } from 'react';
import { ist, num } from './format';
import type { ProfitExitEvidence, ProfitExitSettings } from './replayApi';

export const profitExitLabel = (mode: ProfitExitSettings['mode']) => mode === 'FixedNetTarget' ? 'Fixed net take-profit'
  : mode === 'TrailingNetProfit' ? 'Trailing net-profit lock' : 'Baseline — no profit exit';

export function ReplayProfitExitPolicy({ configuration, disabled, blocked, apply }: {
  configuration: ProfitExitSettings; disabled: boolean; blocked: boolean; apply: (policy: ProfitExitSettings) => void;
}) {
  const [draft, setDraft] = useState(configuration);
  const signature = JSON.stringify(configuration);
  useEffect(() => setDraft(configuration), [signature]);
  const valid = [draft.netTarget, draft.trailActivation, draft.trailGiveback, draft.minimumLockedNetProfit].every(Number.isFinite)
    && draft.netTarget > 0 && draft.trailActivation > 0 && draft.trailGiveback > 0
    && draft.minimumLockedNetProfit >= 0 && draft.minimumLockedNetProfit < draft.trailActivation;
  const dirty = JSON.stringify(draft) !== signature;
  return <section className="panel replay-profit-policy" aria-label="Paper profit-exit policy">
    <div className="panel-title"><div><p className="eyebrow">VERSIONED PAPER POLICY</p><h2>Profit protection</h2></div><span className="tag">Active: {profitExitLabel(configuration.mode)}</span></div>
    <p className="helper">All thresholds use net liquidation after actual entry fees and projected exit costs. Fixed and trailing modes are independently selectable. Applying a policy opens a new paused run on the same frozen dataset.</p>
    <form onSubmit={event => { event.preventDefault(); if (valid && dirty && !disabled) apply(draft); }}>
      <fieldset disabled={disabled}><div className="replay-profit-settings">
        <label>Paper profit-exit mode <select value={draft.mode} onChange={event => { const mode = event.target.value;
          if (mode === 'Disabled' || mode === 'FixedNetTarget' || mode === 'TrailingNetProfit') setDraft({ ...draft, mode });
        }}><option value="Disabled">Baseline — no profit exit</option><option value="FixedNetTarget">Fixed net take-profit</option><option value="TrailingNetProfit">Trailing net-profit lock</option></select></label>
        <label>Net take-profit target ₹ <input type="number" min="0.01" step="0.01" value={draft.netTarget} onChange={event => setDraft({ ...draft, netTarget: Number(event.target.value) })} /></label>
        <label>Trail activation ₹ <input type="number" min="0.01" step="0.01" value={draft.trailActivation} onChange={event => setDraft({ ...draft, trailActivation: Number(event.target.value) })} /></label>
        <label>Trailing giveback ₹ <input type="number" min="0.01" step="0.01" value={draft.trailGiveback} onChange={event => setDraft({ ...draft, trailGiveback: Number(event.target.value) })} /></label>
        <label>Minimum locked-profit floor ₹ <input type="number" min="0" step="0.01" value={draft.minimumLockedNetProfit} onChange={event => setDraft({ ...draft, minimumLockedNetProfit: Number(event.target.value) })} /></label>
      </div><button type="submit" disabled={disabled || !dirty || !valid}>Apply policy and restart</button></fieldset>
    </form>
    {!valid && <p role="alert">Targets, activation and giveback must be positive. The floor must be nonnegative and below activation.</p>}
    {blocked && <p className="helper">Open or pending positions keep their frozen policy. Close them or explicitly Restart replay before selecting another run policy.</p>}
    <p className="helper">The trailing exit threshold only tightens after activation. Mandatory stops retain priority. Exit triggers use the existing delayed execution model; realized profit can be below the threshold. Policy {configuration.version}.</p>
  </section>;
}

export function ReplayProfitExitEvidence({ evidence, threshold }: { evidence?: ProfitExitEvidence | null | undefined; threshold?: number | null | undefined }) {
  if (!evidence) return <span className="helper">Profit evidence not recorded.</span>;
  return <div className="replay-profit-evidence"><strong>{profitExitLabel(evidence.policy.mode)}</strong>
    <p>Peak net mark ₹{num(evidence.peakNetLiquidationPnl)}</p>
    {threshold !== undefined && <p>{threshold === null ? evidence.policy.mode === 'TrailingNetProfit' ? 'Trail not armed' : 'No profit-exit threshold' : `Exit threshold ₹${num(threshold)}`}</p>}
    {evidence.triggeredAtUtc && <><p>Triggered {ist(evidence.triggeredAtUtc)} · net mark ₹{num(evidence.triggerNetLiquidationPnl)} · threshold ₹{num(evidence.triggerThreshold)}</p>
      <p>Realized shortfall ₹{num(evidence.realizedShortfall)}</p></>}
  </div>;
}
