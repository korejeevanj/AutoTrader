"""Pre-registered bank-breadth lead test (docs/25-RESEARCH-PROTOCOL.md, "Bank breadth lead"). Frozen before the constituent data was fetched.

Usage: python -I scripts/research/breadth.py <index-minute root, e.g. data/research/index-minute>
Python standard library only.
"""
import datetime as dt, math, os, statistics, sys
sys.path.insert(0, os.path.dirname(__file__))
import hypotheses as h

WEIGHTS = {"HDFCBANK": 18.61, "ICICIBANK": 14.18, "KOTAKBANK": 10.2, "AXISBANK": 10.06, "SBIN": 9.93, "FEDERALBNK": 6.56, "INDUSINDBK": 4.97,
           "AUBANK": 4.85, "IDFCFIRSTB": 4.54, "BANKBARODA": 3.59, "CANBK": 3.37, "PNB": 3.27, "YESBANK": 3.01, "UNIONBANK": 2.86}
LOOKBACK = 5
T_REQUIRED = 2.69
ECONOMIC_POINTS = 19.0
START, END = dt.time(9, 30), dt.time(15, 0)


def load(root):
    index = h.parse_files(os.path.join(root, "NIFTY_BANK"))
    banks = {k: {d: {b[0]: b[4] for b in bars} for d, bars in h.parse_files(os.path.join(root, k)).items()} for k in WEIGHTS}
    return index, banks


def samples(index, banks, horizon):
    """-> list of (date, D in percent, next-horizon index move in points), sampled every `horizon` minutes, non-overlapping."""
    out = []
    for d, bars in index.items():
        times = [b[0] for b in bars]; closes = [b[4] for b in bars]; pos = {t: k for k, t in enumerate(times)}
        i = next((k for k, t in enumerate(times) if t >= START and k >= LOOKBACK), None)
        while i is not None and i + horizon < len(times) and times[i] <= END:
            t_now, t_then = times[i], times[i - LOOKBACK]
            num = 0.0; wsum = 0.0
            for k, w in WEIGHTS.items():
                day = banks[k].get(d)
                if day and t_now in day and t_then in day and day[t_then] > 0:
                    num += w * (day[t_now] / day[t_then] - 1); wsum += w
            if wsum >= 0.9 * sum(WEIGHTS.values()):
                divergence = (num / wsum - (closes[i] / closes[i - LOOKBACK] - 1)) * 100
                out.append((d, divergence, closes[i + horizon] - closes[i]))
            i += horizon
    return out


def clustered_slope(rows):
    xs = [r[1] for r in rows]; ys = [r[2] for r in rows]; mx, my = statistics.mean(xs), statistics.mean(ys)
    sxx = sum((x - mx) ** 2 for x in xs); b = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx; a = my - b * mx
    by_day = {}
    for (d, x, y) in rows: by_day[d] = by_day.get(d, 0.0) + (x - mx) * (y - a - b * x)
    var = sum(v * v for v in by_day.values()) / sxx ** 2
    return b, b / math.sqrt(var), len(by_day)


def main(argv):
    if len(argv) < 2: sys.exit(__doc__)
    index, banks = load(argv[1])
    print(f"Sessions: {len(index)}; banks loaded: {len(banks)}; lookback {LOOKBACK} min; weights: current published (not point-in-time)\n")
    verdicts = []
    for horizon in (5, 15):
        rows = samples(index, banks, horizon)
        b, t, days = clustered_slope(rows)
        sd = statistics.pstdev([r[1] for r in rows])
        print(f"H={horizon:>2} min: {len(rows):,} samples over {days} days; slope {b:+.1f} index points per 1% divergence "
              f"(a 1-sd divergence of {sd:.3f}% -> {b * sd:+.2f} pts); day-clustered t {t:+.2f} (needs {T_REQUIRED})")
        verdicts.append(abs(t) >= T_REQUIRED and b > 0)
    rows = samples(index, banks, 15)
    base = sorted(r[1] for r in rows if r[0].year == 2022)
    low, high = base[len(base) // 10], base[9 * len(base) // 10]
    later = [r for r in rows if r[0].year >= 2023]
    top = [r[2] for r in later if r[1] >= high]; bottom = [r[2] for r in later if r[1] <= low]
    diff = statistics.mean(top) - statistics.mean(bottom)
    se = math.sqrt(statistics.variance(top) / len(top) + statistics.variance(bottom) / len(bottom))
    print(f"\nEconomic test (2022 deciles: bottom <= {low:+.3f}%, top >= {high:+.3f}%; applied to 2023-2026):")
    print(f"   next 15 min after top decile: {statistics.mean(top):+.1f} pts (n={len(top)}); after bottom decile: {statistics.mean(bottom):+.1f} pts (n={len(bottom)})")
    print(f"   difference {diff:+.1f} pts (+/- {1.96 * se:.1f}); needs >= {ECONOMIC_POINTS} pts")
    economic = diff >= ECONOMIC_POINTS
    verdict = "SURVIVES (would still need an option-level test)" if (verdicts[1] and economic) else "KILLED"
    print(f"\nVerdict: {verdict}  [predictive H=15: {'pass' if verdicts[1] else 'fail'}; economic: {'pass' if economic else 'fail'}]")


if __name__ == "__main__":
    main(sys.argv)
