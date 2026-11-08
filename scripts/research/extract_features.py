"""Re-decode a recorded market day into a flat research table, including the fields the engine does not use.

Usage: python -I scripts/research/extract_features.py <market-YYYY-MM-DD.sqlite> <out.sqlite>

Input is a raw recording (open it read-only; archived .br days must be decompressed first). Output table `snapshots`, one row per
instrument per received message, Kite full/quote/index packet layouts per https://kite.trade/docs/connect/v3/websocket/ :
184 bytes (full), 44 (quote), 32/28 (index), 8 (ltp). Prices are divided by 100. Python standard library only.
"""
import json, sqlite3, struct, sys, datetime as dt


def packets(message):
    if len(message) < 2:
        return
    count = struct.unpack(">H", message[:2])[0]; offset = 2
    for _ in range(count):
        length = struct.unpack(">H", message[offset:offset + 2])[0]; offset += 2
        yield message[offset:offset + length]; offset += length


def u32(p, o): return struct.unpack(">I", p[o:o + 4])[0]


def decode(p):
    n = len(p)
    row = {"token": u32(p, 0), "ltp": u32(p, 4) / 100}
    if n in (44, 184):
        row.update(last_qty=u32(p, 8), atp=u32(p, 12) / 100, volume=u32(p, 16), buy_qty=u32(p, 20), sell_qty=u32(p, 24),
                   open=u32(p, 28) / 100, high=u32(p, 32) / 100, low=u32(p, 36) / 100, prev_close=u32(p, 40) / 100)
    if n == 184:
        row.update(last_trade_ts=u32(p, 44), oi=u32(p, 48), oi_high=u32(p, 52), oi_low=u32(p, 56), exchange_ts=u32(p, 60))
        for side, base in (("bid", 64), ("ask", 124)):
            for i in range(5):
                q, price = u32(p, base + i * 12), u32(p, base + i * 12 + 4) / 100
                row[f"{side}{i + 1}_price"] = price; row[f"{side}{i + 1}_qty"] = q
    if n in (28, 32):
        row.update(high=u32(p, 8) / 100, low=u32(p, 12) / 100, open=u32(p, 16) / 100, prev_close=u32(p, 20) / 100)
        if n == 32: row["exchange_ts"] = u32(p, 28)
    return row


COLUMNS = ["received_at", "symbol", "role", "token", "ltp", "atp", "volume", "last_qty", "buy_qty", "sell_qty", "open", "high", "low", "prev_close",
           "oi", "oi_high", "oi_low", "last_trade_ts", "exchange_ts"] + [f"{s}{i}_{k}" for s in ("bid", "ask") for i in range(1, 6) for k in ("price", "qty")]


def main(source, target):
    reader = sqlite3.connect(f"file:{source}?mode=ro&immutable=1", uri=True)
    manifest = json.loads(reader.execute("select manifest from quote_runs").fetchone()[0])
    meta = {i["instrumentToken"]: (i["id"], i["role"]) for i in manifest["instruments"]}
    out = sqlite3.connect(target)
    out.execute("drop table if exists snapshots")
    out.execute("create table snapshots(" + ",".join(f"{c} {'TEXT' if c in ('received_at', 'symbol', 'role') else 'REAL'}" for c in COLUMNS) + ")")
    batch = []; total = 0
    for received, payload in reader.execute("select received_at, payload from market_events where kind='Binary' order by sequence"):
        for p in packets(payload):
            row = decode(p); symbol, role = meta.get(row["token"], (None, None))
            if symbol is None: continue
            row.update(received_at=received, symbol=symbol, role=str(role))
            batch.append(tuple(row.get(c) for c in COLUMNS))
        if len(batch) > 50000:
            out.executemany(f"insert into snapshots values({','.join('?' * len(COLUMNS))})", batch); total += len(batch); batch = []
    out.executemany(f"insert into snapshots values({','.join('?' * len(COLUMNS))})", batch); total += len(batch)
    out.execute("create index ix_symbol_time on snapshots(symbol, received_at)"); out.commit()
    print(f"{source}: wrote {total} rows to {target}")


if __name__ == "__main__":
    if len(sys.argv) != 3: sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
