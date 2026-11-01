"""Plumbing tests for candles.py: pure noise must be KILLED, a planted lead of the banks over the index must SURVIVE, and features must not see the future.
Run: python -I scripts/research/test_candles.py   (from the repo root)"""
import datetime as dt, math, os, random, sys, unittest

sys.path.insert(0, os.path.dirname(__file__))
import candles as c
from breadth import WEIGHTS


def synthetic(days, seed, lag=0, start=dt.date(2022, 1, 3)):
    """Banks follow random walks; the index is their weighted average delayed by `lag` minutes (lag > 0 plants a real lead), plus noise."""
    rng = random.Random(seed); index = {}; vix = {}; banks = {k: {} for k in WEIGHTS}; d = start; count = 0
    sigma = 0.0006
    while count < days:
        d += dt.timedelta(days=1)
        if d.weekday() >= 5: continue
        count += 1
        times = [(dt.datetime.combine(d, dt.time(9, 15)) + dt.timedelta(minutes=m)).time() for m in range(375)]
        paths = {}
        for k in WEIGHTS:
            p = 100.0; path = []
            for _ in times: p *= 1 + rng.gauss(0, sigma); path.append(p)
            paths[k] = path; banks[k][d] = {t: (path[i], 1000 + rng.random() * 100) for i, t in enumerate(times)}
        wsum = sum(WEIGHTS.values()); bars = []
        for i, t in enumerate(times):
            j = max(0, i - lag)
            level = 50000 * sum(w * paths[k][j] / 100 for k, w in WEIGHTS.items()) / wsum * (1 + rng.gauss(0, sigma / 4))
            bars.append((t, level, level * 1.0002, level * 0.9998, level, 0))
        index[d] = bars; vix[d] = {t: 15.0 + rng.gauss(0, 0.1) for t in times}
    return index, vix, banks


class CandleModel(unittest.TestCase):
    def test_noise_is_killed(self):
        rows = c.build(*synthetic(700, 1, lag=0))
        self.assertTrue(c.evaluate(rows, first=dt.date(2023, 7, 1))["verdict"].startswith("KILLED"))

    def test_a_planted_bank_lead_is_found(self):
        rows = c.build(*synthetic(900, 2, lag=10))
        result = c.evaluate(rows, first=dt.date(2023, 1, 1))          # five blocks, so the 4-positive-blocks rule can be met
        self.assertGreater(result["prediction_outcome_correlation_all_points"], 0.2, result)
        self.assertTrue(result["verdict"].startswith("SURVIVES"), result)

    def test_features_do_not_see_the_future(self):
        index, vix, banks = synthetic(3, 3)
        d = sorted(index)[-1]
        before = c.features_for_day(d, index[d], vix[d], banks)
        for k in banks:                      # change everything after 11:00 in the banks
            banks[k][d] = {t: ((v[0] * 1.1, v[1] * 5) if t >= dt.time(11, 0) else v) for t, v in banks[k][d].items()}
        after = c.features_for_day(d, index[d], vix[d], banks)
        for x, y in zip(before, after):
            if x[1] < 11 * 60: self.assertEqual(x[2], y[2])


if __name__ == "__main__":
    unittest.main()
