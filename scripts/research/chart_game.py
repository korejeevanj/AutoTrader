"""Blind chart game: can an educated guess from Bank Nifty 10-minute candles and the banks' 5-minute candles beat chance?

  build:  python -I scripts/research/chart_game.py build <index-minute root> <out dir> [count] [players]
          writes players' batches (no dates, no outcomes, prices as % of the previous close) and a separate answers file
  score:  python -I scripts/research/chart_game.py score <out dir>
          reads calls-*.json (one per player) and scores them against the answers, with baselines on the same scenarios

Bar (fixed before playing): about 56% of calls right, or a mean index move of at least +19 points in the call's direction over the next 15 minutes.
Python standard library only.
"""
import datetime as dt, glob, json, math, os, random, statistics, sys
sys.path.insert(0, os.path.dirname(__file__))
import hypotheses as h
from breadth import WEIGHTS

SEED = 20261101
COST_POINTS, HIT_BAR = 19.0, 0.56


def build(root, out, count=200, players=4):
    index = h.parse_files(os.path.join(root, "NIFTY_BANK"))
    vix = {d: {b[0]: b[4] for b in bars} for d, bars in h.parse_files(os.path.join(root, "INDIA_VIX")).items()}
    banks = {k: {d: {b[0]: b[4] for b in bars} for d, bars in h.parse_files(os.path.join(root, k)).items()} for k in WEIGHTS}
    rng = random.Random(SEED); dates = sorted(index)
    prev = {dates[i]: index[dates[i - 1]][-1][4] for i in range(1, len(dates))}
    prev_bank = {k: {} for k in WEIGHTS}
    for k in WEIGHTS:
        bd = sorted(banks[k])
        for i in range(1, len(bd)): prev_bank[k][bd[i]] = banks[k][bd[i - 1]][max(banks[k][bd[i - 1]])]
    chosen = rng.sample(dates[1:], count * 2); scenarios = []; answers = {}
    for d in chosen:
        if len(scenarios) == count: break
        bars = index[d]; t = [b[0] for b in bars]; pos = {x: i for i, x in enumerate(t)}
        decision = dt.time(10, 0)
        decision = (dt.datetime.combine(d, dt.time(10, 0)) + dt.timedelta(minutes=5 * rng.randrange(0, 55))).time()   # 10:00 .. 14:30
        i = pos.get(decision)
        if i is None or i + 30 >= len(bars) or d not in prev: continue
        pc = prev[d]; pct = lambda v: round((v / pc - 1) * 100, 3)
        # index 10-minute candles from 09:15 up to the decision minute (only completed candles)
        candles = []
        for start in range(0, i + 1 - 10 + 1, 10):
            chunk = bars[start:start + 10]
            if start + 10 > i + 1: break
            candles.append((chunk[0][0].strftime("%H:%M"), pct(chunk[0][1]), pct(max(b[2] for b in chunk)), pct(min(b[3] for b in chunk)), pct(chunk[-1][4])))
        bank_rows = []; ok = True
        for k, w in WEIGHTS.items():
            day = banks[k].get(d, {}); base = prev_bank[k].get(d)
            closes = []
            for back in range(55, -1, -5):                     # last 12 five-minute closes, oldest first
                tt = t[i - back] if i - back >= 0 else None
                if tt is None or tt not in day or not base: closes.append(None); continue
                closes.append(round((day[tt] / base - 1) * 100, 2))
            if closes[-1] is None: ok = False; break
            bank_rows.append((k, w, closes))
        if not ok or d not in vix or decision not in vix[d]: continue
        vix_change = round((vix[d][decision] / vix[d][min(vix[d])] - 1) * 100, 1)
        sid = f"S{len(scenarios) + 1:03d}"
        scenarios.append({"id": sid, "time": decision.strftime("%H:%M"), "vix_change_today_pct": vix_change, "index10": candles, "banks5": bank_rows})
        answers[sid] = {"date": str(d), "move15_points": round(bars[i + 15][4] - bars[i][4], 2), "move30_points": round(bars[i + 30][4] - bars[i][4], 2),
                        # facts the baselines need, kept out of the players' batches
                        "index10_move_points": round(bars[i][4] - bars[i - 10][4], 2),
                        "bank5_up_share": sum(w for k, w, c in bank_rows if c[-1] is not None and c[-2] is not None and c[-1] > c[-2]) / sum(WEIGHTS.values())}
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, "answers.json"), "w", encoding="utf-8") as f: json.dump(answers, f)
    per = math.ceil(len(scenarios) / players)
    for p in range(players):
        with open(os.path.join(out, f"batch-{p + 1}.txt"), "w", encoding="utf-8") as f:
            for s in scenarios[p * per:(p + 1) * per]:
                f.write(f"=== {s['id']} | time {s['time']} IST | India VIX change today {s['vix_change_today_pct']:+.1f}% ===\n")
                f.write("Bank Nifty 10-minute candles, % from previous close (start open high low close):\n")
                for c in s["index10"]: f.write(f"  {c[0]} {c[1]:+.3f} {c[2]:+.3f} {c[3]:+.3f} {c[4]:+.3f}\n")
                f.write("Banks, weight %, last 12 five-minute closes as % from previous close (oldest -> newest, ~1 hour):\n")
                for k, w, cl in s["banks5"]: f.write(f"  {k:<11} {w:5.2f}: " + " ".join("  n/a" if v is None else f"{v:+.2f}" for v in cl) + "\n")
                f.write("\n")
    print(f"built {len(scenarios)} scenarios into {players} batches; answers kept separately")


