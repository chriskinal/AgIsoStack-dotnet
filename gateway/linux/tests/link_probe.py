#!/usr/bin/env python3
"""The AOW link gateway on virtual CAN: starts aow-linkd on vcan1 and checks the parts that need a
real interface: frames both ways (ids, flags, data, kernel timestamps), the bus drained when a
control client connects, STATUS following the interface down and up, an interface that is
missing at start found when it appears, and its --status reports (JSON, here to 127.0.0.1:18891).
The protocol itself is checked by bench/aowlink_check.py: give its path as a second argument to
run it here too. MIT.

    sudo modprobe vcan; sudo ip link add vcan1 type vcan; sudo ip link set vcan1 up
    sudo python3 gateway/linux/tests/link_probe.py build/aow-linkd [bench/aowlink_check.py]

Needs root for "ip link" (the down and up, and the missing interface). Uses port 12323.
"""
import json
import socket
import struct
import subprocess
import sys
import time

PORT = 12323
STATUS_PORT = 18891
CAN_EFF = 0x80000000
HELLO, FRAME, HEARTBEAT, STATUS, CLOSE = 1, 2, 3, 4, 5
failures = 0


def check(what, ok, detail=""):
    global failures
    failures += 0 if ok else 1
    print(f"  {'ok  ' if ok else 'FAIL'} {what}{(': ' + str(detail)) if detail else ''}", flush=True)


def msg(kind, body):
    return bytes((kind, len(body))) + body


def heartbeat():
    return msg(HEARTBEAT, struct.pack("<IIH", max(1, int(time.monotonic() * 1000) & 0xFFFFFFFF), 0, 0))


class Client:
    def __init__(self, name="link_probe"):
        self.s = socket.create_connection(("127.0.0.1", PORT), timeout=2)
        self.s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.buf = b""
        n = name.encode()
        self.s.sendall(msg(HELLO, b"AOWL" + bytes((1, 0, len(n))) + n))

    def messages(self, seconds):
        """Everything received within the time, keeping the link alive meanwhile."""
        out, end, beat = [], time.monotonic() + seconds, 0.0
        while time.monotonic() < end:
            if time.monotonic() >= beat:
                beat = time.monotonic() + 0.1
                self.s.sendall(heartbeat())
            self.s.settimeout(0.02)
            try:
                chunk = self.s.recv(65536)
            except socket.timeout:
                chunk = b""
            self.buf += chunk
            while len(self.buf) >= 2 and len(self.buf) >= 2 + self.buf[1]:
                out.append((self.buf[0], self.buf[2:2 + self.buf[1]]))
                self.buf = self.buf[2 + self.buf[1]:]
        return out

    def send_frame(self, can_id, data, extended=True):
        self.s.sendall(msg(FRAME, struct.pack("<BBII", 0, 1 if extended else 0, can_id, 0) + bytes(data)))

    def close(self):
        self.s.sendall(msg(CLOSE, b"\x05\x00"))
        self.s.close()


def frames(ms):
    return [struct.unpack("<BBII", b[:10]) + (b[10:],) for t, b in ms if t == FRAME]


def states(ms):
    return [b[1] for t, b in ms if t == STATUS]


def reports(sock, seconds):
    """The gateway's --status reports received within the time, flattened for the checks."""
    out, end = [], time.monotonic() + seconds
    while time.monotonic() < end:
        sock.settimeout(max(0.01, end - time.monotonic()))
        try:
            j = json.loads(sock.recv(4096))
        except socket.timeout:
            break
        c = j["client"] or {}
        out.append((time.monotonic(), {"gateway": j["gateway"], "connected": j["client"] is not None,
                                       "listen": j["listen"], "port": j["port"], "connections": j["connections"],
                                       "toClient": c.get("toClient", 0), "toBus": c.get("toBus", 0),
                                       "client": c.get("name", ""), "address": c.get("address", ""),
                                       "channels": len(j["channels"])}))
    return out


