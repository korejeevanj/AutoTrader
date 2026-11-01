"""Pre-registered multi-timeframe candle model over Bank Nifty and its 14 banks (docs/25-RESEARCH-PROTOCOL.md). Frozen before any result.

Usage: python -I scripts/research/candles.py <index-minute root, e.g. data/research/index-minute>
Python standard library only.
"""
import datetime as dt, math, os, statistics, sys
sys.path.insert(0, os.path.dirname(__file__))
import hypotheses as h
from breadth import WEIGHTS

SCALES = (5, 10, 15)
HORIZON = 15
DECISION_MINUTES = list(range(9 * 60 + 45, 14 * 60 + 46, 15))
RIDGE = 10.0
T_REQUIRED = 2.77
COST_POINTS = 19.0
TOP_TWO = ("HDFCBANK", "ICICIBANK")


def load(root):
    index = h.parse_files(os.path.join(root, "NIFTY_BANK"))
    vix = {d: {b[0]: b[4] for b in bars} for d, bars in h.parse_files(os.path.join(root, "INDIA_VIX")).items()}
    banks = {k: {d: {b[0]: (b[4], b[5]) for b in bars} for d, bars in h.parse_files(os.path.join(root, k)).items()} for k in WEIGHTS}
    return index, vix, banks


def features_for_day(d, bars, vix_day, banks):
    """-> list of (date, decision minute, feature vector, next-15-minute index move in points). Uses only data up to the decision minute."""
    times = [b[0] for b in bars]; minute = [t.hour * 60 + t.minute for t in times]; n = len(times)
    hi = [b[2] for b in bars]; lo = [b[3] for b in bars]; c = [b[4] for b in bars]
    pos = {m: k for k, m in enumerate(minute)}; out = []
    # per bank, closes and cumulative volume aligned to the index minutes (None where the bank has no candle)
    aligned = {}
    for k in WEIGHTS:
        day = banks[k].get(d, {}); closes = [None] * n; cum = [0.0] * n; seen = [0] * n; total = 0.0; count = 0
        for i, t in enumerate(times):
            if t in day: closes[i] = day[t][0]; total += day[t][1]; count += 1
            cum[i] = total; seen[i] = count
        aligned[k] = (closes, cum, seen)
    for decision in DECISION_MINUTES:
        i = pos.get(decision)
        if i is None or i < 15 or i + HORIZON >= n: continue
        t_now = times[i]
        if vix_day is None or t_now not in vix_day or times[i - 15] not in vix_day: continue
        row = []; complete = True
        for s in SCALES:
            r_index = c[i] / c[i - s] - 1
            window_hi = max(hi[i - s + 1:i + 1]); window_lo = min(lo[i - s + 1:i + 1])
            close_pos = (c[i] - window_lo) / (window_hi - window_lo) if window_hi > window_lo else 0.5
            rets = []; weights = []; vol_now = 0.0; vol_avg = 0.0; top = []
            for k, w in WEIGHTS.items():
                closes, cum, seen = aligned[k]
                if closes[i] is not None and closes[i - s] is not None and closes[i - s] > 0:
                    r = closes[i] / closes[i - s] - 1; rets.append(r); weights.append(w)
                    if k in TOP_TWO: top.append(r)
                    if seen[i]: vol_now += cum[i] - cum[i - s]; vol_avg += cum[i] / seen[i] * s
            if sum(weights) < 0.9 * sum(WEIGHTS.values()): complete = False; break
            wsum = sum(weights); bank_ret = sum(w * r for w, r in zip(weights, rets)) / wsum
            up_share = sum(w for w, r in zip(weights, rets) if r > 0) / wsum
            spread = math.sqrt(sum(w * (r - bank_ret) ** 2 for w, r in zip(weights, rets)) / wsum)
            row += [r_index * 100, close_pos, bank_ret * 100, (bank_ret - r_index) * 100, up_share, spread * 100,
                    (sum(top) / len(top) * 100) if top else 0.0, (vol_now / vol_avg) if vol_avg > 0 else 1.0]
        if not complete: continue
        row += [(c[i] / bars[0][1] - 1) * 100, (decision - 555) / 375.0, vix_day[t_now], vix_day[t_now] - vix_day[times[i - 15]]]
        out.append((d, decision, row, c[i + HORIZON] - c[i]))
    return out


def build(index, vix, banks):
    rows = []
    for d, bars in index.items(): rows += features_for_day(d, bars, vix.get(d), banks)
    return rows


