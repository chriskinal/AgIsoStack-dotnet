#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 The SharpIsoTerm contributors

"""Listen on hatcan2 and hatcan3 for SECS seconds: frames per source address on each, the first
time each source appears, and how many frames the kernel dropped on our sockets. Every frame goes
to frames.log as "iface kernel_ts_s id data" (hatcan3 is the wire as the implement side sees it)."""
import collections, select, signal, socket, struct, sys, time
SO_RXQ_OVFL = 40
SO_TIMESTAMP = 29
secs = float(sys.argv[1]) if len(sys.argv) > 1 else 20
socks = {}
for name in ("hatcan2", "hatcan3"):
    s = socket.socket(socket.AF_CAN, socket.SOCK_RAW, socket.CAN_RAW)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 << 20)
    s.setsockopt(socket.SOL_SOCKET, SO_TIMESTAMP, 1)
    s.setsockopt(socket.SOL_SOCKET, SO_RXQ_OVFL, 1)
    s.bind((name,)); socks[s] = name
seen = {n: collections.Counter() for n in socks.values()}
first = {n: {} for n in socks.values()}
dropped = {n: 0 for n in socks.values()}
stop = []
signal.signal(signal.SIGTERM, lambda *a: stop.append(1))
signal.signal(signal.SIGINT, lambda *a: stop.append(1))
log = open("frames.log", "w")
t0 = time.time()
while time.time() - t0 < secs and not stop:
    try:
        r, _, _ = select.select(list(socks), [], [], 0.5)
    except InterruptedError:
        continue
    for s in r:
        raw, anc, _, _ = s.recvmsg(16, 256)
        n = socks[s]
        ts = 0.0
        for lvl, typ, data in anc:
            if lvl == socket.SOL_SOCKET and typ == SO_TIMESTAMP:
                sec, usec = struct.unpack("qq", data[:16]); ts = sec + usec / 1e6
            elif lvl == socket.SOL_SOCKET and typ == SO_RXQ_OVFL:
                dropped[n] = struct.unpack("I", data[:4])[0]
        cid, ln, payload = struct.unpack("=IB3x8s", raw)
        cid &= 0x1FFFFFFF
        log.write(f"{n} {ts:.6f} {cid:08X} {payload[:ln].hex()}\n")
        src = f"{cid & 0xFF:02X}"
        seen[n][src] += 1
        first[n].setdefault(src, round(time.time() - t0, 2))
log.close()
for n in seen:
    print(n, {k: (v, first[n][k]) for k, v in sorted(seen[n].items())}, "kernel drops", dropped[n])
