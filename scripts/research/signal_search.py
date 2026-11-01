"""Automated, walk-forward search for intraday long-option entry rules (docs/25-RESEARCH-PROTOCOL.md, "Intraday long-option search").

The search space, procedure and success criteria were frozen in the protocol before any result was seen. Do not change them to improve a result.

Usage:  python -I scripts/research/signal_search.py <NIFTY_BANK minute folder> <INDIA_VIX minute folder> [--out report.md]
Python standard library only.
"""
import datetime as dt, json, math, os, statistics, sys
from array import array

sys.path.insert(0, os.path.dirname(__file__))
import hypotheses as h
import option_sim as o

T_REQUIRED = 2.69                      # seven pre-registered tests, Bonferroni 0.05/7 two-sided
STOPS = (0.08, 0.12, 0.16)
FIRST_BAR, LAST_SIGNAL_MINUTE, MAX_TRADES_PER_DAY = 15, 14 * 60 + 30, 3
MIN_TRAIN_TRADES = 100


# ---- data -----------------------------------------------------------------------------------------------------------------------------
def load_days(index_folder, vix_folder):
    index = h.parse_files(index_folder); vix = h.parse_files(vix_folder); days = []
    for date, bars in index.items():
        vbars = {b[0]: b for b in vix.get(date, [])}
        if len(vbars) < h.MIN_BARS: continue
        aligned = [(b, vbars[b[0]]) for b in bars if b[0] in vbars]
        if len(aligned) < h.MIN_BARS: continue
        days.append(o.Day(date, [b[0].hour * 60 + b[0].minute for b, _ in aligned], [b[1] for b, _ in aligned], [b[2] for b, _ in aligned],
                          [b[3] for b, _ in aligned], [b[4] for b, _ in aligned], [v[1] for _, v in aligned], [v[4] for _, v in aligned]))
    return days


# ---- signals --------------------------------------------------------------------------------------------------------------------------
def signal_catalogue():
    """18 signals: momentum (N, theta, with/against) = 12, break (N or prior day, with/against) = 6."""
    items = []
    for n in (15, 30, 60):
        for theta in (1.5, 2.0):
            for side in (1, -1): items.append(("M", n, theta, side))
    for n in (30, 60, "D"):
        for side in (1, -1): items.append(("B", n, None, side))
    return items


def direction_arrays(days):
    """For each day, one array('b') of -1/0/+1 per signal over every bar index (decided at the close of that bar), using only data up to that bar."""
    catalogue = signal_catalogue(); out = []
    for k, day in enumerate(days):
        n = len(day.m); ic = day.ic
        logret = [0.0] + [math.log(ic[i] / ic[i - 1]) for i in range(1, n)]
        c1 = [0.0] * (n + 1); c2 = [0.0] * (n + 1)
        for i in range(n): c1[i + 1] = c1[i] + logret[i]; c2[i + 1] = c2[i] + logret[i] * logret[i]
        prev_high = days[k - 1].ih and max(days[k - 1].ih) if k else None
        prev_low = min(days[k - 1].il) if k else None
        arrays = []
        for kind, nn, theta, side in catalogue:
            a = array("b", [0]) * n
            for i in range(FIRST_BAR, n):
                if day.m[i] > LAST_SIGNAL_MINUTE: break
                d = 0
                if kind == "M":
                    if i < nn: continue
                    lo = max(1, i - 119); cnt = i - lo + 1
                    if cnt < 15: continue
                    mean_ = (c1[i + 1] - c1[lo]) / cnt; var = (c2[i + 1] - c2[lo]) / cnt - mean_ * mean_
                    if var <= 0: continue
                    z = (ic[i] / ic[i - nn] - 1) / (math.sqrt(var) * math.sqrt(nn))
                    d = 1 if z >= theta else -1 if z <= -theta else 0
                elif nn == "D":
                    if prev_high is None: continue
                    d = 1 if ic[i] > prev_high else -1 if ic[i] < prev_low else 0
                else:
                    if i < nn: continue
                    d = 1 if ic[i] > max(day.ih[i - nn:i]) else -1 if ic[i] < min(day.il[i - nn:i]) else 0
                a[i] = d * side
            arrays.append(a)
        out.append(arrays)
    return out


