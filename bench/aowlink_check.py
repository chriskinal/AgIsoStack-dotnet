#!/usr/bin/env python3
"""Checks a gateway against the AOW link protocol, version 1 (docs/TRANSPORT.md section 5): any
gateway, ours or another's. Needs the gateway to have no client connected, and some traffic on
its bus helps (the monitor check waits for a frame).

  aowlink_check.py HOST [PORT]

Each check says ok or FAIL; the exit status is the number of failures.
"""
import socket, struct, sys, time

HOST = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 2323
HELLO, FRAME, HEARTBEAT, STATUS, CLOSE = 1, 2, 3, 4, 5
failures = 0


def check(what, ok, detail=""):
    global failures
    failures += 0 if ok else 1
    print(f"  {'ok  ' if ok else 'FAIL'} {what}{(': ' + detail) if detail else ''}", flush=True)


def msg(kind, body):
    return bytes((kind, len(body))) + body


def hello(version=1, role=0, name="aowlink_check"):
    n = name.encode()
    return msg(HELLO, b"AOWL" + bytes((version, role, len(n))) + n)


def heartbeat():
    return msg(HEARTBEAT, struct.pack("<IIH", max(1, int(time.monotonic() * 1000) & 0xFFFFFFFF), 0, 0))


class Conn:
    def __init__(self):
        self.s = socket.create_connection((HOST, PORT), timeout=2)
        self.s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.buf = b""

    def send(self, data):
        self.s.sendall(data)

    def next(self, timeout=2.0):
        """The next message (type, body), None on timeout, or "closed"."""
        end = time.monotonic() + timeout
        while True:
            if len(self.buf) >= 2 and len(self.buf) >= 2 + self.buf[1]:
                kind, body = self.buf[0], self.buf[2:2 + self.buf[1]]
                self.buf = self.buf[2 + self.buf[1]:]
                return kind, body
            left = end - time.monotonic()
            if left <= 0:
                return None
            self.s.settimeout(left)
            try:
                chunk = self.s.recv(65536)
            except socket.timeout:
                return None
            except OSError:
                return "closed"
            if not chunk:
                return "closed"
            self.buf += chunk

    def until(self, kind, timeout=2.0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            m = self.next(end - time.monotonic())
            if m is None or m == "closed":
                return m
            if m[0] == kind:
                return m
        return None

    def close(self):
        self.s.close()


def parse_hello(b):
    """(version, role, name, [(bitrate, flags, name)]) from a gateway's HELLO body, or None."""
    if b[:4] != b"AOWL":
        return None
    def text(at):
        return b[at + 1:at + 1 + b[at]].decode(errors="replace"), at + 1 + b[at]
    name, at = text(6)
    channels = []
    count, at = b[at], at + 1
    for _ in range(count):
        rate, flags = struct.unpack("<IB", b[at:at + 5])
        cname, at = text(at + 5)
        channels.append((rate, flags, cname))
    return b[4], b[5], name, channels


print(f"AOW link check: {HOST}:{PORT}")

# 1. Hello, status, heartbeats.
c = Conn()
c.send(hello())
m = c.next()
ok = m not in (None, "closed") and m[0] == HELLO
check("answers HELLO with HELLO", ok, "" if ok else f"got {m}")
if ok:
    h = parse_hello(m[1])
    check("magic AOWL, version 1, role 0", h is not None and h[0] == 1 and h[1] == 0, str(h[:2]) if h else "bad magic")
    if h:
        print(f"       gateway \"{h[2]}\", channels {h[3]}")
    st = c.until(STATUS)
    check("STATUS after HELLO", st not in (None, "closed"))
    if st not in (None, "closed"):
        check("STATUS body is 12 bytes", len(st[1]) >= 12, f"{len(st[1])}")
    t0 = time.monotonic()
    beats = 0
    while time.monotonic() - t0 < 1.0:
        c.send(heartbeat())
        m = c.next(0.1)
        if m not in (None, "closed") and m[0] == HEARTBEAT:
            beats += 1
    check("HEARTBEAT about every 100 ms", 7 <= beats <= 13, f"{beats} in 1 s")

    # 2. A second control client is refused; a monitor is taken or refused as busy. The first
    # client is kept alive meanwhile.
    c.send(heartbeat())
    d = Conn()
    d.send(hello())
    m = d.until(CLOSE)
    check("a second control client gets CLOSE busy (2)", m not in (None, "closed") and m[1][:1] == b"\x02", f"{m}")
    d.close()
    c.send(heartbeat())
    mon = Conn()
    mon.send(hello(role=1, name="aowlink_check monitor"))
    m = mon.next()
    if m not in (None, "closed") and m[0] == HELLO:
        print("       monitors are accepted")
        mon.send(heartbeat())
        check("a monitor gets STATUS", mon.until(STATUS) not in (None, "closed"))
        c.send(heartbeat())
        mon.send(msg(FRAME, struct.pack("<BBII", 0, 1, 0x18EAFFFE, 0) + b"\x00\xee\x00"))
        m = mon.until(CLOSE)
        check("a FRAME from a monitor is a protocol error (4)", m not in (None, "closed") and m[1][:1] == b"\x04", f"{m}")
    else:
        check("a refused monitor gets CLOSE busy (2)", m not in (None, "closed") and m[0] == CLOSE and m[1][:1] == b"\x02", f"{m}")
    mon.close()

    # 3. Silence: the gateway closes within about 1 s of the last thing it heard.
    c.send(heartbeat())
    t0 = time.monotonic()
    m = None
    while time.monotonic() - t0 < 3:
        m = c.next(0.2)
        if m == "closed":
            break
    took = time.monotonic() - t0
    check("closes a silent client after about 1 s", m == "closed" and 0.9 <= took <= 1.5, f"{took:.2f} s")
c.close()

# 4. A control client can connect at once after the last one went.
c = Conn()
c.send(hello())
m = c.next()
check("takes a new control client at once", m not in (None, "closed") and m[0] == HELLO)
c.send(msg(CLOSE, b"\x05" + b"\x00"))
c.close()
time.sleep(0.2)

# 5. Wrong version, garbage, no hello.
c = Conn()
c.send(hello(version=9))
m = c.until(CLOSE)
check("version 9 gets CLOSE version (1)", m not in (None, "closed") and m[1][:1] == b"\x01", f"{m}")
c.close()
c = Conn()
c.send(b"\x07\x02hi")
m = c.next()
check("a message before HELLO gets CLOSE protocol error (4) or a close",
      m == "closed" or (m is not None and m[0] == CLOSE and m[1][:1] == b"\x04"), f"{m}")
c.close()
c = Conn()
t0 = time.monotonic()
m = c.next(4)
took = time.monotonic() - t0
check("closes a client that sends no HELLO, in about 2 s", m == "closed" or (m is not None and m[0] == CLOSE), f"{took:.2f} s")
c.close()

# 6. A newer message type is skipped.
c = Conn()
c.send(hello() + msg(0x40, b"future") + heartbeat())
m = c.next()
check("skips an unknown message type", m not in (None, "closed") and m[0] == HELLO and c.until(HEARTBEAT) not in (None, "closed"))
c.send(msg(CLOSE, b"\x05\x00"))
c.close()

print("PASS" if failures == 0 else f"FAIL: {failures}")
sys.exit(failures)
