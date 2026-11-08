"""One coin-flip option trade per session with rupee loss cuts and profit trails (docs/25 closing summary).
Exploratory diagnostic, run once from the repo root: python -I scripts/research/diagnostics/coinflip.py
Reads data/research (gitignored). Not a pre-registered test."""
import os, sys, random, statistics; sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import option_sim as o, signal_search as s
days = s.load_days("data/research/index-minute/NIFTY_BANK", "data/research/index-minute/INDIA_VIX")
rng = random.Random(42)
plan = []
for d in days:                                       # one coin-flip trade per session at a random minute 09:30-14:00
    i = rng.randrange(15, 285); plan.append((d, i, rng.choice((1, -1))))
OFF = 0.99                                           # disable the percentage trailing stop
rules = [
    ("Cut at -1,000, hold winners to 15:15", o.Params(stop=OFF, stop_rupees=1000)),
    ("Cut at -2,000, hold winners to 15:15", o.Params(stop=OFF, stop_rupees=2000)),
    ("Cut at -1,000, trail winners after +1,000 (give back 500)", o.Params(stop=OFF, stop_rupees=1000, trail_activation_rupees=1000, trail_giveback_rupees=500)),
    ("Cut at -1,000, trail winners after +2,000 (give back 1,000)", o.Params(stop=OFF, stop_rupees=1000, trail_activation_rupees=2000, trail_giveback_rupees=1000)),
    ("Cut at -2,000, trail winners after +3,000 (give back 1,500)", o.Params(stop=OFF, stop_rupees=2000, trail_activation_rupees=3000, trail_giveback_rupees=1500)),
    ("Cut at -1,000, take profit at +3,000", o.Params(stop=OFF, stop_rupees=1000, target_rupees=3000)),
    ("Cut at -1,000, take profit at +5,000", o.Params(stop=OFF, stop_rupees=1000, target_rupees=5000)),
    ("No stop, no target: hold to 15:15 (pure coin flip)", o.Params(stop=OFF)),
]
print(f"{len(plan)} sessions, one coin-flip trade each (1 lot = 30 units, ATM option, real fees, spread and slippage). Start capital Rs 5,00,000.\n")
print(f"{'rule':58s} {'avg/trade':>9} {'win%':>5} {'avg win':>8} {'avg loss':>9} {'biggest win':>11} {'worst':>8} {'end capital':>12} {'deepest fall':>12}")
for name, p in rules:
    trades = [o.trade(d, i, dr, p) for d, i, dr in plan]; trades = [t for t in trades if t]
    nets = [t["net"] for t in trades]; w = [x for x in nets if x > 0]; l = [x for x in nets if x <= 0]
    eq = 500000; peak = eq; dd = 0
    for x in nets: eq += x; peak = max(peak, eq); dd = max(dd, peak - eq)
    print(f"{name:58s} {statistics.mean(nets):+9,.0f} {len(w)/len(nets):5.0%} {statistics.mean(w):+8,.0f} {statistics.mean(l):+9,.0f} {max(nets):+11,.0f} {min(nets):+8,.0f} {eq:12,.0f} {dd:12,.0f}")
