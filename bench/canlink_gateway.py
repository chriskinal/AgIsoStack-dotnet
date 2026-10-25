#!/usr/bin/env python3
"""Reference gateway for the CAN link protocol, version 1 (docs/TRANSPORT.md section 5): SocketCAN
interfaces <-> TCP clients. One control client, any number of monitors.

  canlink_gateway.py IFACE[,IFACE...] [PORT]       channels are numbered in the order given

Knows nothing of ISOBUS. Every frame waiting when a CAN socket is read goes out in one TCP write;
Nagle is off. A client that has sent nothing for 1 s is closed with what it had not yet been
sent (SO_LINGER 0). Logs a line per client, and writes dump.log (every frame of the control
client's link, both ways) for bench/etpcheck.py.
"""
import errno, json, select, socket, struct, subprocess, sys, time

IFACES = (sys.argv[1] if len(sys.argv) > 1 else "hatcan2").split(",")
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 2323
NAME = f"{socket.gethostname()} {','.join(IFACES)}"
VERSION, MAGIC = 1, b"CANL"
HELLO, FRAME, HEARTBEAT, STATUS, CLOSE = 1, 2, 3, 4, 5
R_VERSION, R_BUSY, R_TIMEOUT, R_PROTOCOL, R_SHUTDOWN = 1, 2, 3, 4, 5
HEARTBEAT_S, SILENCE_S, HELLO_S, TX_LIMIT_S, STATUS_S = 0.1, 1.0, 2.0, 0.1, 1.0
CAN_FMT = "=IB3x8s"
EFF = socket.CAN_EFF_FLAG
SO_TIMESTAMP, SO_RXQ_OVFL = 29, 40  # Python has neither constant
STATES = {"ERROR-ACTIVE": 0, "ERROR-WARNING": 1, "ERROR-PASSIVE": 2, "BUS-OFF": 3}
T0 = time.monotonic()


def log(msg):
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


def msg(kind, body):
    return bytes((kind, len(body))) + body


def s8(text):
    b = text.encode()[:200]
    return bytes((len(b),)) + b


def now_ms():
    return max(1, int((time.monotonic() - T0) * 1000)) & 0xFFFFFFFF


class Channel:
    def __init__(self, index, iface):
        self.index, self.iface = index, iface
        self.sock = socket.socket(socket.AF_CAN, socket.SOCK_RAW, socket.CAN_RAW)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 << 20)
        self.sock.setsockopt(socket.SOL_SOCKET, SO_RXQ_OVFL, 1)
        self.sock.setsockopt(socket.SOL_SOCKET, SO_TIMESTAMP, 1)
        self.sock.bind((iface,))
        self.sock.setblocking(False)
        self.overflow = 0      # SO_RXQ_OVFL: frames the kernel dropped for this socket, since it opened
        self.tx = []           # (frame, deadline): in order, each tried until its deadline
        self.bitrate, self.state, self.txerr, self.rxerr = 0, 4, 0, 0
        self.refresh()

    def refresh(self):
        """Bitrate, state and error counters from the kernel (ip -details -json)."""
        try:
            out = subprocess.run(["ip", "-details", "-json", "link", "show", "dev", self.iface],
                                 capture_output=True, text=True, timeout=1).stdout
            link = json.loads(out)[0]
        except (OSError, ValueError, IndexError, subprocess.TimeoutExpired):
            self.state = 4
            return
        info = link.get("linkinfo", {}).get("info_data", {})
        up = "UP" in link.get("flags", [])
        self.bitrate = info.get("bittiming", {}).get("bitrate", 0)
        self.state = STATES.get(info.get("state"), 0) if up else 4  # vcan has no state: active when up
        self.txerr = info.get("berr_counter", {}).get("tx", 0)
        self.rxerr = info.get("berr_counter", {}).get("rx", 0)

    def drain(self):
        while True:
            try:
                self.sock.recv(16)
            except BlockingIOError:
                return

    def read(self):
        """Every frame waiting: (wire id, len, data, kernel time in us)."""
        frames = []
        while True:
            try:
                raw, anc, _, _ = self.sock.recvmsg(16, 128)
            except BlockingIOError:
                return frames
            ts = None
            for lvl, typ, d in anc:
                if lvl == socket.SOL_SOCKET and typ == SO_RXQ_OVFL:
                    self.overflow = struct.unpack("I", d[:4])[0]
                elif lvl == socket.SOL_SOCKET and typ == SO_TIMESTAMP:
                    sec, usec = struct.unpack("@ll", d[:struct.calcsize("@ll")])
                    ts = sec * 1000000 + usec
            if ts is None:
                ts = int(time.time() * 1e6)
            cid, ln, data = struct.unpack(CAN_FMT, raw)
            frames.append((cid, ln, data, ts & 0xFFFFFFFF))

    def flush(self, client):
        """Puts queued frames on the bus in order; drops (and counts) any past its deadline."""
        now = time.monotonic()
        while self.tx:
            frame, deadline = self.tx[0]
            if now > deadline:
                self.tx.pop(0)
                if client:
                    client.tx_dropped += 1
                continue
            try:
                self.sock.send(frame)
            except OSError as e:
                if e.errno in (errno.ENOBUFS, errno.EAGAIN):
                    if client:
                        client.stats["can_tx_retry"] += 1
                    return  # the controller's queue is full: try again next pass
                self.tx.pop(0)  # down or bus-off: this frame is lost
                if client:
                    client.tx_dropped += 1
                continue
            self.tx.pop(0)