def ip(*args):
    subprocess.run(["ip", "link", *args], check=False, capture_output=True)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    daemon = sys.argv[1]
    bus = socket.socket(socket.AF_CAN, socket.SOCK_RAW, socket.CAN_RAW)
    bus.bind(("vcan1",))
    bus.settimeout(1)

    status = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    status.bind(("127.0.0.1", STATUS_PORT))
    ip("del", "vcan7")  # missing at start
    proc = subprocess.Popen([daemon, "--can", "vcan1,vcan7", "--port", str(PORT), "--status", f"127.0.0.1:{STATUS_PORT}"],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    time.sleep(0.3)
    try:
        rs = reports(status, 1.3)
        check("reports, once a second, no terminal", 1 <= len(rs) <= 2 and not rs[-1][1]["connected"]
              and rs[-1][1]["gateway"] == "aow-linkd" and rs[-1][1]["port"] == PORT and rs[-1][1]["channels"] == 2
              and rs[-1][1]["listen"] == "127.0.0.1", [r for _, r in rs])

        # Frames on the bus before anyone connects are not delivered later.
        bus.send(struct.pack("=IB3x8s", 0x18EF0001 | CAN_EFF, 1, b"\x01".ljust(8, b"\0")))
        time.sleep(0.1)
        t0 = time.monotonic()
        c = Client()
        rs = reports(status, 0.3)
        check("a report at once when the terminal connects",
              rs and rs[0][1]["connected"] and rs[0][1]["client"] == "link_probe" and rs[0][1]["address"] == "127.0.0.1"
              and rs[0][1]["connections"] == 1 and rs[0][0] - t0 < 0.2, [(round(t - t0, 3), r) for t, r in rs])
        ms = c.messages(0.3)
        check("HELLO lists two channels", ms and ms[0][0] == HELLO and b"vcan1" in ms[0][1] and b"vcan7" in ms[0][1])
        st = [(b[0], b[1]) for t, b in ms if t == STATUS]
        check("STATUS: vcan1 active, vcan7 down", (0, 0) in st and (1, 4) in st, st)
        check("the frame from before the link is not delivered", not frames(ms))

        # Bus to client: an extended and a standard frame, in order, with kernel times.
        bus.send(struct.pack("=IB3x8s", 0x18EF2680 | CAN_EFF, 8, bytes(range(8))))
        bus.send(struct.pack("=IB3x8s", 0x123, 2, b"\xAA\xBB".ljust(8, b"\0")))
        fs = frames(c.messages(0.3))
        check("two frames from the bus", len(fs) == 2, fs)
        if len(fs) == 2:
            check("extended: id, flag, data", fs[0][0] == 0 and fs[0][1] == 1 and fs[0][2] == 0x18EF2680 and fs[0][4] == bytes(range(8)))
            check("standard: id, flag, data", fs[1][1] == 0 and fs[1][2] == 0x123 and fs[1][4] == b"\xAA\xBB")
            check("kernel times in order", 0 < (fs[1][3] - fs[0][3]) % (1 << 32) < 100000, (fs[0][3], fs[1][3]))

        # Client to bus.
        c.send_frame(0x0CE6FF26, b"\x09\x08\x07")
        c.send_frame(0x7FF, b"", extended=False)
        got = []
        try:
            while len(got) < 2:
                cid, ln, data = struct.unpack("=IB3x8s", bus.recv(16))
                got.append((cid, data[:ln]))
        except socket.timeout:
            pass
        check("two frames to the bus", got == [(0x0CE6FF26 | CAN_EFF, b"\x09\x08\x07"), (0x7FF, b"")], got)
        c.messages(1.2)  # the link kept alive meanwhile; the reports wait in the socket
        rs = reports(status, 0.05)
        check("the report counts them", rs and rs[-1][1]["toClient"] == 2 and rs[-1][1]["toBus"] == 2, [r for _, r in rs])

        # The interface down and up: STATUS follows within about a second.
        ip("set", "vcan1", "down")
        check("vcan1 down: STATUS down", 4 in [s for s in states(c.messages(1.3))])
        ip("set", "vcan1", "up")
        ms = c.messages(1.3)
        check("vcan1 up: STATUS active again", [b[1] for t, b in ms if t == STATUS and b[0] == 0][-1:] == [0])

        # The missing interface appears: found within about a second.
        ip("add", "vcan7", "type", "vcan")
        ip("set", "vcan7", "up")
        ms = c.messages(1.5)
        check("vcan7 created: STATUS active", [b[1] for t, b in ms if t == STATUS and b[0] == 1][-1:] == [0])
        reports(status, 0.01)  # what came before
        c.close()
        rs = reports(status, 0.3)
        check("a report at once when the terminal goes", rs and not rs[0][1]["connected"] and rs[0][1]["client"] == "",
              [r for _, r in rs])
    finally:
        proc.terminate()
        out, _ = proc.communicate(timeout=3)
        ip("del", "vcan7")
    check("stops on SIGTERM", proc.returncode == 0, proc.returncode)
    print("".join("       " + line + "\n" for line in out.splitlines()), end="")

    if len(sys.argv) > 2:
        proc = subprocess.Popen([daemon, "--can", "vcan1", "--port", str(PORT)], stdout=subprocess.DEVNULL)
        time.sleep(0.3)
        rc = subprocess.run([sys.executable, sys.argv[2], "127.0.0.1", str(PORT)]).returncode
        proc.terminate()
        proc.wait(timeout=3)
        check("aowlink_check.py", rc == 0, f"{rc} failures")

    print("PASS" if failures == 0 else f"FAIL: {failures}")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
