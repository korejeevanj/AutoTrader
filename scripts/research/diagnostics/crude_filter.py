"""Coin-flip trades after big MCX crude moves versus other days (docs/25, outside forces section).
Exploratory diagnostic, run once from the repo root: python -I scripts/research/diagnostics/crude_filter.py
Reads data/research (gitignored). Not a pre-registered test."""
import os, sys, random, statistics, bisect, datetime as dt; sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import option_sim as o, signal_search as s, trend as t
days = s.load_days("data/research/index-minute/NIFTY_BANK", "data/research/index-minute/INDIA_VIX")
crude = {b[0]: b[4] for b in t.load_daily("data/research/index-day/FUT_MCX_CRUDEOIL")}; keys = sorted(crude)
def crude_move(d):
    j = bisect.bisect_left(keys, d); return abs(crude[keys[j - 1]] / crude[keys[j - 2]] - 1) if j >= 2 else None
rng = random.Random(11); P = o.Params(stop=0.99, stop_rupees=1000, trail_activation_rupees=1000, trail_giveback_rupees=500)
rows = []
for d in days:
    cm = crude_move(d.date)
    if cm is None: continue
    for _ in range(3):
        i = rng.randrange(15, 285); tr = o.trade(d, i, rng.choice((1, -1)), P)
        if tr: rows.append((d.date, cm, tr["net"]))
train = [r for r in rows if r[0] < dt.date(2024, 1, 1)]; test = [r for r in rows if r[0] >= dt.date(2024, 1, 1)]
cut = sorted(r[1] for r in train)[int(0.8 * len(train))]          # "big crude move" threshold fixed on 2022-23
for label, sel in (("2022-23", train), ("2024-26 (unseen)", test)):
    big = [r[2] for r in sel if r[1] >= cut]; rest = [r[2] for r in sel if r[1] < cut]
    print(f"{label:17s} coin-flip option trades after a BIG crude move (>{cut*100:.1f}%): {statistics.mean(big):+5.0f}/trade ({len(big)})   other days: {statistics.mean(rest):+5.0f}/trade ({len(rest)})")
