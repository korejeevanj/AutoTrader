"""Independent, mechanical scoring of the owner's Indian Market Forecast Ledger against recorded index data.

Usage: python -I scripts/research/ledger_score.py <ledger.xlsx> <index-day root, e.g. data/research/index-day>

Reads the 'Daily Forecasts' sheet (stdlib zip/xml only, nothing in the workbook is executed). Bias text is mapped to a direction:
bearish/negative/weak -> -1, bullish/positive -> +1, neutral/flat/mixed -> 0; for "A -> B" the part after the arrow is used.
Scored against Kite daily candles: opening bias vs the gap (open vs previous close), session bias vs open-to-close.
"""
import os, re, statistics, sys, zipfile, math, xml.etree.ElementTree as ET
sys.path.insert(0, os.path.dirname(__file__))
import trend as t

NS = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
INDICES = {"NIFTY 50": ("NIFTY_50", "C", "D", "E"), "BANK NIFTY": ("NIFTY_BANK", "J", "K", "L"), "SENSEX": ("SENSEX", "P", "Q", "R")}


def sheet_rows(path, name):
    z = zipfile.ZipFile(path)
    shared = [''.join(x.text or '' for x in si.iter('{%s}t' % NS['m'])) for si in ET.fromstring(z.read('xl/sharedStrings.xml')).findall('m:si', NS)]
    wb = ET.fromstring(z.read('xl/workbook.xml')); rels = ET.fromstring(z.read('xl/_rels/workbook.xml.rels'))
    sheet = next(s for s in wb.find('m:sheets', NS) if s.get('name') == name)
    rid = sheet.get('{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id')
    target = next(r.get('Target') for r in rels if r.get('Id') == rid)
    root = ET.fromstring(z.read('xl/' + target.lstrip('/').replace('xl/', '')))
    out = []
    for r in root.find('m:sheetData', NS):
        row = {}
        for c in r.findall('m:c', NS):
            v = c.find('m:v', NS); kind = c.get('t'); text = None
            if kind == 's' and v is not None: text = shared[int(v.text)]
            elif kind == 'inlineStr': text = ''.join(x.text or '' for x in c.iter('{%s}t' % NS['m']))
            elif v is not None: text = v.text
            if text not in (None, ''): row[re.sub(r'\d', '', c.get('r'))] = text
        out.append(row)
    return out


def direction(text):
    if not text: return None
    part = re.split(r"→|->", text)[-1].lower()
    # colour markers used in later rows: red = bearish, green = bullish, yellow = neutral; a red/yellow mix is read as a bearish lean
    if "🔴" in part and "🟢" not in part: return -1
    if "🟢" in part and "🔴" not in part: return 1
    if any(w in part for w in ("bear", "negative", "weak", "down", "gap-down")): return -1
    if any(w in part for w in ("bull", "positive", "gap-up", "up")): return 1
    if any(w in part for w in ("neutral", "flat", "mixed", "range", "cautious", "🟡")): return 0
    return None


def score(calls):
    """calls: list of (direction, actual points). Directional calls only."""
    taken = [(d, a) for d, a in calls if d]
    if not taken: return {"calls": 0}
    signed = [d * a for d, a in taken]; n = len(signed); k = sum(1 for s in signed if s > 0)
    p = sum(math.comb(n, j) for j in range(k, n + 1)) / 2 ** n
    return {"calls": n, "right": k, "hit_rate": round(k / n, 2), "mean_points_in_call_direction": round(statistics.mean(signed), 1),
            "chance_of_this_many_right_or_more": round(p, 3), "neutral_days": len(calls) - n}


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8")
    if len(argv) < 3: sys.exit(__doc__)
    rows = [r for r in sheet_rows(argv[1], "Daily Forecasts") if re.match(r"\d{4}-\d{2}-\d{2}$", r.get("A", ""))]
    print(f"Forecast rows: {len(rows)} ({rows[0]['A']} to {rows[-1]['A']})\n")
    for name, (folder, open_col, session_col, conf_col) in INDICES.items():
        bars = {str(b[0]): b for b in t.load_daily(os.path.join(argv[2], folder))}
        dates = sorted(bars); prev = {dates[i]: bars[dates[i - 1]] for i in range(1, len(dates))}
        opening, session, close_to_close, base_up, base_prev, base_gap = [], [], [], [], [], []
        unmapped = []
        for r in rows:
            d = r["A"]
            if d not in bars or d not in prev: continue
            o, c, pc = bars[d][1], bars[d][4], prev[d][4]
            gap, intraday, day = o - pc, c - o, c - pc
            do, ds = direction(r.get(open_col)), direction(r.get(session_col))
            if do is None: unmapped.append(r.get(open_col))
            if ds is None: unmapped.append(r.get(session_col))
            opening.append((do or 0, gap)); session.append((ds or 0, intraday)); close_to_close.append((ds or 0, day))
            base_up.append((1, intraday)); base_prev.append((1 if pc - prev.get(dates[dates.index(d) - 1], bars[d])[4] > 0 else -1, intraday))
            base_gap.append((1 if gap > 0 else -1, intraday))
        print(f"== {name}  ({len(opening)} scored days)")
        print(f"   opening bias vs actual gap:            {score(opening)}")
        print(f"   session bias vs open-to-close:         {score(session)}")
        print(f"   session bias vs previous close-to-close: {score(close_to_close)}")
        print(f"   baseline 'always up' (open-to-close):  {score(base_up)}")
        print(f"   baseline 'follow the gap' (open-to-close): {score(base_gap)}")
        print(f"   baseline 'same as yesterday' (open-to-close): {score(base_prev)}")
        if unmapped: print(f"   unmapped bias text: {sorted(set(x for x in unmapped if x))}")
        print()


if __name__ == "__main__":
    main(sys.argv)
