"""Pre-registered tests H1-H3 on Bank Nifty index minute history (docs/25-RESEARCH-PROTOCOL.md).

The definitions below are FROZEN before any real data was looked at. Do not tune them after seeing results; to test a variation, add a new
hypothesis to the protocol document first and count it in the multiple-testing correction.

Usage:  python -I scripts/research/hypotheses.py <index-minute folder, e.g. data/research/index-minute/NIFTY_BANK> [--vix <folder>] [--out report.md]
Input:  raw Kite candle JSON files saved by POST /api/research/archive/index-history.
Python standard library only.
"""
import glob, json, math, os, sys, datetime as dt
from collections import defaultdict

# ---- frozen parameters -------------------------------------------------------------------------------------------------------------
COST_POINTS = 9.5            # round trip, index points: ~Rs 112 cost at ~Rs 15 per point plus slippage (docs/24). Stress test uses 2x.
STRUCTURAL_BREAK = dt.date(2024, 11, 20)   # Bank Nifty weekly expiries ended 13 Nov 2024; split the sample here.
ALPHA_FAMILY = 0.05 / 3      # three pre-registered hypotheses (Bonferroni); two-sided
T_REQUIRED = 2.40            # |t| for ALPHA_FAMILY with large samples
H3_BREAKOUT_MINUTES = 10
H3_HOLD_MINUTES = 20
H3_DISASTER_STOP = 80.0
H3_LAST_ENTRY = dt.time(14, 30)
MIN_BARS = 330               # a usable session has most of its 375 minute bars


def parse_files(folder):
    """-> {date: [(time, open, high, low, close, volume)]} from Kite candle JSON files (duplicates across windows are merged)."""
    days = defaultdict(dict)
    for path in sorted(glob.glob(os.path.join(folder, "*.json"))):
        if os.path.basename(path).startswith("manifest"): continue
        with open(path, encoding="utf-8") as handle:
            payload = json.load(handle)
        for candle in payload.get("data", {}).get("candles", []):
            stamp = dt.datetime.strptime(candle[0][:19], "%Y-%m-%dT%H:%M:%S")
            if dt.time(9, 15) <= stamp.time() < dt.time(15, 30):
                days[stamp.date()][stamp.time()] = (stamp.time(), candle[1], candle[2], candle[3], candle[4], candle[5] if len(candle) > 5 else 0)
    return {d: [bars[t] for t in sorted(bars)] for d, bars in sorted(days.items()) if len(bars) >= MIN_BARS}


def mean(xs): return sum(xs) / len(xs)


def stdev(xs):
    m = mean(xs); return math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))


def t_stat(xs):
    if len(xs) < 3: return 0.0
    s = stdev(xs)
    return 0.0 if s == 0 else mean(xs) / (s / math.sqrt(len(xs)))


