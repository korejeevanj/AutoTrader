"""Plumbing tests for trend.py on synthetic markets: a driftless random walk must not show an edge, persistent trends must.
Run: python -I scripts/research/test_trend.py   (from the repo root)"""
import datetime as dt, math, os, random, sys, unittest

sys.path.insert(0, os.path.dirname(__file__))
import trend as t


def market(days, seed, drift=0.0, regime_switch=0.0, regime_drift=0.0009, vol=0.011):
    """Daily OHLC bars. With regime_switch > 0 the drift flips sign at random (persistent trends); otherwise a plain random walk."""
    rng = random.Random(seed); price = 10000.0; d = dt.date(2005, 1, 3); bars = []; regime = 1
    while len(bars) < days:
        d += dt.timedelta(days=1)
        if d.weekday() >= 5: continue
        if rng.random() < regime_switch: regime = -regime
        mu = drift + regime * regime_drift if regime_switch else drift
        o = price * (1 + rng.gauss(0, vol / 4)); c = o * (1 + rng.gauss(mu, vol))
        h = max(o, c) * (1 + abs(rng.gauss(0, vol / 3))); l = min(o, c) * (1 - abs(rng.gauss(0, vol / 3)))
        bars.append((d, o, h, l, c)); price = c
    return bars


class TrendPlumbing(unittest.TestCase):
    def test_execution_model_buy_and_hold_matches_the_index(self):
        bars = market(500, 1, drift=0.0004)
        bh = t.daily_returns(bars, [1] * len(bars), 0.0)
        # holding from the first open: day 1 intraday return only, afterwards the index close-to-close return
        x = t.index_returns(bars)
        self.assertAlmostEqual(sum(math.log1p(v) for v in bh[1:]), sum(math.log1p(v) for v in x[1:]), places=6)

    def test_costs_are_charged_once_per_change(self):
        bars = market(50, 2)
        d = [0] * 10 + [1] * 10 + [0] * 30
        free = t.daily_returns(bars, d, 0.0); paid = t.daily_returns(bars, d, 0.01)
        self.assertAlmostEqual(sum(free) - sum(paid), 0.02, places=9)      # in and out

    def test_random_walk_has_no_edge(self):
        bars = market(4000, 3, drift=0.0002)
        for name, rule, long_flat in (("T1", t.rule_t1, False), ("T2", t.rule_t2, False), ("T3", t.rule_t3, True)):
            result = t.evaluate(bars, name, rule, long_flat)
            self.assertFalse(result["class"].startswith("EDGE"), result)

    def test_persistent_trends_are_detected_by_the_long_short_rules(self):
        bars = market(4000, 4, regime_switch=0.004, regime_drift=0.0025, vol=0.009)
        results = {name: t.evaluate(bars, name, rule, False) for name, rule in (("T1", t.rule_t1), ("T2", t.rule_t2))}
        self.assertTrue(any(r["t"] > t.T_REQUIRED for r in results.values()), results)

    def test_payoff_shape_is_reported(self):
        bars = market(3000, 5, regime_switch=0.004, regime_drift=0.0025, vol=0.009)
        payoff = t.evaluate(bars, "T1", t.rule_t1, False)["payoff"]
        self.assertGreater(payoff["trades"], 10)
        self.assertGreater(payoff["profit_factor"], 1.0)    # trending market: the rule makes money
        self.assertTrue(0.0 < payoff["win_rate"] <= 1.0 and payoff["avg_win_over_avg_loss"] > 0)


if __name__ == "__main__":
    unittest.main()
