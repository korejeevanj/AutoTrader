import { useState } from 'react';
import type { DashboardSnapshot, OptionRow } from './model';
import { ist } from './format';
import { QuoteBadge, QuoteDetails } from './Panels';

interface Frozen { rows: OptionRow[]; snapshotId: string; asOfUtc: string; scenarioLabel: string }
export function Comparison({ snapshot, pinned, remove, onFreeze, busy }: { snapshot: DashboardSnapshot | null; pinned: string[]; remove: (id: string) => void; onFreeze: (value: boolean) => void; busy: boolean }) {
  const [frozen, setFrozen] = useState<Frozen | null>(null);
  const rows = frozen?.rows ?? pinned.flatMap(id => snapshot?.options.find(o => o.contract.id === id) ?? []);
  if (pinned.length === 0 && !frozen) return <section className="panel comparison-empty" aria-labelledby="comparison-heading"><h2 id="comparison-heading">Option comparison</h2><p className="helper">Select a contract below, then pin up to two observations from the plan drawer.</p></section>;
  return <section className="panel comparison-panel" aria-labelledby="comparison-heading"><div className="panel-title"><div><p className="eyebrow">COMPARE / TWO OBSERVATIONS</p><h2 id="comparison-heading">Option comparison</h2></div>
    <button disabled={busy || (!frozen && (rows.length !== 2 || snapshot === null))} onClick={() => {
      if (frozen) { setFrozen(null); onFreeze(false); }
      else if (snapshot && rows.length === 2) { setFrozen({ rows: structuredClone(rows), snapshotId: snapshot.snapshotId, asOfUtc: snapshot.asOfUtc, scenarioLabel: snapshot.scenarioLabel }); onFreeze(true); }
    }}>{frozen ? 'Unfreeze comparison' : 'Freeze comparison'}</button></div>
    <p className="helper">{frozen ? 'Frozen evidence · scenario changes update the cockpit, not these captured observations.' : 'Current snapshot · pin up to two contracts from the plan drawer.'}</p>
    {frozen ? <p className="evidence-label">{frozen.scenarioLabel} · {frozen.snapshotId} · captured as-of {ist(frozen.asOfUtc)}</p> : snapshot ? <p className="evidence-label">{snapshot.scenarioLabel} · {snapshot.snapshotId} · as-of {ist(snapshot.asOfUtc)}</p> : <p role="status">Current comparison unavailable until the snapshot recovers.</p>}
    {rows.length === 0 && <p className="muted">No contracts pinned.</p>}
    <div className="compare-grid">{rows.map(o => <article key={o.contract.id}><div className="panel-title"><h3>{o.contract.symbol}</h3><QuoteBadge state={o.quote.state} /></div><QuoteDetails row={o} />{!frozen && <button disabled={busy} onClick={() => remove(o.contract.id)} aria-label={`Unpin ${o.contract.symbol}`}>Unpin</button>}</article>)}</div>
    {frozen && <p className="helper">Unfreeze to resolve pinned IDs against the current snapshot. Pin controls are locked while frozen.</p>}
  </section>;
}
