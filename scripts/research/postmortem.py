"""Trade post-mortem for the walk-forward long-option search: were the losses caused by entries or by exits?

Usage: python -I scripts/research/postmortem.py <NIFTY_BANK minute folder> <INDIA_VIX minute folder>
Diagnostic only (it reads the same out-of-sample trades as checkpoint A3; nothing here feeds back into a rule).
"""
import os, statistics, sys
sys.path.insert(0, os.path.dirname(__file__))
import option_sim as o
import signal_search as s


def main(argv):
    days = s.load_days(argv[1], argv[2]); arrays = s.direction_arrays(days); medians = s.vix_medians(days)
    catalogue = s.signal_catalogue(); combos = [(k, f, x) for k in range(len(catalogue)) for f in (False, True) for x in s.STOPS]
    wf, _ = s.walk_forward(days, arrays, medians, combos)
    trades = [t for b in wf for t in b["test"]]
    by_date = {d.date: d for d in days}
    fees = 80.0                                   # ~ round-trip fees for one lot at these premiums
    n = len(trades)
    went_green = [t for t in trades if t["best_rupees"] > fees]
    green_then_loss = [t for t in went_green if t["net"] < 0]
    winners = [t for t in trades if t["net"] > 0]; losers = [t for t in trades if t["net"] <= 0]
    print(f"Out-of-sample trades: {n}")
    print(f"\n-- Exits: did we cut winners? --")
    print(f"Trades that were ever ahead by more than the fees (Rs {fees:.0f}) at the bid: {len(went_green)} ({len(went_green)/n:.0%})")
    print(f"   ...of which ended as a net loss: {len(green_then_loss)} ({len(green_then_loss)/max(1,len(went_green)):.0%})")
    print(f"Median best-ever gain per trade: Rs {statistics.median(t['best_rupees'] for t in trades):,.0f}; median final net: Rs {statistics.median(t['net'] for t in trades):,.0f}")
    print(f"Winners: median best Rs {statistics.median(t['best_rupees'] for t in winners):,.0f}, kept (net) Rs {statistics.median(t['net'] for t in winners):,.0f}")
    print(f"Losers:  median best Rs {statistics.median(t['best_rupees'] for t in losers):,.0f}, final net Rs {statistics.median(t['net'] for t in losers):,.0f}; median worst Rs {statistics.median(t['worst_rupees'] for t in losers):,.0f}")
    perfect = sum(max(t["best_rupees"] - fees, -fees) for t in trades) / n
    print(f"Hindsight ceiling (sell every trade at its best moment): Rs {perfect:,.0f} per trade vs actual Rs {sum(t['net'] for t in trades)/n:,.0f}")
    print(f"\n-- Entries: did the index keep going our way after the signal? (index points, signed in the trade direction) --")
    for horizon in (1, 5, 15, 30, 60):
        moves = []
        for t in trades:
            d = by_date[t["date"]]; i = t["signal_bar"]
            if i + horizon < len(d.ic): moves.append(t["dir"] * (d.ic[i + horizon] - d.ic[i]))
        m = statistics.mean(moves); se = statistics.stdev(moves) / len(moves) ** 0.5
        print(f"   {horizon:>2} min after the signal: mean {m:+6.1f} pts (+/- {1.96*se:4.1f}), moved our way {sum(1 for x in moves if x > 0)/len(moves):.0%} of the time")
    before = []
    for t in trades:
        d = by_date[t["date"]]; i = t["signal_bar"]
        if i >= 30: before.append(t["dir"] * (d.ic[i] - d.ic[i - 30]))
    print(f"   30 min BEFORE the signal (the move we reacted to): mean {statistics.mean(before):+6.1f} pts")
    print(f"\n-- Exit reasons --")
    for reason in ("STOP", "SESSION"):
        part = [t for t in trades if t["reason"] == reason]
        if part: print(f"   {reason}: {len(part)} trades, mean net Rs {statistics.mean(t['net'] for t in part):,.0f}, median minutes {statistics.median(t['minutes'] for t in part):.0f}")


if __name__ == "__main__":
    main(sys.argv)
