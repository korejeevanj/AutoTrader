"""Plumbing tests for option_sim.py and signal_search.py on synthetic markets.
Run: python -I scripts/research/test_signal_search.py   (from the repo root)"""
import datetime as dt, math, os, random, sys, unittest

sys.path.insert(0, os.path.dirname(__file__))
import option_sim as o
import signal_search as s


def synthetic_days(count, seed, regime_drift=0.0, regime_minutes=90, vix=15.0, start=dt.date(2022, 1, 3)):
    """Minute index sessions as Day objects. With regime_drift > 0 the intraday drift flips sign at random (a planted, tradable momentum)."""
    rng = random.Random(seed); days = []; d = start
    sigma = vix / 100 / math.sqrt(252 * 375)
    while len(days) < count:
        d += dt.timedelta(days=1)
        if d.weekday() >= 5: continue
        price = 40000.0 * math.exp(rng.gauss(0, 0.01))          # stationary level: no compounding across days
        m = list(range(9 * 60 + 15, 15 * 60 + 30)); io = []; ih = []; il = []; ic = []; regime = 1
        for _ in m:
            if regime_drift and rng.random() < 1 / regime_minutes: regime = -regime
            op = price; cl = op * (1 + rng.gauss(regime * regime_drift, sigma))
            io.append(op); ic.append(cl); ih.append(max(op, cl) * (1 + abs(rng.gauss(0, sigma / 4)))); il.append(min(op, cl) * (1 - abs(rng.gauss(0, sigma / 4)))); price = cl
        days.append(o.Day(d, m, io, ih, il, ic, [vix] * len(m), [vix] * len(m)))
    return days


class OptionModel(unittest.TestCase):
    def test_put_call_parity(self):
        c = o.black76(55000, 55000, 14 / 365, 0.15, True); p = o.black76(55000, 55000, 14 / 365, 0.15, False)
        self.assertAlmostEqual(c - p, 0.0, places=6)

    def test_round_trip_fees_match_the_paper_schedule(self):
        total = o.fee(400, 30, True) + o.fee(400, 30, False)
        self.assertAlmostEqual(total, 75.3, delta=0.5)         # Rs 20 + Rs 20 brokerage, exchange, GST, stamp, 0.15% tax on the sell

    def test_a_flat_market_costs_the_spread_and_fees(self):
        days = synthetic_days(1, 1, vix=15.0); day = days[0]
        day.io = day.ih = day.il = day.ic = [40000.0] * len(day.m)          # no movement at all
        trade = o.trade(day, 30, 1, o.Params(stop=0.5))
        self.assertLess(trade["net"], -75.0)                    # at least the fees; spread, slippage and time decay on top

    def test_trade_exits_by_the_end_of_the_session(self):
        day = synthetic_days(1, 2)[0]
        trade = o.trade(day, 30, 1, o.Params(stop=0.9))
        self.assertLessEqual(day.m[trade["exit_bar"]], 15 * 60 + 15)


class Search(unittest.TestCase):
    COMBOS = [(0, False, 0.12), (1, False, 0.12), (2, False, 0.12), (3, False, 0.12), (6, False, 0.12), (7, False, 0.12)]

    def run_search(self, days):
        arrays = s.direction_arrays(days); medians = s.vix_medians(days)
        return s.evaluate(days, arrays, medians, first_test=dt.date(2023, 1, 2), months_train=12, months_test=6, combos=self.COMBOS, min_train=30)

    def test_signals_use_only_past_data(self):
        days = synthetic_days(30, 3)
        before = s.direction_arrays(days)
        changed = synthetic_days(30, 3)
        # perturb only the second half of the last day; directions decided earlier in that day must not change
        last = changed[-1]
        for k in range(200, len(last.ic)): last.ic[k] *= 1.05; last.ih[k] *= 1.05; last.il[k] *= 1.05
        after = s.direction_arrays(changed)
        for a, b in zip(before[-1], after[-1]): self.assertEqual(list(a[:200]), list(b[:200]))

    def test_noise_is_killed(self):
        result = self.run_search(synthetic_days(900, 4))
        self.assertTrue(result["verdict"].startswith("KILLED"), result["oos"])

    def test_a_planted_momentum_edge_is_found_and_survives(self):
        result = self.run_search(synthetic_days(900, 5, regime_drift=0.00012))
        self.assertGreater(result["oos"]["mean_net_rupees"], 0, result["oos"])
        self.assertGreater(result["oos_day_t"], s.T_REQUIRED, result["oos_day_t"])
        self.assertTrue(result["verdict"].startswith("SURVIVES"), result["checks"])


if __name__ == "__main__":
    unittest.main()
