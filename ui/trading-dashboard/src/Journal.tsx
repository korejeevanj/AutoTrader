import { useCallback, useEffect, useRef, useState } from 'react';
import { readEntry, readJournal } from './api';
import { useRemote } from './useRemote';
import { count, ist } from './format';
import { ConstituentPanel, PositionPanel, QuoteDetails } from './Panels';
import { FuturesChart } from './Chart';
import type { IndexDefinition, Remote } from './model';

export function Journal({ revision, indices, retryIndices }: { revision: number; indices: Remote<IndexDefinition[]>; retryIndices: () => void }) {
  const [refresh, setRefresh] = useState(0);
  const [filter, setFilter] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const loadList = useCallback((signal: AbortSignal) => readJournal(signal, filter || undefined), [filter]);
  const list = useRemote(loadList, revision + refresh);
  const definition = (id: string) => indices.status === 'ready' ? indices.data.find(i => i.id === id) : undefined;
  const indexLabel = (id: string) => `${definition(id)?.displayName ?? 'Index'} · ${id}`;
  const load = useCallback(async (signal: AbortSignal) => {
    if (!selected) return null;
    const entry = await readEntry(selected, signal);
    if (entry.id !== selected) throw new Error('Journal returned a mismatched entry. Please retry.');
    if (filter && entry.indexId !== filter) throw new Error('Saved evidence returned the wrong index. Please retry.');
    return entry;
  }, [selected, filter]);
  const entry = useRemote(load, refresh);
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { if (entry.status === 'ready' && entry.data) heading.current?.focus(); }, [entry]);
  return <div className="journal-view"><div className="panel-title"><div><p className="eyebrow">DURABLE / SQLITE PLANS</p><h2>Decision journal</h2></div><button onClick={() => setRefresh(v => v + 1)}>Refresh journal</button></div>
    <p className="helper">Plans, not intents or fills. Immutable synthetic evidence is resolved and stored by .NET; notes are plain text.</p>
    <div className="toolbar"><label>Journal index<select aria-label="Journal index" value={filter} disabled={indices.status !== 'ready'} onChange={e => { setFilter(e.target.value); setSelected(null); }}><option value="">All indices</option>{indices.status === 'ready' && indices.data.map(i => <option key={i.id} value={i.id}>{i.displayName}</option>)}</select></label><p className="helper">Shared journal · filtering retrieves preserved entries without removing them.</p></div>
    <p className="evidence-label" role="status" aria-label="Journal filter scope">Journal scope: <strong>{filter ? indexLabel(filter) : 'All indices'}</strong></p>
    {indices.status === 'error' && <p role="alert" className="error">Index catalog unavailable. {indices.message} <button onClick={retryIndices}>Retry index catalog</button></p>}
    {list.status === 'loading' ? <p role="status">Loading journal…</p> : list.status === 'error' ? <section className="panel" role="alert"><h3>Journal unavailable</h3><p>{list.message}</p><button onClick={() => setRefresh(v => v + 1)}>Retry journal</button></section> : list.data.length === 0 ? <section className="panel empty">No saved plans yet{filter ? ` for ${indexLabel(filter)}` : ''}. Select a contract in the cockpit to document your view.</section> : <div className="journal-list">{list.data.map(e => <article key={e.id} className="panel journal-card"><div className="panel-title"><h3>{e.snapshot.options.find(o => o.contract.id === e.contractId)?.contract.symbol ?? e.contractId}</h3><span className="tag">{e.status}</span></div><p className="evidence-label">{indexLabel(e.indexId)} · {ist(e.savedAtUtc)} · {e.id}</p><p>{count(e.lots)} whole lots · {count(e.units)} units · SYNTHETIC TEST</p><dl className="note-list"><dt>Thesis</dt><dd>{e.thesis || 'Not recorded'}</dd><dt>Stop plan</dt><dd>{e.stopPlan || 'Not recorded'}</dd><dt>Invalidation</dt><dd>{e.invalidation || 'Not recorded'}</dd><dt>Note</dt><dd>{e.note || 'Not recorded'}</dd></dl><p className="helper">{e.snapshot.scenarioLabel} · {e.snapshotId}</p><button onClick={() => setSelected(e.id)}>Inspect saved evidence</button></article>)}</div>}
    {selected && <section className="panel saved-evidence" aria-labelledby="evidence-heading"><div className="panel-title"><h2 id="evidence-heading" tabIndex={-1} ref={heading}>Original saved evidence</h2><button onClick={() => setSelected(null)}>Close evidence</button></div>
      {entry.status === 'loading' ? <p role="status">Loading preserved evidence…</p> : entry.status === 'error' ? <><p role="alert" className="negative">{entry.message}</p><button onClick={() => setRefresh(v => v + 1)}>Retry evidence</button></> : entry.data && <>
        <p className="evidence-label">{indexLabel(entry.data.indexId)} · {entry.data.mode} · {entry.data.snapshot.source} · {entry.data.snapshot.snapshotId}</p><p className="helper">Original as-of {ist(entry.data.snapshot.asOfUtc)} · schema {entry.data.snapshot.schemaVersion} · fixture {entry.data.snapshot.fixtureVersion} · sequence {entry.data.snapshot.sequence}</p><p>Saved scenario: {entry.data.snapshot.scenarioLabel}. This evidence is preserved independently of the current cockpit scenario.</p>
        {entry.data.snapshot.schemaVersion === '1' && <p className="helper">Legacy Bank Nifty schema-1 fixture. Index identity is supplied by the journal; the original snapshot JSON is preserved.</p>}
        {entry.data.snapshot.options.filter(o => o.contract.id === entry.data?.contractId).map(o => <div key={o.contract.id}><h3>{o.contract.symbol}</h3><QuoteDetails row={o} /></div>)}
        <ConstituentPanel snapshot={entry.data.snapshot} label={definition(entry.data.indexId)?.constituentLabel ?? (entry.data.snapshot.schemaVersion === '1' ? 'Banks' : 'Constituents')} /><PositionPanel snapshot={entry.data.snapshot} /><FuturesChart snapshot={entry.data.snapshot} />
        <details><summary>Full preserved snapshot / provenance (JSON)</summary><pre>{JSON.stringify(entry.data.snapshot, null, 2)}</pre></details>
      </>}
    </section>}
  </div>;
}
