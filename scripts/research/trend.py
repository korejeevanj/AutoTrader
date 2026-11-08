"""Pre-registered trend-following tests T1-T3 on daily index history (docs/25-RESEARCH-PROTOCOL.md, "Trend-following family").

The rules, cost model and decision criteria were frozen in the protocol before any result was seen; do not edit them to improve a result.

Usage:  python -I scripts/research/trend.py data/research/index-day/NIFTY_BANK [--out report.md]
Input:  raw Kite daily candle JSON files from POST /api/research/archive/index-history?interval=day. Python standard library only.
"""
import datetime as dt, glob, json, math, os, sys

COST_SIDE = 0.0005            # 0.05% of notional per side; stress case doubles it
T_REQUIRED = 2.64             # six pre-registered tests, Bonferroni 0.05/6 two-sided
LAG = 10                      # Newey-West (Bartlett) lag
CHANNEL, CHANDELIER_ATR, ATR_DAYS = 55, 3.0, 20
TSMOM_DAYS, SMA_FAST, SMA_SLOW = 252, 50, 200


def load_daily(folder):
    rows = {}
    for path in sorted(glob.glob(os.path.join(folder, "2*.json"))):
        with open(path, encoding="utf-8") as handle:
            for c in json.load(handle).get("data", {}).get("candles", []):
                rows[c[0][:10]] = (dt.date.fromisoformat(c[0][:10]), c[1], c[2], c[3], c[4])
    return [rows[k] for k in sorted(rows)]


# ---- rules: each returns the desired position decided at every close (+1, 0, -1) ----------------------------------------------------
def atr(bars, days=ATR_DAYS):
    tr = [bars[0][2] - bars[0][3]] + [max(b[2] - b[3], abs(b[2] - bars[i][4]), abs(b[3] - bars[i][4])) for i, b in enumerate(bars[1:])]
    out = [None] * len(bars)
    for i in range(days - 1, len(bars)): out[i] = sum(tr[i - days + 1:i + 1]) / days
    return out


def rule_t1(bars):
    a = atr(bars); d = [0] * len(bars); pos = 0; ext = 0.0
    for i in range(CHANNEL, len(bars)):
        close = bars[i][4]
        if pos == 0:
            if close > max(b[2] for b in bars[i - CHANNEL:i]): pos, ext = 1, close
            elif close < min(b[3] for b in bars[i - CHANNEL:i]): pos, ext = -1, close
        else:
            ext = max(ext, close) if pos == 1 else min(ext, close)
            stop = ext - CHANDELIER_ATR * a[i] if pos == 1 else ext + CHANDELIER_ATR * a[i]
            if (pos == 1 and close <= stop) or (pos == -1 and close >= stop): pos = 0
        d[i] = pos
    return d


def rule_t2(bars):
    d = [0] * len(bars); pos = 0
    for i in range(len(bars)):
        month_end = i + 1 < len(bars) and bars[i + 1][0].month != bars[i][0].month
        if i >= TSMOM_DAYS and month_end:
            ret = bars[i][4] / bars[i - TSMOM_DAYS][4] - 1
            pos = 1 if ret > 0 else -1 if ret < 0 else 0
        d[i] = pos
    return d


def rule_t3(bars):
    d = [0] * len(bars); closes = [b[4] for b in bars]
    for i in range(SMA_SLOW - 1, len(bars)):
        fast = sum(closes[i - SMA_FAST + 1:i + 1]) / SMA_FAST; slow = sum(closes[i - SMA_SLOW + 1:i + 1]) / SMA_SLOW
        d[i] = 1 if fast > slow else 0
    return d



# ---- execution model ----------------------------------------------------------------------------------------------------------------
def daily_returns(bars, d, cost):
    """Return for day i uses d[i-2] overnight (close i-1 to open i) and d[i-1] intraday (open i to close i); cost on every change at the open."""
    out = []
    for i in range(1, len(bars)):
        overnight = d[i - 2] if i >= 2 else 0; today = d[i - 1]
        r = (1 + overnight * (bars[i][1] / bars[i - 1][4] - 1)) * (1 + today * (bars[i][4] / bars[i][1] - 1)) - 1 - cost * abs(today - overnight)
        out.append(r)
    return out


def index_returns(bars): return [bars[i][4] / bars[i - 1][4] - 1 for i in range(1, len(bars))]


def mean(xs): return sum(xs) / len(xs)


def stdev(xs):
    m = mean(xs); return math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1))


def hac_t_mean(y, lag=LAG):
    """t-stat of the mean with Newey-West (Bartlett) variance."""
    n = len(y); m = mean(y); e = [v - m for v in y]
    s = sum(v * v for v in e) / n
    for l in range(1, lag + 1):
        s += 2 * (1 - l / (lag + 1)) * sum(e[t] * e[t - l] for t in range(l, n)) / n
    return m / math.sqrt(s / n) if s > 0 else 0.0


def alpha_beta_t(y, x, lag=LAG):
    """OLS y = a + b x; returns (a, b, t for a) with the Newey-West (Bartlett) sandwich covariance."""
    n = len(y); sx = sum(x); sxx = sum(v * v for v in x); sy = sum(y); sxy = sum(x[i] * y[i] for i in range(n))
    det = n * sxx - sx * sx
    b = (n * sxy - sx * sy) / det; a = (sy - b * sx) / n
    e = [y[i] - a - b * x[i] for i in range(n)]
    u = [(e[t], e[t] * x[t]) for t in range(n)]                       # score contributions
    s = [[sum(u[t][i] * u[t][j] for t in range(n)) for j in range(2)] for i in range(2)]
    for l in range(1, lag + 1):
        w = 1 - l / (lag + 1)
        for i in range(2):
            for j in range(2):
                s[i][j] += w * sum(u[t][i] * u[t - l][j] + u[t - l][i] * u[t][j] for t in range(l, n))
    inv = [[sxx / det, -sx / det], [-sx / det, n / det]]
    var_a = sum(inv[0][i] * s[i][j] * inv[j][0] for i in range(2) for j in range(2))
    return a, b, (a / math.sqrt(var_a) if var_a > 0 else 0.0)


