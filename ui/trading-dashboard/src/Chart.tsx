import { useEffect, useId, useRef, useState } from 'react';
import { CandlestickSeries, ColorType, createChart, LineSeries, LineStyle, type IChartApi, type Time, type UTCTimestamp } from 'lightweight-charts';
import type { SnapshotEvidence, FixtureBar } from './model';
import { chartTime, ist, num } from './format';

const time = (utc: string) => (Date.parse(utc) / 1000) as UTCTimestamp;
export function FuturesChart({ snapshot }: { snapshot: SnapshotEvidence }) {
  const heading = useId();
  const host = useRef<HTMLDivElement>(null);
  const api = useRef<IChartApi | null>(null);
  const [hover, setHover] = useState<FixtureBar | null>(null);
  useEffect(() => {
    if (!host.current) return;
    setHover(null);
    const chart = createChart(host.current, {
      height: 300, width: host.current.clientWidth,
      layout: { background: { type: ColorType.Solid, color: '#101923' }, textColor: '#a7b9cc', fontFamily: 'Consolas, monospace', attributionLogo: true },
      grid: { vertLines: { color: '#1c2a38' }, horzLines: { color: '#1c2a38' } },
      rightPriceScale: { borderColor: '#304254' }, timeScale: { borderColor: '#304254', timeVisible: true, secondsVisible: false, tickMarkFormatter: (t: Time) => typeof t === 'number' ? chartTime(t) : '' },
      localization: { timeFormatter: (t: Time) => typeof t === 'number' ? `${chartTime(t)} IST` : '', priceFormatter: num },
      handleScroll: { mouseWheel: true, pressedMouseMove: true, horzTouchDrag: true, vertTouchDrag: false },
    });
    api.current = chart;
    const candles = chart.addSeries(CandlestickSeries, { upColor: '#50d8ac', downColor: '#ff8492', borderVisible: false, wickUpColor: '#50d8ac', wickDownColor: '#ff8492', priceLineVisible: false });
    candles.setData(snapshot.chart.bars.map(b => ({ time: time(b.startUtc), open: b.open, high: b.high, low: b.low, close: b.close })));
    const vwap = chart.addSeries(LineSeries, { color: '#edc678', lineWidth: 2, priceLineVisible: false, lastValueVisible: false });
    vwap.setData(snapshot.chart.vwap.map(p => ({ time: time(p.timeUtc), value: p.value })));
    for (const [price, title] of [[snapshot.chart.openingRangeHigh, 'OR high'], [snapshot.chart.openingRangeLow, 'OR low']] as const) {
      if (price !== null) candles.createPriceLine({ price, color: '#91a9d7', lineWidth: 1, lineStyle: LineStyle.Dashed, axisLabelVisible: true, title });
    }
    const onHover: Parameters<typeof chart.subscribeCrosshairMove>[0] = param => {
      const b = typeof param.time === 'number' ? snapshot.chart.bars.find(b => time(b.startUtc) === param.time) : null;
      setHover(b ?? null);
    };
    chart.subscribeCrosshairMove(onHover);
    chart.timeScale().fitContent();
    const observer = new ResizeObserver(entries => {
      const width = entries[0]?.contentRect.width;
      if (width && width > 0) chart.applyOptions({ width });
    });
    observer.observe(host.current);
    return () => { observer.disconnect(); chart.unsubscribeCrosshairMove(onHover); chart.remove(); api.current = null; };
  }, [snapshot]);
  const latest = hover ?? snapshot.chart.bars.at(-1);
  return <section className="panel chart-panel" aria-labelledby={heading}>
    <div className="panel-title"><div><p className="eyebrow">01 / MARKET STRUCTURE</p><h2 id={heading}>Futures context</h2></div><span className="tag">1m · completed</span></div>
    <div className="chart-tools"><span>{snapshot.chart.symbol} · fixture history</span><div className="button-group">
      <button aria-label="Zoom in chart" onClick={() => { const scale = api.current?.timeScale(); if (scale) scale.applyOptions({ barSpacing: scale.options().barSpacing * 1.3 }); }}>+</button>
      <button aria-label="Zoom out chart" onClick={() => { const scale = api.current?.timeScale(); if (scale) scale.applyOptions({ barSpacing: scale.options().barSpacing / 1.3 }); }}>−</button>
      <button onClick={() => { const scale = api.current?.timeScale(); if (scale) scale.scrollToPosition(scale.scrollPosition() - 5, false); }} aria-label="Pan chart earlier">←</button>
      <button onClick={() => { const scale = api.current?.timeScale(); if (scale) scale.scrollToPosition(scale.scrollPosition() + 5, false); }} aria-label="Pan chart later">→</button>
      <button onClick={() => api.current?.timeScale().fitContent()}>Reset chart</button>
    </div></div>
    <div className="ohlc" aria-live="off">{latest ? <><span>{hover ? 'Hover' : 'Latest completed'} · {ist(latest.startUtc)}</span><span>O {num(latest.open)} · H {num(latest.high)} · L {num(latest.low)} · C {num(latest.close)}</span></> : 'Completed bars unavailable'}</div>
    <div ref={host} className="chart-host" aria-label="Interactive completed one-minute futures candlestick chart" role="img" />
    <p className="chart-caption"><span className="vwap-key">{snapshot.chart.vwapLabel} · approximate, .NET-derived</span> · OR high {num(snapshot.chart.openingRangeHigh)} / low {num(snapshot.chart.openingRangeLow)}</p>
    <details><summary>Accessible chart summary</summary><p>{snapshot.chart.bars.length} completed one-minute bars, through {ist(snapshot.chart.bars.at(-1)?.endUtc ?? null)}. Available by {ist(snapshot.asOfUtc)}. No unfinished candles.</p>
      <div className="table-scroll" tabIndex={0} role="region" aria-label="Completed candle values"><table><thead><tr><th>Start / IST</th><th>Open</th><th>High</th><th>Low</th><th>Close</th><th>Available / IST</th></tr></thead><tbody>{snapshot.chart.bars.map(b => <tr key={b.startUtc}><th>{ist(b.startUtc)}</th><td>{num(b.open)}</td><td>{num(b.high)}</td><td>{num(b.low)}</td><td>{num(b.close)}</td><td>{ist(b.availableAtUtc)}</td></tr>)}</tbody></table></div>
    </details>
  </section>;
}
