#!/usr/bin/env python3
"""Records what an AOW link gateway sends out, as a monitor client (role 1): every FRAME both ways,
with the gateway's bus time, to a file, until stopped (SIGTERM) or the link goes. For comparing
with a capture on the bus (sniff.py's frames.log) when something arrives twice or not at all.

  aowlink_monitor.py HOST [PORT] [FILE]       FILE default monitor.log

Each line: "<arrival s, this host's clock> <dir> <bus time s> <id hex> <data hex>", dir "bus" for
a frame from the bus (bus time the gateway's kernel receive time, wrapping at 2^32 us), "tcp" for
the control client's frame to the bus (no bus time: 0).
"""
import signal, socket, struct, sys, time

HOST = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 2323
OUT = sys.argv[3] if len(sys.argv) > 3 else "monitor.log"
HELLO, FRAME, HEARTBEAT, STATUS, CLOSE = 1, 2, 3, 4, 5
stop = []
signal.signal(signal.SIGTERM, lambda *a: stop.append(1))
signal.signal(signal.SIGINT, lambda *a: stop.append(1))


def msg(kind, body):
    return bytes((kind, len(body))) + body


name = b"aowlink_monitor"
s = socket.create_connection((HOST, PORT), timeout=2)
s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
s.sendall(msg(HELLO, b"AOWL" + bytes((1, 1, len(name))) + name))
s.settimeout(0.05)
buf, beat, frames = b"", 0.0, 0
with open(OUT, "w") as log:
    while not stop:
        now = time.time()
        if now >= beat:
            beat = now + 0.1
            s.sendall(msg(HEARTBEAT, struct.pack("<IIH", max(1, int(time.monotonic() * 1000) & 0xFFFFFFFF), 0, 0)))
        try:
            chunk = s.recv(65536)
        except socket.timeout:
            continue
        if not chunk:
            break
        buf += chunk
        arrival = time.time()
        while len(buf) >= 2 and len(buf) >= 2 + buf[1]:
            kind, body = buf[0], buf[2:2 + buf[1]]
            buf = buf[2 + buf[1]:]
            if kind == FRAME and len(body) >= 10:
                _, flags, cid, ts = struct.unpack("<BBII", body[:10])
                log.write(f"{arrival:.6f} {'tcp' if ts == 0 else 'bus'} {ts / 1e6:.6f} {cid:08X} {body[10:].hex()}\n")
                frames += 1
            elif kind == CLOSE:
                print(f"closed by the gateway: {body[:1].hex()}", file=sys.stderr)
                stop.append(1)
                break
try:
    s.sendall(msg(CLOSE, b"\x05\x00"))
except OSError:
    pass
print(f"{frames} frames to {OUT}")