class Client:
    def __init__(self, conn, addr):
        self.conn, self.addr = conn, addr[0]
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        conn.setblocking(False)
        self.rx, self.out = b"", bytearray()
        self.role = None  # None until its HELLO: 0 control, 1 monitor
        self.opened = self.heard = time.monotonic()
        self.next_heartbeat = 0.0
        self.heard_time, self.heard_at = 0, None
        self.tx_dropped, self.overflow_base = 0, {}
        self.stats = {"can_to_tcp": 0, "tcp_to_can": 0, "heartbeats": 0, "writes": 0, "can_tx_retry": 0}

    def send(self, data):
        self.out += data

    def heartbeat(self):
        held = 0 if self.heard_at is None else min(0xFFFF, int((time.monotonic() - self.heard_at) * 1000))
        self.send(msg(HEARTBEAT, struct.pack("<IIH", now_ms(), self.heard_time, held)))

    def status(self, ch):
        rx_dropped = (ch.overflow - self.overflow_base.get(ch.index, 0)) & 0xFFFFFFFF
        tx_dropped = self.tx_dropped if self.role == 0 else 0
        self.send(msg(STATUS, struct.pack("<BBBBII", ch.index, ch.state, min(ch.txerr, 255), min(ch.rxerr, 255),
                                           rx_dropped, tx_dropped & 0xFFFFFFFF)))

    def flush(self):
        if not self.out:
            return True
        try:
            n = self.conn.send(self.out)
            del self.out[:n]
            self.stats["writes"] += 1
        except BlockingIOError:
            pass
        except OSError:
            return False
        return True

    def close(self, reason=None, text=""):
        if reason is not None:
            try:
                self.conn.send(msg(CLOSE, bytes((reason,)) + s8(text)))
            except OSError:
                pass
        try:
            self.conn.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
        except OSError:
            pass
        self.conn.close()