def vix_medians(days):
    """Median of the last 60 sessions' final India VIX close, for each day (None when fewer than 20 sessions are available)."""
    closes = [d.vc[-1] for d in days]; out = []
    for k in range(len(days)): out.append(statistics.median(closes[max(0, k - 60):k]) if k >= 20 else None)
    return out


# ---- simulation -----------------------------------------------------------------------------------------------------------------------
def run_combo(days, arrays, medians, combo, params, lo=None, hi=None):
    """combo = (signal index, high_vol_filter, stop). Trades for days with lo <= date < hi."""
    sig, filtered, stop = combo; params.stop = stop; trades = []
    for k, day in enumerate(days):
        if (lo and day.date < lo) or (hi and day.date >= hi): continue
        if filtered and medians[k] is None: continue
        a = arrays[k][sig]; i = FIRST_BAR; count = 0; n = len(day.m)
        while i < n - 3 and count < MAX_TRADES_PER_DAY and day.m[i] <= LAST_SIGNAL_MINUTE:
            d = a[i]
            if d and (not filtered or day.vc[i] > medians[k]):
                t = o.trade(day, i, d, params)
                if t is None: break
                trades.append(t); count += 1; i = max(t["exit_bar"], i + 1)
            else: i += 1
    return trades


def summarize(trades):
    if not trades: return {"trades": 0}
    nets = [t["net"] for t in trades]; wins = [x for x in nets if x > 0]; losses = [-x for x in nets if x <= 0]
    return {"trades": len(nets), "mean_net_rupees": round(sum(nets) / len(nets), 1), "total_net_rupees": round(sum(nets)),
            "win_rate": round(len(wins) / len(nets), 3), "avg_win_over_avg_loss": round((sum(wins) / len(wins)) / (sum(losses) / len(losses)), 2) if wins and losses else None,
            "profit_factor": round(sum(wins) / sum(losses), 2) if losses and sum(losses) else None,
            "avg_minutes": round(sum(t["minutes"] for t in trades) / len(nets), 1), "avg_entry_premium": round(sum(t["entry"] for t in trades) / len(nets), 1)}


def day_totals(trades, dates):
    by = {}
    for t in trades: by[t["date"]] = by.get(t["date"], 0.0) + t["net"]
    return [by.get(d, 0.0) for d in dates]


def t_stat(xs):
    if len(xs) < 3: return 0.0
    s = h.stdev(xs); return 0.0 if s == 0 else h.mean(xs) / (s / math.sqrt(len(xs)))


def blocks(days, first_test=dt.date(2024, 1, 1), months_train=24, months_test=6):
    def add(d, months):
        y, m = divmod(d.month - 1 + months, 12); return dt.date(d.year + y, m + 1, 1)
    last = days[-1].date; out = []; start = first_test
    while start <= last:
        end = add(start, months_test); out.append((add(start, -months_train), start, end)); start = end
    return out


def walk_forward(days, arrays, medians, combos, base=None, first_test=dt.date(2024, 1, 1), months_train=24, months_test=6, min_train=MIN_TRAIN_TRADES):
    base = base or o.Params()
    cache = {c: run_combo(days, arrays, medians, c, o.Params(base.vol_scale, base.expiry_days, base.spread_mult), None, None) for c in combos}
    results = []
    for train_lo, test_lo, test_hi in blocks(days, first_test, months_train, months_test):
        best = None
        for c, trades in cache.items():
            train = [t for t in trades if train_lo <= t["date"] < test_lo]
            if len(train) < min_train: continue
            score = sum(t["net"] for t in train) / len(train)
            if best is None or score > best[0]: best = (score, c)
        if best is None: continue
        test = [t for t in cache[best[1]] if test_lo <= t["date"] < test_hi]
        mean_all = [sum(t["net"] for t in tr if test_lo <= t["date"] < test_hi) / max(1, sum(1 for t in tr if test_lo <= t["date"] < test_hi)) for tr in cache.values()]
        results.append({"train_from": train_lo, "test_from": test_lo, "test_to": test_hi, "combo": best[1], "train_mean": round(best[0], 1), "test": test,
                        "all_combo_mean_oos": round(sum(mean_all) / len(mean_all), 1)})
    return results, cache