def ridge_fit(X, y, penalty=RIDGE):
    """Ridge on standardised features with an unpenalised intercept. -> (means, sds, beta, intercept). Pure Python normal equations."""
    k = len(X[0]); n = len(X)
    means = [sum(r[j] for r in X) / n for j in range(k)]
    sds = [math.sqrt(sum((r[j] - means[j]) ** 2 for r in X) / n) or 1.0 for j in range(k)]
    Z = [[(r[j] - means[j]) / sds[j] for j in range(k)] for r in X]; ym = sum(y) / n
    A = [[0.0] * k for _ in range(k)]; b = [0.0] * k
    for z, t in zip(Z, y):
        e = t - ym
        for i in range(k):
            zi = z[i]; b[i] += zi * e; Ai = A[i]
            for j in range(i, k): Ai[j] += zi * z[j]
    for i in range(k):
        for j in range(i): A[i][j] = A[j][i]
        A[i][i] += penalty
    # Gaussian elimination
    M = [A[i][:] + [b[i]] for i in range(k)]
    for col in range(k):
        p = max(range(col, k), key=lambda r: abs(M[r][col])); M[col], M[p] = M[p], M[col]
        for r in range(col + 1, k):
            f = M[r][col] / M[col][col]
            for cc in range(col, k + 1): M[r][cc] -= f * M[col][cc]
    beta = [0.0] * k
    for i in reversed(range(k)): beta[i] = (M[i][k] - sum(M[i][j] * beta[j] for j in range(i + 1, k))) / M[i][i]
    return means, sds, beta, ym


def predict(model, x):
    means, sds, beta, intercept = model
    return intercept + sum(b * (v - m) / s for b, v, m, s in zip(beta, x, means, sds))


def blocks(first=dt.date(2024, 1, 1), last=dt.date(2026, 12, 31)):
    out = []; start = first
    while start <= last:
        end = dt.date(start.year + (start.month + 5) // 12, (start.month + 5) % 12 + 1, 1); out.append((start, end)); start = end
    return out


def evaluate(rows, first=dt.date(2024, 1, 1)):
    calls = []; all_points = []; block_results = []
    for lo, hi in blocks(first, max(r[0] for r in rows)):
        train = [r for r in rows if r[0] < lo]; test = [r for r in rows if lo <= r[0] < hi]
        if len(train) < 1000 or not test: continue
        model = ridge_fit([r[2] for r in train], [r[3] for r in train])
        fitted = sorted(predict(model, r[2]) for r in train)
        low, high = fitted[len(fitted) // 10], fitted[9 * len(fitted) // 10]
        block = []
        for r in test:
            p = predict(model, r[2]); all_points.append((p, r[3]))
            if p >= high: block.append((r[0], 1, r[3]))
            elif p <= low: block.append((r[0], -1, r[3]))
        calls += block
        block_results.append((lo, hi, len(block), sum(s * m for _, s, m in block) / len(block) if block else 0.0))
    signed = [s * m for _, s, m in calls]
    by_day = {}
    for d, s, m in calls: by_day[d] = by_day.get(d, []) + [s * m]
    day_means = [statistics.mean(v) for v in by_day.values()]
    t = statistics.mean(day_means) / (statistics.stdev(day_means) / math.sqrt(len(day_means))) if len(day_means) > 2 else 0.0
    ps = [p for p, _ in all_points]; ms = [m for _, m in all_points]
    corr = statistics.correlation(ps, ms) if len(ps) > 2 else 0.0
    hit = sum(1 for x in signed if x > 0) / len(signed) if signed else 0.0
    mean_move = statistics.mean(signed) if signed else 0.0
    positive_blocks = sum(1 for b in block_results if b[3] > 0)
    in_2024 = [s * m for d, s, m in calls if d.year == 2024]; later = [s * m for d, s, m in calls if d.year >= 2025]
    ok = (mean_move >= COST_POINTS and t >= T_REQUIRED and positive_blocks >= 4 and in_2024 and later
          and statistics.mean(in_2024) > 0 and statistics.mean(later) > 0)
    return {"verdict": "SURVIVES (needs an option-level test)" if ok else "KILLED", "calls": len(signed), "call_days": len(by_day),
            "mean_move_points_in_call_direction": round(mean_move, 2), "day_clustered_t": round(t, 2), "hit_rate": round(hit, 3),
            "positive_blocks": f"{positive_blocks} of {len(block_results)}",
            "blocks": [(str(a), str(b), n, round(m, 2)) for a, b, n, m in block_results],
            "prediction_outcome_correlation_all_points": round(corr, 4), "out_of_sample_points": len(all_points)}


def main(argv):
    if len(argv) < 2: sys.exit(__doc__)
    index, vix, banks = load(argv[1]); rows = build(index, vix, banks)
    print(f"Decision points with complete data: {len(rows):,} over {len(set(r[0] for r in rows))} sessions; features per point: {len(rows[0][2])}")
    result = evaluate(rows)
    for k, v in result.items(): print(f"  {k}: {v}")


if __name__ == "__main__":
    main(sys.argv)