def main():
    channels = [Channel(i, iface) for i, iface in enumerate(IFACES)]
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", PORT))
    srv.listen(4)
    srv.setblocking(False)
    log(f"gateway {','.join(IFACES)} <-> tcp {PORT}, CAN link version {VERSION}")
    clients = []
    control = None
    dump = None
    next_status = 0.0
    states = {}

    def drop(c, why, reason=None, text=""):
        nonlocal control, dump
        c.close(reason, text)
        clients.remove(c)
        if c is control:
            control = None
            for ch in channels:
                ch.tx.clear()
            if dump:
                dump.close()
                dump = None
        role = {0: "control", 1: "monitor"}.get(c.role, "no hello")
        log(f"client gone: {c.addr} {role}, {why}: {c.stats}")

    while True:
        want_write = [c.conn for c in clients if c.out]
        pending_tx = any(ch.tx for ch in channels)
        r, w, _ = select.select([srv] + [ch.sock for ch in channels] + [c.conn for c in clients],
                                want_write, [], 0.005 if pending_tx else 0.02)
        now = time.monotonic()

        if srv in r:
            try:
                conn, addr = srv.accept()
                clients.append(Client(conn, addr))
            except BlockingIOError:
                pass

        for ch in channels:
            if ch.sock not in r:
                continue
            frames = ch.read()
            live = [c for c in clients if c.role is not None]
            if not live:
                continue
            out = b"".join(msg(FRAME, struct.pack("<BBII", ch.index, 1 if cid & EFF else 0, cid & 0x1FFFFFFF, ts) + data[:ln])
                           for cid, ln, data, ts in frames)
            for c in live:
                c.send(out)
                c.stats["can_to_tcp"] += len(frames)
            if dump:
                for cid, ln, data, ts in frames:
                    dump.write(f"{(now - T0) * 1000:.1f} bus {cid & 0x1FFFFFFF:08X} {data[:ln].hex()}\n")

        for c in list(clients):
            if c.conn not in r:
                continue
            try:
                chunk = c.conn.recv(65536)
            except BlockingIOError:
                continue
            except OSError as e:
                drop(c, f"error {e}")
                continue
            if not chunk:
                drop(c, "closed by the client")
                continue
            c.heard = now
            c.rx += chunk
            while len(c.rx) >= 2 and len(c.rx) >= 2 + c.rx[1]:
                kind, body = c.rx[0], c.rx[2:2 + c.rx[1]]
                c.rx = c.rx[2 + c.rx[1]:]
                if c.role is None:
                    if kind != HELLO or len(body) < 7 or body[:4] != MAGIC:
                        drop(c, "no HELLO", R_PROTOCOL, "expected HELLO")
                        break
                    if body[4] != VERSION:
                        drop(c, f"version {body[4]}", R_VERSION, f"this gateway speaks version {VERSION}")
                        break
                    role = body[5]
                    name = body[7:7 + body[6]].decode(errors="replace")
                    if role == 0 and control is not None:
                        drop(c, "busy", R_BUSY, "another control client is connected")
                        break
                    if role not in (0, 1):
                        drop(c, f"role {role}", R_PROTOCOL, "unknown role")
                        break
                    c.role = role
                    if role == 0:
                        control = c
                        for ch in channels:
                            ch.drain()  # nothing older than the link
                            ch.tx.clear()
                        dump = open("dump.log", "w")
                    c.overflow_base = {ch.index: ch.overflow for ch in channels}
                    hello = MAGIC + bytes((VERSION, 0)) + s8(NAME) + bytes((len(channels),))
                    for ch in channels:
                        hello += struct.pack("<IB", ch.bitrate, 0) + s8(ch.iface)
                    c.send(msg(HELLO, hello))
                    for ch in channels:
                        c.status(ch)
                    log(f"client {c.addr} {'control' if role == 0 else 'monitor'} \"{name}\"")
                elif kind == FRAME:
                    if c.role != 0:
                        drop(c, "FRAME from a monitor", R_PROTOCOL, "a monitor sends no frames")
                        break
                    if len(body) < 10 or body[0] >= len(channels):
                        drop(c, "bad FRAME", R_PROTOCOL, "bad FRAME")
                        break
                    index, flags, cid = body[0], body[1], struct.unpack("<I", body[2:6])[0]
                    data = body[10:18]
                    cid = (cid & 0x1FFFFFFF) | (EFF if flags & 1 else 0)
                    channels[index].tx.append((struct.pack(CAN_FMT, cid, len(data), data.ljust(8, b"\0")), now + TX_LIMIT_S))
                    c.stats["tcp_to_can"] += 1
                    echo = msg(FRAME, body)
                    for m in clients:
                        if m.role == 1:
                            m.send(echo)
                    if dump:
                        dump.write(f"{(now - T0) * 1000:.1f} tcp {cid & 0x1FFFFFFF:08X} {data.hex()}\n")
                elif kind == HEARTBEAT and len(body) >= 10:
                    c.heard_time, c.heard_at = struct.unpack("<I", body[:4])[0], now
                    c.stats["heartbeats"] += 1
                elif kind == CLOSE:
                    drop(c, f"CLOSE {body[0] if body else 0}")
                    break
                elif kind == HELLO:
                    drop(c, "second HELLO", R_PROTOCOL, "a second HELLO")
                    break
                # anything else: a newer message, skipped

        for ch in channels:
            ch.flush(control)

        if now >= next_status:
            next_status = now + STATUS_S
            for ch in channels:
                ch.refresh()
            periodic = True
        else:
            periodic = False
        for c in list(clients):
            if c.role is None:
                if now - c.opened > HELLO_S:
                    drop(c, "no HELLO in 2 s", R_PROTOCOL, "no HELLO")
                continue
            if now - c.heard > SILENCE_S:
                drop(c, "silent for 1 s")
                continue
            if now >= c.next_heartbeat:
                c.next_heartbeat = now + HEARTBEAT_S
                c.heartbeat()
            for ch in channels:
                if periodic or states.get(ch.index) != ch.state:
                    c.status(ch)
        for ch in channels:
            states[ch.index] = ch.state

        for c in list(clients):
            if c.out and not c.flush():
                drop(c, "send failed")


if __name__ == "__main__":
    main()