def summarize(per_day, label, cost_free=None):
    """per_day: {date: net points that day}. Reports mean, t, subperiods, dependence on the best days and a 2x cost stress."""
    rows = sorted(per_day.items())
    values = [v for _, v in rows]
    if len(values) < 10: return {"label": label, "days": len(values), "verdict": "INSUFFICIENT DAYS"}
    pre = [v for d, v in rows if d < STRUCTURAL_BREAK]; post = [v for d, v in rows if d >= STRUCTURAL_BREAK]
    top = sorted(values, reverse=True)[:max(1, len(values) // 20)]
    total = sum(values)
    return {"label": label, "days": len(values), "mean_net_points_per_day": round(mean(values), 2), "t": round(t_stat(values), 2),
            "pre_break": {"days": len(pre), "mean": round(mean(pre), 2) if pre else None, "t": round(t_stat(pre), 2) if len(pre) > 2 else None},
            "post_break": {"days": len(post), "mean": round(mean(post), 2) if post else None, "t": round(t_stat(post), 2) if len(post) > 2 else None},
            "share_of_total_from_best_5pct_days": round(sum(top) / total, 2) if total > 0 else None,
            "hit_rate": round(sum(1 for v in values if v > 0) / len(values), 3)}


def verdict(summary, min_mean):
    if summary.get("verdict"): return summary["verdict"]
    ok = (summary["mean_net_points_per_day"] >= min_mean and summary["t"] >= T_REQUIRED
          and all(p["mean"] is not None and p["mean"] > 0 for p in (summary["pre_break"], summary["post_break"]))
          and (summary["share_of_total_from_best_5pct_days"] or 1) <= 0.5)
    return "SURVIVES (still needs out-of-sample confirmation)" if ok else "KILLED"


# ---- H2: intraday momentum into the close -----------------------------------------------------------------------------------------
def h2(days, signal, cost=COST_POINTS):
    out = {}; prev_close = None
    for d, bars in days.items():
        at = {b[0]: b for b in bars}
        p0944, p1459, p1529 = at.get(dt.time(9, 44)), at.get(dt.time(14, 59)), at.get(dt.time(15, 29))
        if prev_close is not None and p0944 and p1459 and p1529:
            ref = (p0944[4] - prev_close) if signal == "first30" else (p1459[4] - prev_close)
            if ref != 0:
                direction = 1 if ref > 0 else -1
                out[d] = direction * (p1529[4] - p1459[4]) - cost
        prev_close = bars[-1][4]
    return out


# ---- H3: 10-minute breakout continuation, 20-minute hold --------------------------------------------------------------------------
def h3(days, cost=COST_POINTS):
    out = {}
    for d, bars in days.items():
        pnl = 0.0; i = H3_BREAKOUT_MINUTES + 15; trades = 0
        while i < len(bars) - H3_HOLD_MINUTES - 2:
            t = bars[i][0]
            if t > H3_LAST_ENTRY: break
            window = bars[i - H3_BREAKOUT_MINUTES:i]
            close = bars[i][4]
            direction = 1 if close > max(b[2] for b in window) else -1 if close < min(b[3] for b in window) else 0
            if direction == 0: i += 1; continue
            entry = bars[i + 1][1]; exit_price = None
            for j in range(i + 1, i + 1 + H3_HOLD_MINUTES):
                adverse = (entry - bars[j][3]) if direction == 1 else (bars[j][2] - entry)
                if adverse >= H3_DISASTER_STOP:
                    exit_price = entry - direction * (H3_DISASTER_STOP + 2.0); break
            if exit_price is None: exit_price = bars[i + 1 + H3_HOLD_MINUTES][1]
            pnl += direction * (exit_price - entry) - cost; trades += 1
            i += 1 + H3_HOLD_MINUTES                     # one position at a time
        if trades: out[d] = pnl
    return out


# ---- H1: regime forecast ----------------------------------------------------------------------------------------------------------
def ols(rows, targets):
    """Least squares by normal equations (small feature count), pure Python."""
    k = len(rows[0]); a = [[0.0] * k for _ in range(k)]; b = [0.0] * k
    for x, y in zip(rows, targets):
        for i in range(k):
            b[i] += x[i] * y
            for j in range(k): a[i][j] += x[i] * x[j]
    for i in range(k): a[i][i] += 1e-9
    for col in range(k):
        pivot = max(range(col, k), key=lambda r: abs(a[r][col])); a[col], a[pivot] = a[pivot], a[col]; b[col], b[pivot] = b[pivot], b[col]
        for r in range(col + 1, k):
            f = a[r][col] / a[col][col]
            for c in range(col, k): a[r][c] -= f * a[col][c]
            b[r] -= f * b[col]
    beta = [0.0] * k
    for i in reversed(range(k)):
        beta[i] = (b[i] - sum(a[i][j] * beta[j] for j in range(i + 1, k))) / a[i][i]
    return beta


def h1(days, vix_by_day=None):
    """Walk-forward forecast of log(rest-of-day range, 09:30-15:30) from opening-range width, gap, prior-day range, prior-day close-to-close |return|, VIX at open."""
    feats = []; prev = None; history = []
    trailing = []
    for d, bars in days.items():
        if prev is not None:
            opening = [b for b in bars if b[0] < dt.time(9, 30)]
            rest = [b for b in bars if b[0] >= dt.time(9, 30)]
            if opening and rest:
                orw = max(b[2] for b in opening) - min(b[3] for b in opening)
                rng = max(b[2] for b in rest) - min(b[3] for b in rest)
                gap = abs(bars[0][1] - prev["close"]) / prev["close"] * 100
                row = [1.0, math.log(max(orw, 1)), gap, math.log(prev["range"]), prev["absret"]]
                if vix_by_day is not None:
                    if d not in vix_by_day: prev = None; continue
                    row.append(vix_by_day[d])
                trailing.append(mean(history[-20:]) if history else math.log(prev["range"]))
                feats.append((d, row, math.log(rng), math.log(prev["range"])))
                history.append(math.log(rng))
        prev = {"close": bars[-1][4], "range": max(b[2] for b in bars) - min(b[3] for b in bars),
                "absret": abs(bars[-1][4] - bars[0][1]) / bars[0][1] * 100}
    start = max(120, len(feats) // 3)
    if len(feats) < start + 30: return {"verdict": "INSUFFICIENT DAYS", "days": len(feats)}
    err_model = []; err_base = []; err_trailing = []
    for i in range(start, len(feats)):
        beta = ols([f[1] for f in feats[:i]], [f[2] for f in feats[:i]])
        pred = sum(c * x for c, x in zip(beta, feats[i][1]))
        err_model.append((pred - feats[i][2]) ** 2); err_base.append((feats[i][3] - feats[i][2]) ** 2); err_trailing.append((trailing[i] - feats[i][2]) ** 2)
    rmse_model = math.sqrt(mean(err_model)); rmse_base = math.sqrt(mean(err_base)); rmse_trailing = math.sqrt(mean(err_trailing))
    improvement = 1 - rmse_model / min(rmse_base, rmse_trailing)   # must beat the better of yesterday's range and the trailing 20-day average
    return {"days_forecast": len(err_model), "rmse_model_log": round(rmse_model, 4), "rmse_prior_day_range_log": round(rmse_base, 4), "rmse_trailing_20d_log": round(rmse_trailing, 4),
            "improvement": round(improvement, 3), "verdict": "SURVIVES (still needs a filter-lift check on H3)" if improvement >= 0.10 else "KILLED"}


def load_vix(folder):
    """-> {date: VIX at the first bar}. Optional."""
    days = parse_files(folder)
    return {d: bars[0][1] for d, bars in days.items()} if days else None


def main(argv):
    if len(argv) < 2: sys.exit(__doc__)
    folder = argv[1]; vix = None; out = None
    if "--vix" in argv: vix = load_vix(argv[argv.index("--vix") + 1])
    if "--out" in argv: out = argv[argv.index("--out") + 1]
    days = parse_files(folder)
    lines = [f"# Pre-registered hypothesis results", f"Sessions: {len(days)} ({min(days) if days else '-'} to {max(days) if days else '-'}); cost {COST_POINTS} pts/round trip; required |t| {T_REQUIRED} (3-hypothesis correction).", ""]
    results = {}
    for name, run, min_mean in (("H2a first-30-minute signal -> last 30 minutes", lambda c: h2(days, "first30", c), 10.0),
                                ("H2b prev-close-to-14:59 signal -> last 30 minutes", lambda c: h2(days, "rest", c), 10.0),
                                ("H3 10-minute breakout, 20-minute hold", lambda c: h3(days, c), 12.0)):
        base = summarize(run(COST_POINTS), name); stressed = summarize(run(2 * COST_POINTS), name + " [2x cost]")
        base["verdict"] = verdict(base, min_mean)
        if base["verdict"].startswith("SURVIVES") and not (stressed.get("mean_net_points_per_day") or -1) > 0:
            base["verdict"] = "KILLED (not positive at 2x cost)"
        results[name] = base
        lines.append(f"- {name}: {json.dumps({k: v for k, v in base.items() if k != 'label'})}")
        lines.append(f"    at 2x cost: mean {stressed.get('mean_net_points_per_day')} t {stressed.get('t')}")
    lines.append(""); lines.append("H1 regime forecast: " + json.dumps(h1(days, vix)))
    report = "\n".join(lines)
    print(report)
    if out:
        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        with open(out, "w", encoding="utf-8") as handle: handle.write(report + "\n")


if __name__ == "__main__":
    main(sys.argv)
