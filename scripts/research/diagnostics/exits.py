"""Same A3 out-of-sample entries with ten different exits (docs/25, 'Diagnostics after A3').
Exploratory diagnostic, run once from the repo root: python -I scripts/research/diagnostics/exits.py
Reads data/research (gitignored). Not a pre-registered test."""
import os, sys, statistics; sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import option_sim as o, signal_search as s
days = s.load_days("data/research/index-minute/NIFTY_BANK", "data/research/index-minute/INDIA_VIX")
arrays = s.direction_arrays(days); medians = s.vix_medians(days)
cat = s.signal_catalogue(); combos = [(k, f, x) for k in range(len(cat)) for f in (False, True) for x in s.STOPS]
wf, _ = s.walk_forward(days, arrays, medians, combos)
entries = [(t["date"], t["signal_bar"], t["dir"]) for b in wf for t in b["test"]]
by = {d.date: d for d in days}
def run(p):
    r = [o.trade(by[d], i, dr, p) for d, i, dr in entries]; r = [x for x in r if x]
    nets = [x["net"] for x in r]; m = statistics.mean(nets); se = statistics.stdev(nets) / len(nets) ** 0.5
    return m, se, sum(1 for x in nets if x > 0) / len(nets)
print("Same 536 out-of-sample entries, different exits (mean net Rs per trade, 95% band, win rate):")
rules = [("trailing 8% (as tested)", o.Params(stop=0.08)), ("trailing 16%", o.Params(stop=0.16)), ("trailing 30%", o.Params(stop=0.30)),
         ("take profit Rs 500, trailing 16%", o.Params(stop=0.16, target_rupees=500)), ("take profit Rs 1,000, trailing 16%", o.Params(stop=0.16, target_rupees=1000)),
         ("take profit Rs 2,000, trailing 16%", o.Params(stop=0.16, target_rupees=2000)),
         ("no stop, exit after 5 min", o.Params(stop=0.99, max_minutes=5)), ("no stop, exit after 15 min", o.Params(stop=0.99, max_minutes=15)),
         ("no stop, exit after 60 min", o.Params(stop=0.99, max_minutes=60)), ("no stop, hold to 15:15", o.Params(stop=0.99))]
for name, p in rules:
    m, se, w = run(p); print(f"  {name:36s} {m:+8.0f}  (+/- {1.96*se:4.0f})  win {w:.0%}")
