"""Coin-flip trades split by conditions known at entry; filters chosen on 2022-23, checked on 2024-26 (docs/25, outside forces section).
Exploratory diagnostic, run once from the repo root: python -I scripts/research/diagnostics/filters.py
Reads data/research (gitignored). Not a pre-registered test."""
import os, sys, random, statistics, math, datetime as dt; sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import option_sim as o, signal_search as s
days = s.load_days("data/research/index-minute/NIFTY_BANK", "data/research/index-minute/INDIA_VIX")
rng = random.Random(11)
P = o.Params(stop=0.99, stop_rupees=1000, trail_activation_rupees=1000, trail_giveback_rupees=500)   # best rule from the coin-flip test
rows = []
for k, d in enumerate(days):
    if k == 0: continue
    prev_close = days[k - 1].ic[-1]
    for _ in range(3):
        i = rng.randrange(15, 285); direction = rng.choice((1, -1))
        t = o.trade(d, i, direction, P)
        if not t: continue
        rng_so_far = (max(d.ih[:i + 1]) - min(d.il[:i + 1])) / d.io[0] * 100
        recent = abs(d.ic[i] - d.ic[i - 15]) / d.ic[i] * 100
        feats = {"gap size %": abs(d.io[0] / prev_close - 1) * 100,
                 "day range so far %": rng_so_far,
                 "range so far vs VIX-expected": rng_so_far / (d.vc[i] / math.sqrt(252)),
                 "last 15 min move %": recent,
                 "India VIX level": d.vc[i],
                 "VIX change since open %": (d.vc[i] / d.vo[0] - 1) * 100,
                 "minutes since 09:15": i}
        rows.append((d.date, feats, t["net"]))
train = [r for r in rows if r[0] < dt.date(2024, 1, 1)]; test = [r for r in rows if r[0] >= dt.date(2024, 1, 1)]
def m(xs): return statistics.mean(xs) if xs else float("nan")
print(f"Coin-flip trades: {len(rows)} ({len(train)} in 2022-23 to choose filters, {len(test)} in 2024-26 to check them).")
print(f"Unfiltered: 2022-23 avg Rs {m([r[2] for r in train]):+.0f}/trade; 2024-26 avg Rs {m([r[2] for r in test]):+.0f}/trade\n")
print(f"{'feature (terciles fixed on 2022-23)':32s} {'bucket':>16} {'2022-23 avg':>12} {'2024-26 avg':>12} {'2024-26 trades':>14}")
best = []
for f in rows[0][1]:
    vals = sorted(r[1][f] for r in train); lo, hi = vals[len(vals) // 3], vals[2 * len(vals) // 3]
    for name, cond in (("low", lambda v, lo=lo: v <= lo), ("middle", lambda v, lo=lo, hi=hi: lo < v <= hi), ("high", lambda v, hi=hi: v > hi)):
        tr = [r[2] for r in train if cond(r[1][f])]; te = [r[2] for r in test if cond(r[1][f])]
        best.append((m(tr), f, name, m(te), len(te), cond))
        print(f"{f:32s} {name + f' ({lo:.2f}-{hi:.2f})' if name=='middle' else name:>16} {m(tr):+12.0f} {m(te):+12.0f} {len(te):14d}")
best.sort(key=lambda x: -x[0])
print("\nFilters chosen on 2022-23 (top 3 by 2022-23 average), and what they did on 2024-26:")
for tr, f, name, te, n, _ in best[:3]: print(f"   {f} = {name}: 2022-23 {tr:+.0f}/trade -> 2024-26 {te:+.0f}/trade over {n} trades")
c1, c2 = best[0][5], best[1][5]; f1, f2 = best[0][1], best[1][1]
both_tr = [r[2] for r in train if c1(r[1][f1]) and c2(r[1][f2])]; both_te = [r[2] for r in test if c1(r[1][f1]) and c2(r[1][f2])]
print(f"   both top filters together: 2022-23 {m(both_tr):+.0f}/trade ({len(both_tr)}) -> 2024-26 {m(both_te):+.0f}/trade ({len(both_te)})")
