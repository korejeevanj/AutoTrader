import { useEffect, useRef } from 'react';
import { CandlestickSeries, ColorType, createChart, type IChartApi, type ISeriesApi, type Time, type UTCTimestamp } from 'lightweight-charts';
import type { ReplayBar } from './replayApi';
import { chartTime, ist, num } from './format';

export function ReplayChart({ bars, asOf, label = 'Historical futures context' }: { bars: ReplayBar[]; asOf: string | null; label?: string }) {
  const host = useRef<HTMLDivElement>(null);
  const chartRef = useRef<IChartApi | null>(null); const seriesRef = useRef<ISeriesApi<'Candlestick'> | null>(null);
  const previous = useRef({ label: '', signature: '', count: 0 }); const interacting = useRef(false);
  useEffect(() => {
    if (!host.current) return;
    const chart = createChart(host.current, { height: 300, width: host.current.clientWidth,
      layout: { background: { type: ColorType.Solid, color: '#101923' }, textColor: '#a7b9cc', attributionLogo: true },
      timeScale: { timeVisible: true, tickMarkFormatter: (t: Time) => typeof t === 'number' ? chartTime(t) : '' }, localization: { timeFormatter: (t: Time) => typeof t === 'number' ? `${chartTime(t)} IST` : '', priceFormatter: num } });
    const candles = chart.addSeries(CandlestickSeries, { upColor: '#50d8ac', downColor: '#ff8492', borderVisible: false });
    chartRef.current = chart; seriesRef.current = candles;
    previous.current = { label: '', signature: '', count: 0 };
    const resize = new ResizeObserver(entries => { const width = entries[0]?.contentRect.width; if (width) chart.applyOptions({ width }); });
    resize.observe(host.current);
    return () => { resize.disconnect(); chart.remove(); chartRef.current = null; seriesRef.current = null; };
  }, []);
  useEffect(() => {
    const chart = chartRef.current; const series = seriesRef.current; if (!chart || !series) return;
    const last = bars.at(-1); const signature = `${label}:${bars.length}:${last?.id}:${last?.close}`;
    if (previous.current.signature === signature) return;
    const reset = previous.current.label !== label || bars.length < previous.current.count;
    if (reset) interacting.current = false;
    series.setData(bars.map(bar => ({ time: (Date.parse(bar.startUtc) / 1000) as UTCTimestamp, open: bar.open, high: bar.high, low: bar.low, close: bar.close })));
    if (bars.length && (reset || previous.current.count === 0)) chart.timeScale().setVisibleLogicalRange({ from: -2, to: Math.max(60, bars.length) });
    else if (!interacting.current && bars.length > previous.current.count) chart.timeScale().scrollToRealTime();
    previous.current = { label, signature, count: bars.length };
  }, [bars, label]);
  return <section className="panel" aria-label="Historical replay chart"><h2>{label}</h2>
    <p>{bars.length} completed candles available by {ist(asOf)}. Future candles remain hidden.</p>
    <div ref={host} role="img" aria-label="Historical completed candles available at replay time" onPointerDown={() => { interacting.current = true; }} onWheel={() => { interacting.current = true; }} />
    <button onClick={() => { interacting.current = false; chartRef.current?.timeScale().fitContent(); }}>Fit replay chart</button>
    <details><summary>Available replay candles</summary><div className="table-scroll"><table><thead><tr><th>Start / IST</th><th>Open</th><th>High</th><th>Low</th><th>Close</th></tr></thead>
      <tbody>{bars.map(bar => <tr key={bar.id}><th>{ist(bar.startUtc)}</th><td>{num(bar.open)}</td><td>{num(bar.high)}</td><td>{num(bar.low)}</td><td>{num(bar.close)}</td></tr>)}</tbody></table></div></details>
  </section>;
}