def describe(combo):
    kind, nn, theta, side = signal_catalogue()[combo[0]]
    name = (f"momentum N={nn} theta={theta}" if kind == "M" else f"break {nn}") + (" WITH the move" if side == 1 else " AGAINST the move")
    return f"{name}; {'VIX above 60-session median' if combo[1] else 'no filter'}; trailing stop {int(combo[2] * 100)}%"


def evaluate(days, arrays, medians, first_test=dt.date(2024, 1, 1), months_train=24, months_test=6, combos=None, min_train=MIN_TRAIN_TRADES):
    catalogue = signal_catalogue()
    combos = combos or [(s, f, x) for s in range(len(catalogue)) for f in (False, True) for x in STOPS]
    wf, _ = walk_forward(days, arrays, medians, combos, None, first_test, months_train, months_test, min_train)
    test_days = [d.date for d in days if wf and wf[0]["test_from"] <= d.date < wf[-1]["test_to"]]
    oos = [t for block in wf for t in block["test"]]
    totals = day_totals(oos, test_days)
    per_block = [sum(t["net"] for t in b["test"]) for b in wf]
    stress = []
    for block in wf:
        stress += run_combo(days, arrays, medians, block["combo"], o.Params(spread_mult=2.0), block["test_from"], block["test_to"])
    sens = {}
    for name, p in (("vol_scale_0.85", o.Params(vol_scale=0.85)), ("vol_scale_1.15", o.Params(vol_scale=1.15)), ("expiry_7d", o.Params(expiry_days=7)), ("expiry_30d", o.Params(expiry_days=30))):
        tr = []
        for block in wf: tr += run_combo(days, arrays, medians, block["combo"], p, block["test_from"], block["test_to"])
        sens[name] = summarize(tr).get("mean_net_rupees")
    total = sum(per_block)
    summary = summarize(oos)
    worst_day = min(totals) if totals else None
    eq = 0.0; peak = 0.0; mdd = 0.0
    for v in totals: eq += v; peak = max(peak, eq); mdd = max(mdd, peak - eq)
    checks = {"mean_net_positive_and_t": summary.get("mean_net_rupees", -1) > 0 and t_stat(totals) >= T_REQUIRED,
              "at_least_4_of_5_blocks_positive": sum(1 for v in per_block if v > 0) >= 4,
              "positive_with_doubled_spread": (summarize(stress).get("mean_net_rupees") or -1) > 0,
              "no_block_over_50pct_of_profit": total > 0 and max(per_block) <= 0.5 * total,
              "sensitivities_not_reversed": all((v or -1) > 0 for v in sens.values())}
    verdict = "SURVIVES (needs validation on real option prices)" if all(checks.values()) else "KILLED"
    return {"verdict": verdict, "checks": checks, "oos": summary, "oos_day_t": round(t_stat(totals), 2), "oos_days": len(totals),
            "blocks": [{"test": f"{b['test_from']}..{b['test_to']}", "chosen": describe(b["combo"]), "train_mean_rupees": b["train_mean"],
                        "oos_net_rupees": round(sum(t["net"] for t in b["test"])), "oos_trades": len(b["test"]), "oos_mean_all_combos": b["all_combo_mean_oos"]} for b in wf],
            "doubled_spread": summarize(stress), "sensitivity_mean_net_rupees": sens, "worst_day_rupees": round(worst_day) if worst_day is not None else None,
            "max_drawdown_rupees_per_lot": round(mdd), "combinations": len(combos)}


def main(argv):
    if len(argv) < 3: sys.exit(__doc__)
    days = load_days(argv[1], argv[2]); out = argv[argv.index("--out") + 1] if "--out" in argv else None
    arrays = direction_arrays(days); medians = vix_medians(days)
    result = evaluate(days, arrays, medians)
    report = f"# Intraday long-option search (walk-forward)\nSessions {len(days)} ({days[0].date} to {days[-1].date}); required |t| {T_REQUIRED}\n\n" + json.dumps(result, indent=1, default=str)
    print(report)
    if out:
        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        with open(out, "w", encoding="utf-8") as handle: handle.write(report + "\n")


if __name__ == "__main__":
    main(sys.argv)