def stats(calls, answers):
    rows = [(answers[s]["move15_points"] * (1 if c == "UP" else -1), conf) for s, c, conf in calls if c in ("UP", "DOWN")]
    if not rows: return {"calls": 0}
    moves = [m for m, _ in rows]; n = len(moves); k = sum(1 for m in moves if m > 0)
    p_chance = sum(math.comb(n, j) for j in range(k, n + 1)) / 2 ** n
    se = statistics.stdev(moves) / math.sqrt(n) if n > 2 else float("nan")
    return {"calls": n, "hit_rate": round(k / n, 3), "mean_move15_in_call_direction": round(statistics.mean(moves), 1),
            "band95": round(1.96 * se, 1), "chance_of_this_many_right_or_more_if_guessing": round(p_chance, 3),
            "meets_bar": (k / n >= HIT_BAR and p_chance < 0.05) or (statistics.mean(moves) >= COST_POINTS and statistics.mean(moves) - 1.96 * se > 0)}


def score(out):
    answers = json.load(open(os.path.join(out, "answers.json"), encoding="utf-8"))
    report = {}; all_calls = []
    for path in sorted(glob.glob(os.path.join(out, "calls-*.json"))):
        calls = [(c["id"], c["call"].upper(), int(c.get("confidence", 1))) for c in json.load(open(path, encoding="utf-8")) if c["id"] in answers]
        all_calls += calls; report[os.path.basename(path)] = stats(calls, answers)
        report[os.path.basename(path)]["skips"] = sum(1 for _, c, _ in calls if c not in ("UP", "DOWN"))
    report["ALL PLAYERS"] = stats(all_calls, answers)
    for level in (1, 2, 3): report[f"ALL PLAYERS, confidence {level}"] = stats([c for c in all_calls if c[2] == level], answers)
    ids = list(answers)
    rule = [(s, "UP" if answers[s]["index10_move_points"] > 0 else "DOWN", 1) for s in ids
            if abs(answers[s]["index10_move_points"]) >= 30 and (answers[s]["bank5_up_share"] >= 0.7 if answers[s]["index10_move_points"] > 0 else answers[s]["bank5_up_share"] <= 0.3)]
    report["BASELINE your approach as a rule (index 10m >= 30 pts and banks 5m >= 70% agree, follow)"] = stats(rule, answers)
    rng = random.Random(7)
    report["BASELINE random guesses (all scenarios)"] = stats([(s, rng.choice(["UP", "DOWN"]), 1) for s in ids], answers)
    report["BASELINE always UP"] = stats([(s, "UP", 1) for s in ids], answers)
    for name, r in report.items(): print(f"{name}: {json.dumps(r)}")
    with open(os.path.join(out, "report.json"), "w", encoding="utf-8") as f: json.dump(report, f, indent=1)


if __name__ == "__main__":
    if len(sys.argv) >= 4 and sys.argv[1] == "build": build(sys.argv[2], sys.argv[3], *(int(x) for x in sys.argv[4:6]))
    elif len(sys.argv) == 3 and sys.argv[1] == "score": score(sys.argv[2])
    else: sys.exit(__doc__)
