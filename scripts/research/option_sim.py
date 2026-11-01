"""Modelled long-option trades from index and India VIX minute bars (docs/25-RESEARCH-PROTOCOL.md, "Intraday long-option search").

Black-76 with zero rate, forward = index, volatility = scale x India VIX, ATM strike, spread/slippage/fees as pre-registered.
Python standard library only. The model is not a market price; see the protocol for its stated limits.
"""
import math

UNITS = 30
TICK = 0.05
HALF_SPREAD = 0.00135          # of the model price; recorded median spread was 0.27%
FEES = {"brokerage": 20.0, "exchange": 0.0003553, "regulatory": 1e-6, "stt_sell": 0.0015, "stamp_buy": 0.00003, "gst": 0.18}
MINUTES_PER_YEAR = 365 * 24 * 60


def ncdf(x): return 0.5 * (1 + math.erf(x / 1.4142135623730951))


def black76(F, K, T, sigma, call):
    if T <= 0 or sigma <= 0: return max(0.0, (F - K) if call else (K - F))
    s = sigma * math.sqrt(T); d1 = (math.log(F / K) + 0.5 * s * s) / s; d2 = d1 - s
    return F * ncdf(d1) - K * ncdf(d2) if call else K * ncdf(-d2) - F * ncdf(-d1)


def fee(price, units, buy):
    turnover = price * units
    brokerage = FEES["brokerage"]; exchange = round(turnover * FEES["exchange"], 2); regulatory = round(turnover * FEES["regulatory"], 2)
    tax = 0.0 if buy else float(round(turnover * FEES["stt_sell"])); stamp = float(round(turnover * FEES["stamp_buy"])) if buy else 0.0
    gst = round((brokerage + exchange + regulatory) * FEES["gst"], 2)
    return brokerage + exchange + regulatory + tax + stamp + gst


class Params:
    def __init__(self, vol_scale=1.0, expiry_days=14.0, spread_mult=1.0, stop=0.08, target_rupees=None, max_minutes=None):
        self.vol_scale, self.expiry_days, self.spread_mult, self.stop = vol_scale, expiry_days, spread_mult, stop
        # Optional exits used only by diagnostics: take profit at a rupee gain per lot, or leave after a fixed number of minutes.
        self.target_rupees, self.max_minutes = target_rupees, max_minutes


def quote(F, K, minutes_since_entry, vix, call, p):
    """-> (bid, ask) of the modelled option."""
    # Trading-time decay: variance accrues only during the 375 trading minutes of a session, so the option's remaining life is measured in trading days
    # (calendar days x 252/365 at entry, falling by minutes/375). Calendar-minute decay under-states intraday decay and flatters a long option.
    T = (p.expiry_days * 252 / 365 - minutes_since_entry / 375.0) / 252
    mid = black76(F, K, max(T, 1e-6), p.vol_scale * vix / 100, call)
    half = mid * HALF_SPREAD * p.spread_mult
    slip = TICK * p.spread_mult
    return max(TICK, mid - half - slip), mid + half + slip


class Day:
    """Aligned minute arrays for one session. m = minute of day at bar start."""
    __slots__ = ("date", "m", "io", "ih", "il", "ic", "vo", "vc")

    def __init__(self, date, m, io, ih, il, ic, vo, vc):
        self.date, self.m, self.io, self.ih, self.il, self.ic, self.vo, self.vc = date, m, io, ih, il, ic, vo, vc


def trade(day, i, direction, p):
    """Signal evaluated at the close of bar i. Enter at the open of bar i+1; trail the bid; fill exits at the next open. -> dict or None."""
    n = len(day.m)
    if i + 2 >= n: return None
    call = direction > 0
    strike = round(day.ic[i] / 100.0) * 100.0
    t0 = day.m[i + 1]
    bid0, ask = quote(day.io[i + 1], strike, 0, day.vo[i + 1], call, p)
    peak = ask; trough = ask; best_bid = 0.0; exit_at = None; reason = "SESSION"
    last_entry_bar = i + 1
    for j in range(i + 1, n - 1):
        minutes = day.m[j] + 1 - t0
        bid, _ = quote(day.ic[j], strike, minutes, day.vc[j], call, p)
        if bid > peak: peak = bid
        if bid > best_bid: best_bid = bid
        if bid < trough: trough = bid
        if bid <= peak * (1 - p.stop): exit_at = j + 1; reason = "STOP"; break
        if p.target_rupees is not None and (bid - ask) * UNITS >= p.target_rupees: exit_at = j + 1; reason = "TARGET"; break
        if p.max_minutes is not None and minutes >= p.max_minutes: exit_at = j + 1; reason = "TIME"; break
        if day.m[j + 1] >= 15 * 60 + 15: exit_at = j + 1; reason = "SESSION"; break
    if exit_at is None: exit_at = n - 1
    minutes = day.m[exit_at] - t0
    exit_bid, _ = quote(day.io[exit_at], strike, minutes, day.vo[exit_at], call, p)
    gross = (exit_bid - ask) * UNITS
    net = gross - fee(ask, UNITS, True) - fee(max(TICK, exit_bid), UNITS, False)
    return {"date": day.date, "entry_bar": i + 1, "exit_bar": exit_at, "dir": direction, "entry": ask, "exit": exit_bid, "net": net,
            "gross": gross, "minutes": minutes, "reason": reason, "signal_bar": i,
            # best and worst the position ever was, if sold at the bid (diagnostics only; decisions never use them)
            "best_rupees": (best_bid - ask) * UNITS, "worst_rupees": (trough - ask) * UNITS}
