import { lazy, Suspense, useCallback, useEffect, useState, type MouseEvent } from 'react';
import { readIndices, readScenarios, readSnapshot } from './api';
import { FuturesChart } from './Chart';
import { Health } from './Health';
import { Comparison } from './Comparison';
import { Plan } from './Plan';
import { Journal } from './Journal';
import { ConstituentPanel, OptionsPanel, PositionPanel, QuoteBadge, UnavailablePanel } from './Panels';
import { age, ist, num, percent } from './format';
import type { ScenarioId } from './model';
import { useRemote } from './useRemote';
const HistoricalReplay = lazy(() => import('./HistoricalReplay').then(module => ({ default: module.HistoricalReplay })));
const LivePaper = lazy(() => import('./LivePaper').then(module => ({ default: module.LivePaper })));

export function App() {
  const [indexId, setIndexId] = useState('BANKNIFTY');
  const [comparisonMessage, setComparisonMessage] = useState('');
  const [scenario, setScenario] = useState<ScenarioId>('normal');
  const [refresh, setRefresh] = useState(0);
  const [selected, setSelected] = useState<string | null>(null);
  const [pinned, setPinned] = useState<string[]>([]);
  const [frozen, setFrozen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [journalRevision, setJournalRevision] = useState(0);
  const [route, setRoute] = useState(window.location.hash === '#live' ? 'live' : window.location.hash === '#replay' ? 'replay' : window.location.hash === '#journal' ? 'journal' : 'cockpit');
  useEffect(() => { document.title = route === 'live' ? 'AutoTrader · Live paper trading' : route === 'replay' ? 'AutoTrader · Historical Replay' : route === 'journal' ? 'AutoTrader · Decision journal' : 'AutoTrader · Synthetic cockpit'; }, [route]);
  useEffect(() => {
    const change = () => setRoute(window.location.hash === '#live' ? 'live' : window.location.hash === '#replay' ? 'replay' : window.location.hash === '#journal' ? 'journal' : 'cockpit');
    window.addEventListener('hashchange', change);
    return () => window.removeEventListener('hashchange', change);
  }, []);
  const scenarios = useRemote(readScenarios, refresh);
  const indices = useRemote(readIndices, refresh);
  const definition = indices.status === 'ready' ? indices.data.find(i => i.id === indexId) : undefined;
  const load = useCallback((signal: AbortSignal) => definition ? readSnapshot(definition.id, scenario, signal, definition) : Promise.resolve(null), [definition, scenario]);
  const snapshot = useRemote(load, refresh);
  const current = snapshot.status === 'ready' ? snapshot.data : null;
  const metadataError = indices.status === 'error' ? `Index metadata unavailable. ${indices.message}`
    : indices.status === 'ready' && !definition ? 'Index metadata unavailable: selected identity is absent from the catalog.' : null;
  const snapshotError = metadataError ?? (snapshot.status === 'error' ? snapshot.message : null);
  const optionComparison = definition?.capabilities.optionComparison;
  useEffect(() => {
    if (optionComparison !== false) return;
    setSelected(null);
    setPinned([]);
    setFrozen(false);
    setComparisonMessage('Comparison cleared: option comparison is unavailable for this catalog index. Drafts retained by index + contract.');
  }, [optionComparison, indexId]);
  useEffect(() => {
    if (!comparisonMessage) return;
    const timer = window.setTimeout(() => setComparisonMessage(''), 6_000);
    return () => window.clearTimeout(timer);
  }, [comparisonMessage, indexId]);
  function switchIndex(id: string) {
    if (saving || id === indexId) return;
    setIndexId(id);
    setSelected(null);
    setPinned([]);
    setFrozen(false);
    setComparisonMessage('Comparison cleared for the new index. Drafts retained by index + contract.');
  }
  function togglePin(id: string) {
    if (frozen || saving || optionComparison !== true) return;
    setPinned(ids => ids.includes(id) ? ids.filter(i => i !== id) : ids.length < 2 ? [...ids, id] : ids);
  }
  function closePlan() {
    const trigger = document.getElementById(`option-${selected}`);
    setSelected(null);
    (trigger ?? document.getElementById('workspace'))?.focus();
  }
  function skipToWorkspace(event: MouseEvent<HTMLAnchorElement>) {
    event.preventDefault();
    const workspace = document.getElementById('workspace');
    workspace?.focus();
    workspace?.scrollIntoView();
  }
  return <>
    <a className="skip-link" href="#workspace" onClick={skipToWorkspace}>Skip to workspace</a>
    <header className="app-header"><a className="brand" href="#workspace"><span className="brand-mark" aria-hidden="true">A↗</span><span>AutoTrader<small>LOCAL / PAPER WORKSPACE</small></span></a><span className="mode-badge">{route === 'live' ? 'PAPER + LIVE DATA' : route === 'replay' ? 'HISTORICAL REPLAY' : 'SYNTHETIC TEST'}</span></header>
    <main id="workspace" tabIndex={-1}>
      <div className="workspace-heading"><div><p className="eyebrow">{route === 'live' ? 'BANKNIFTY / LIVE QUOTE SESSION' : route === 'replay' ? 'BANKNIFTY / HISTORICAL SESSION' : route === 'journal' ? 'ALL INDICES / PLAN REVIEW' : `${indexId} / DECISION TERMINAL`}</p><h1>{route === 'live' ? 'Live paper trading' : route === 'replay' ? 'Historical Replay' : route === 'journal' ? 'Shared decision journal' : definition ? `${definition.displayName} cockpit` : 'Index cockpit'}</h1><p className="muted">{route === 'live' ? 'Record actual quotes · simulate delayed fills · recover and replay.' : route === 'replay' ? 'Select a day · prepare historical data · inspect causal decisions and candle research.' : route === 'journal' ? 'Review preserved plans across indices. Synthetic evidence, no intents or fills.' : 'Observe · compare · document. Synthetic fixtures, no live market feed.'}</p></div><span className="tag">{route === 'live' ? 'Local quote-qualified paper simulation' : route === 'replay' ? 'Candle research / no real orders' : 'Ⅱ Paper-auto paused'}</span></div>
      {route !== 'replay' && route !== 'live' && <Health />}
      <div className="workspace-controls"><nav aria-label="Workspace navigation"><a href="#cockpit" aria-current={route === 'cockpit' ? 'page' : undefined}>Cockpit</a><a href="#journal" aria-current={route === 'journal' ? 'page' : undefined}>Journal</a><a href="#replay" aria-current={route === 'replay' ? 'page' : undefined}>Replay</a><a href="#live" aria-current={route === 'live' ? 'page' : undefined}>Live paper</a></nav>
      {route === 'cockpit' && <div className="toolbar"><label>Cockpit index <select aria-label="Cockpit index" value={indexId} disabled={saving || indices.status !== 'ready'} onChange={e => switchIndex(e.target.value)}>{indices.status === 'ready' ? indices.data.map(i => <option key={i.id} value={i.id}>{i.displayName}</option>) : <option value={indexId}>Indices {indices.status === 'error' ? 'unavailable' : 'loading…'}</option>}</select></label><label>Fixture scenario <select value={scenario} disabled={saving || scenarios.status !== 'ready'} onChange={e => setScenario(e.target.value as ScenarioId)}>{scenarios.status === 'ready' ? scenarios.data.map(s => <option key={s.id} value={s.id}>{s.label}</option>) : <option value={scenario}>Scenarios {scenarios.status === 'error' ? 'unavailable' : 'loading…'}</option>}</select></label><button disabled={saving} onClick={() => setRefresh(v => v + 1)}>{snapshotError || scenarios.status === 'error' ? 'Retry snapshot' : 'Refresh snapshot'}</button><p className="helper">REST snapshots on selection / refresh. No synthetic feed timer.</p></div>}</div>
      {route === 'journal' && <Journal revision={journalRevision} indices={indices} retryIndices={() => setRefresh(v => v + 1)} />}
      {route === 'replay' && <Suspense fallback={<section className="panel" role="status">Loading historical replay workspace…</section>}><HistoricalReplay /></Suspense>}
      {route === 'live' && <Suspense fallback={<section className="panel" role="status">Loading live paper workspace…</section>}><LivePaper /></Suspense>}
      <div hidden={route !== 'cockpit'}>
      {comparisonMessage && <p role="status" className="helper">{comparisonMessage}</p>}
      {scenarios.status === 'error' && <p role="alert" className="error">Scenarios unavailable. {scenarios.message}</p>}
      {snapshotError ? <section className="panel empty" role="alert"><h2>Snapshot unavailable</h2><p>{snapshotError}</p><p>Market observations and planning are unavailable until a successful retry.</p></section> : !current ? <section className="panel empty" role="status">Loading snapshot… Previous observations are not shown as current.</section> : <>
        <section className="fixture-status" aria-labelledby="freshness-heading"><div><h2 id="freshness-heading">Fixture freshness</h2><QuoteBadge state={current.freshness.state} /></div><div><strong>Fixed fixture clock</strong><p>Snapshot as-of {ist(current.asOfUtc)} · not live freshness</p></div><p>{current.freshness.message}<small>Quote age: {age(current.freshness.quoteAgeSeconds)} · receipt age: {age(current.freshness.receiptAgeSeconds)}, evaluated by .NET at as-of.</small></p></section>
        <section className="market-strip" aria-label="Market summary"><div><p className="eyebrow">CASH INDEX / CONTEXT ONLY</p><strong>{definition?.displayName}</strong><b>{num(current.market.indexValue)}</b><small>No traded volume · no cash-index VWAP</small></div><div><p className="eyebrow">FUTURES / SYNTHETIC</p><strong>{current.market.futuresSymbol}</strong><b>{num(current.market.futuresValue)}</b><small>Change {percent(current.market.changePercent)}</small></div><div><p className="eyebrow">EVIDENCE / PROVENANCE</p><strong>{current.snapshotId}</strong><small>Schema {current.schemaVersion} · fixture {current.fixtureVersion} · sequence {current.sequence}</small><small>{current.source} · {current.indexId}</small></div></section>
        <div className="cockpit-grid">{definition!.capabilities.futuresChart ? <FuturesChart snapshot={current} /> : <UnavailablePanel title="Futures context" className="chart-panel" message="Futures chart unavailable: this catalog index has futuresChart disabled. Cash-index context remains available." />}<div className="side-stack"><PositionPanel snapshot={current} /><ConstituentPanel snapshot={current} label={definition!.constituentLabel} weighted={definition!.capabilities.weightedConstituents} /></div></div>
      </>}
      {optionComparison === false ? <UnavailablePanel title="Option comparison" message="Option comparison and planning unavailable: this catalog index has optionComparison disabled. No candidate selection, pins, freeze or plan actions are available." /> : <Comparison key={indexId} snapshot={current} pinned={pinned} remove={togglePin} onFreeze={setFrozen} busy={saving} />}
      <div className={selected && optionComparison !== false ? 'planning-grid' : ''}><div>{current && optionComparison === true && <OptionsPanel options={current.options} selected={selected} busy={saving} onSelect={id => { if (!saving) setSelected(id); }} />}</div><Plan enabled={optionComparison !== false} indexId={indexId} snapshot={current} selected={selected} pinned={pinned} frozen={frozen} togglePin={togglePin} close={closePlan} onSaving={setSaving} onSaved={() => setJournalRevision(v => v + 1)} /></div>
      </div>
      <footer className="page-footer"><span>AutoTrader · {route === 'live' ? 'Live-data local paper simulation' : route === 'replay' ? 'Historical candle research' : 'Synthetic software demonstration'}</span><span>Charts by <a href="https://www.tradingview.com/" target="_blank" rel="noreferrer">TradingView Lightweight Charts™</a> · <a href="/NOTICE.txt">NOTICE</a> · <a href="/LICENSE-lightweight-charts.txt">Apache 2.0</a></span></footer>
    </main>
  </>;
}