def metrics(r, label_dates):
    n = len(r); eq = 1.0; peak = 1.0; mdd = 0.0
    for v in r:
        eq *= 1 + v; peak = max(peak, eq); mdd = max(mdd, 1 - eq / peak)
    years = n / 252
    return {"cagr": eq ** (1 / years) - 1, "vol": stdev(r) * math.sqrt(252), "sharpe": mean(r) / stdev(r) * math.sqrt(252) if stdev(r) else 0.0, "max_drawdown": mdd}


def trades(d, r):
    """Continuous holding periods as trades -> list of compounded net returns (daily r aligned: r[i] belongs to day i+1, held position d[i-1])."""
    out = []; current = None; side = 0
    for i in range(1, len(d)):
        pos = d[i - 1]
        if pos != side:
            if current is not None: out.append(current - 1)
            current = 1.0 if pos != 0 else None; side = pos
        if current is not None and i - 1 < len(r): current *= 1 + r[i - 1]
    if current is not None: out.append(current - 1)
    return out


def payoff(tr):
    if not tr: return {"trades": 0}
    wins = [t for t in tr if t > 0]; losses = [-t for t in tr if t <= 0]
    return {"trades": len(tr), "win_rate": round(len(wins) / len(tr), 3), "avg_win_over_avg_loss": round(mean(wins) / mean(losses), 2) if wins and losses else None,
            "profit_factor": round(sum(wins) / sum(losses), 2) if losses and sum(losses) > 0 else None}


def year_share(bars, r):
    by_year = {}
    for i, v in enumerate(r): by_year[bars[i + 1][0].year] = by_year.get(bars[i + 1][0].year, 0.0) + math.log1p(v)
    total = sum(by_year.values())
    return max(by_year.values()) / total if total > 0 else None


def evaluate(bars, name, rule, long_flat):
    d = rule(bars); x = index_returns(bars)
    r1 = daily_returns(bars, d, COST_SIDE); r2 = daily_returns(bars, d, 2 * COST_SIDE)
    bh = daily_returns(bars, [1] * len(bars), COST_SIDE); m = metrics(r1, None); mb = metrics(bh, None)
    if long_flat:
        exposure = mean([abs(v) for v in d[:-1]])
        excess = [r1[i] - exposure * x[i] for i in range(len(r1))]; t = hac_t_mean(excess); alpha = mean(excess)
        excess2 = [r2[i] - exposure * x[i] for i in range(len(r2))]
        thirds = [mean(excess[k * len(excess) // 3:(k + 1) * len(excess) // 3]) for k in range(3)]
        alpha2 = mean(excess2)
    else:
        alpha, beta, t = alpha_beta_t(r1, x); alpha2 = alpha_beta_t(r2, x)[0]
        thirds = []
        for k in range(3):
            lo, hi = k * len(r1) // 3, (k + 1) * len(r1) // 3
            thirds.append(alpha_beta_t(r1[lo:hi], x[lo:hi])[0])
    share = year_share(bars, r1)
    sharpe_ok = m["sharpe"] >= mb["sharpe"]
    risk_managed = (not sharpe_ok) and m["cagr"] >= 0.6 * mb["cagr"] and m["max_drawdown"] <= 0.6 * mb["max_drawdown"]
    edge = (t >= T_REQUIRED and all(v > 0 for v in thirds) and alpha2 > 0 and (share is not None and share <= 0.40) and sharpe_ok)
    klass = "EDGE (needs out-of-sample confirmation)" if edge else "RISK-MANAGED (not an edge)" if risk_managed else "KILLED"
    return {"rule": name, "class": klass, "alpha_per_day": round(alpha, 6), "alpha_annualised_pct": round(alpha * 252 * 100, 2), "t": round(t, 2),
            "alpha_by_third": [round(v * 252 * 100, 2) for v in thirds], "alpha_positive_at_2x_cost": alpha2 > 0,
            "max_year_share_of_log_profit": round(share, 2) if share is not None else None,
            "net": {k: round(v, 4) for k, v in m.items()}, "buy_and_hold": {k: round(v, 4) for k, v in mb.items()},
            "exposure_avg": round(mean([abs(v) for v in d[:-1]]), 2), "payoff": payoff(trades(d, r1))}


def main(argv):
    if len(argv) < 2: sys.exit(__doc__)
    bars = load_daily(argv[1]); out = argv[argv.index("--out") + 1] if "--out" in argv else None
    lines = [f"# Pre-registered trend-following results", f"Daily bars: {len(bars)} ({bars[0][0]} to {bars[-1][0]}); cost {COST_SIDE * 100:.2f}% per side; required |t| {T_REQUIRED}.", ""]
    for name, rule, long_flat in (("T1 55-day channel breakout, 3 ATR chandelier stop, long/short", rule_t1, False),
                                  ("T2 12-month time-series momentum, monthly, long/short", rule_t2, False),
                                  ("T3 50/200-day trend filter, long/flat", rule_t3, True)):
        lines.append("- " + json.dumps(evaluate(bars, name, rule, long_flat)))
    report = "\n".join(lines); print(report)
    if out:
        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        with open(out, "w", encoding="utf-8") as handle: handle.write(report + "\n")


if __name__ == "__main__":
    main(sys.argv)
