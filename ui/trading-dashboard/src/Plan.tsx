import { useCallback, useEffect, useRef, useState } from 'react';
import { errorMessage, previewPlan, savePlan } from './api';
import type { DashboardSnapshot, PlanStatus, SavePlanRequest } from './model';
import { QuoteBadge, QuoteDetails } from './Panels';
import { ist, money } from './format';
import { useRemote } from './useRemote';

interface Draft { lots: string; status: PlanStatus; thesis: string; stopPlan: string; invalidation: string; note: string }
const blank: Draft = { lots: '1', status: 'considered', thesis: '', stopPlan: '', invalidation: '', note: '' };
type SaveState = { key: string; status: 'saved' | 'error'; message: string } | null;
interface Props { enabled: boolean; indexId: string; snapshot: DashboardSnapshot | null; selected: string | null; pinned: string[]; frozen: boolean; togglePin: (id: string) => void; close: () => void; onSaving: (value: boolean) => void; onSaved: () => void }
export function Plan({ enabled, indexId, snapshot, selected, pinned, frozen, togglePin, close, onSaving, onSaved }: Props) {
  const [drafts, setDrafts] = useState<Record<string, Draft>>({});
  const [saveState, setSaveState] = useState<SaveState>(null);
  const [saving, setSaving] = useState(false);
  const [resetFor, setResetFor] = useState<string | null>(null);
  const saveController = useRef<AbortController | null>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const row = snapshot?.options.find(o => o.contract.id === selected);
  const draftKey = JSON.stringify([indexId, selected]);
  const draft = selected ? drafts[draftKey] ?? blank : blank;
  const lots = Number(draft.lots);
  const validLots = draft.lots.trim() !== '' && Number.isSafeInteger(lots) && lots > 0;
  const key = `${draftKey}/${snapshot?.snapshotId ?? ''}/${JSON.stringify(draft)}`;
  const loadPreview = useCallback(async (signal: AbortSignal) => {
    if (!enabled || !snapshot || !selected || !validLots || !snapshot.options.some(o => o.contract.id === selected)) return null;
    const preview = await previewPlan({ indexId, scenarioId: snapshot.scenarioId, snapshotId: snapshot.snapshotId, contractId: selected, lots }, signal);
    if (preview.lots !== lots) throw new Error('Preview returned mismatched whole lots. Retry preview.');
    return preview;
  }, [enabled, indexId, snapshot, selected, lots, validLots]);
  const [previewRevision, setPreviewRevision] = useState(0);
  const preview = useRemote(loadPreview, previewRevision);
  useEffect(() => { heading.current?.focus(); }, [selected]);
  useEffect(() => () => saveController.current?.abort(), []);
  if (!selected || !enabled) return null;
  const isPinned = pinned.includes(selected);
  const feedback = saveState?.key === key ? saveState : null;
  const readyPreview = preview.status === 'ready' ? preview.data : null;
  function edit(update: Partial<Draft>) {
    if (!selected || saving) return;
    setDrafts(all => ({ ...all, [draftKey]: { ...(all[draftKey] ?? blank), ...update } }));
    setSaveState(null);
    setResetFor(null);
  }
  async function submit() {
    if (!enabled || saveController.current || !snapshot || !selected || !validLots || !readyPreview || !row) return;
    const controller = new AbortController();
    saveController.current = controller;
    const request: SavePlanRequest = { indexId, scenarioId: snapshot.scenarioId, snapshotId: snapshot.snapshotId, contractId: selected, lots, status: draft.status, thesis: draft.thesis, stopPlan: draft.stopPlan, invalidation: draft.invalidation, note: draft.note };
    setSaving(true); onSaving(true); setSaveState(null);
    try {
      const entry = await savePlan(request, controller.signal);
      if (controller.signal.aborted) return;
      if (entry.indexId !== request.indexId || entry.scenarioId !== request.scenarioId || entry.snapshotId !== request.snapshotId || entry.contractId !== request.contractId || entry.lots !== request.lots || entry.status !== request.status || entry.note !== request.note || entry.thesis !== request.thesis || entry.stopPlan !== request.stopPlan || entry.invalidation !== request.invalidation) throw new Error('Saved response does not match this plan. Inspect the journal before retrying.');
      setSaveState({ key, status: 'saved', message: `Saved to journal · ${entry.id} · ${ist(entry.savedAtUtc)}. Draft retained; this is a plan, not an order or fill.` });
      onSaved();
    } catch (error) {
      if (!controller.signal.aborted) setSaveState({ key, status: 'error', message: `${errorMessage(error)} Draft retained. If the response was lost, inspect the journal before retrying.` });
    } finally {
      if (!controller.signal.aborted) { setSaving(false); onSaving(false); }
      if (saveController.current === controller) saveController.current = null;
    }
  }
  return <section className="panel plan-panel" aria-labelledby="plan-heading">
    <div className="panel-title"><div><p className="eyebrow">SELECTED CONTRACT / DRAFT</p><h2 id="plan-heading" tabIndex={-1} ref={heading}>Option plan</h2></div><button onClick={close} disabled={saving} aria-label="Close option plan">Close</button></div>
    {row && snapshot ? <><h3>{row.contract.symbol}</h3><p><QuoteBadge state={row.quote.state} /></p><p className="evidence-label">Current evidence · {indexId} · {snapshot.snapshotId} · {ist(snapshot.asOfUtc)}</p><QuoteDetails row={row} /><p className="helper">{row.decision.action} · {row.decision.reasonCodes.join(' · ')}</p><button onClick={() => togglePin(selected)} disabled={saving || frozen || (!isPinned && pinned.length >= 2)}>{isPinned ? 'Unpin from comparison' : 'Pin for comparison'}</button>{frozen ? <p className="helper">Unfreeze comparison to change pins.</p> : !isPinned && pinned.length >= 2 && <p className="helper">Two contracts pinned. Unpin one to replace it.</p>}</> : <p>Selected contract preserved. Current observations unavailable until the snapshot recovers.</p>}
    <p className="draft-policy">Edits stay in this tab by index + contract, including when changing index, scenario or closing the drawer. Save captures the <strong>current scenario</strong> above. Unsaved edits are lost on page reload.</p>
    <form onSubmit={e => { e.preventDefault(); void submit(); }}>
      <fieldset disabled={saving}><legend className="sr-only">Plan draft</legend>
        <div className="form-row"><label>Whole lots<input type="number" min="1" step="1" required value={draft.lots} onChange={e => edit({ lots: e.target.value })} aria-describedby="lots-help" /></label><label>Plan status<select value={draft.status} onChange={e => edit({ status: e.target.value as PlanStatus })}><option value="considered">Considered</option><option value="planned">Planned</option><option value="skipped">Skipped</option></select></label></div>
        <p id="lots-help" className={validLots ? 'helper' : 'negative'}>{validLots ? 'Positive integer whole lots. Units are derived by .NET.' : 'Enter positive integer whole lots.'}</p>
        <div className="preview-box" aria-label="Server plan preview">
          {preview.status === 'loading' ? <p>Loading server preview…</p> : preview.status === 'error' ? <><p role="alert" className="negative">Preview unavailable. {preview.message}</p><button type="button" onClick={() => setPreviewRevision(v => v + 1)}>Retry preview</button></> : readyPreview ? <><strong>{readyPreview.units} units</strong><dl className="metric-grid"><div><dt>Indicative reference ask</dt><dd>{money(readyPreview.referenceAsk)}</dd></div><div><dt>Round-trip fees</dt><dd>{money(readyPreview.planning.roundTripFees)}</dd></div><div><dt>Required cash</dt><dd>{money(readyPreview.planning.requiredCash)}</dd></div><div><dt>Net risk</dt><dd>{money(readyPreview.planning.netRisk)}</dd></div></dl><p className="helper">{readyPreview.planning.availabilityReason}</p></> : <p className="helper">Preview unavailable until a valid whole-lot selection and current snapshot are available.</p>}
        </div>
        <div className="form-field"><label htmlFor="plan-thesis">Thesis</label><textarea id="plan-thesis" value={draft.thesis} onChange={e => edit({ thesis: e.target.value })} /></div>
        <div className="form-field"><label htmlFor="plan-stop">Stop plan</label><textarea id="plan-stop" value={draft.stopPlan} onChange={e => edit({ stopPlan: e.target.value })} /></div>
        <div className="form-field"><label htmlFor="plan-invalidation">Invalidation</label><textarea id="plan-invalidation" value={draft.invalidation} onChange={e => edit({ invalidation: e.target.value })} /></div>
        <div className="form-field"><label htmlFor="plan-note">Note</label><textarea id="plan-note" value={draft.note} onChange={e => edit({ note: e.target.value })} /></div>
        <div className="button-group"><button className="primary" type="submit" disabled={!snapshot || !row || !validLots || !readyPreview || feedback?.status === 'saved'}>{feedback?.status === 'error' ? 'Retry save plan' : 'Save plan'}</button><button type="button" onClick={() => setResetFor(draftKey)}>Reset draft</button></div>
        {resetFor === draftKey && <div className="reset-confirm" role="group" aria-label="Confirm draft reset"><p>Discard unsaved edits for this contract? Saved journal evidence stays preserved.</p><div className="button-group"><button type="button" onClick={() => { edit(blank); setResetFor(null); }}>Discard draft edits</button><button type="button" onClick={() => setResetFor(null)}>Keep editing</button></div></div>}
      </fieldset>
    </form>
    {saving && <p role="status">Saving plan… Wait for the server confirmation.</p>}
    {feedback && <p role={feedback.status === 'error' ? 'alert' : 'status'} className={feedback.status === 'error' ? 'negative save-feedback' : 'positive save-feedback'}>{feedback.message}</p>}
  </section>;
}
