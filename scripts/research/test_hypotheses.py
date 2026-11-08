"""Plumbing tests for hypotheses.py on synthetic data: pure noise must be KILLED, an injected effect must be detected.
Run: python -I scripts/research/test_hypotheses.py   (from the repo root)"""
import datetime as dt, json, os, random, sys, tempfile, unittest

sys.path.insert(0, os.path.dirname(__file__))
import hypotheses as h


def synthetic(days, seed, momentum=0.0, daily_vol=1.0, start=dt.date(2023, 1, 2), cluster=False):
    """Random-walk index sessions as Kite candle files. `momentum` makes the last 30 minutes follow the earlier day's direction."""
    rng = random.Random(seed); price = 40000.0; folder = tempfile.mkdtemp(); candles = []; d = start; count = 0; log_vol = 0.0
    while count < days:
        d += dt.timedelta(days=1)
        if d.weekday() >= 5: continue
        count += 1
        if cluster: log_vol = 0.9 * log_vol + rng.gauss(0, 0.35)       # persistent volatility regimes
        step = price * daily_vol * math.exp(log_vol if cluster else 0.0) / 100 / math.sqrt(375)
        opens = []; drift = 0.0
        for m in range(375):
            stamp = dt.datetime.combine(d, dt.time(9, 15)) + dt.timedelta(minutes=m)
            late = stamp.time() >= dt.time(15, 0)
            mu = momentum * (1 if price >= opens[0] else -1) * step if late and opens and momentum else 0.0
            o = price; c = o + rng.gauss(mu, step); hi = max(o, c) + abs(rng.gauss(0, step / 3)); lo = min(o, c) - abs(rng.gauss(0, step / 3))
            if not opens: opens.append(o)
            candles.append([stamp.strftime("%Y-%m-%dT%H:%M:%S+0530"), round(o, 2), round(hi, 2), round(lo, 2), round(c, 2), 1000])
            price = c
        price += rng.gauss(0, step * 3)
    with open(os.path.join(folder, "all.json"), "w") as handle: json.dump({"status": "success", "data": {"candles": candles}}, handle)
    return folder


import math


class HypothesisPlumbing(unittest.TestCase):
    def test_loader_keeps_only_full_sessions(self):
        days = h.parse_files(synthetic(30, 1))
        self.assertEqual(len(days), 30)
        self.assertTrue(all(len(bars) == 375 for bars in days.values()))

    def test_noise_is_killed(self):
        days = h.parse_files(synthetic(600, 2))
        for series in (h.h2(days, "first30"), h.h2(days, "rest"), h.h3(days)):
            summary = h.summarize(series, "noise")
            self.assertEqual(h.verdict(summary, 10.0), "KILLED")   # costs make a no-edge market lose

    def test_injected_close_momentum_is_detected(self):
        days = h.parse_files(synthetic(500, 3, momentum=1.6))
        summary = h.summarize(h.h2(days, "rest"), "injected")
        self.assertGreater(summary["mean_net_points_per_day"], 10.0)
        self.assertGreater(summary["t"], h.T_REQUIRED)

    def test_regime_forecast_is_killed_when_volatility_is_constant(self):
        result = h.h1(h.parse_files(synthetic(400, 4)))
        self.assertTrue(result["verdict"].startswith("KILLED"), result)

    def test_regime_forecast_survives_when_volatility_clusters(self):
        result = h.h1(h.parse_files(synthetic(500, 7, cluster=True)))
        self.assertTrue(result["verdict"].startswith("SURVIVES"), result)

    def test_too_little_data_is_reported_not_guessed(self):
        self.assertEqual(h.h1(h.parse_files(synthetic(40, 5)))["verdict"], "INSUFFICIENT DAYS")
        self.assertEqual(h.summarize(h.h2(h.parse_files(synthetic(8, 6)), "rest"), "x")["verdict"], "INSUFFICIENT DAYS")


if __name__ == "__main__":
    unittest.main()
