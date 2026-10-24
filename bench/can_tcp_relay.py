#!/usr/bin/env python3
"""Bench relay: one SocketCAN interface <-> one TCP client (the tablet terminal spike).

Records, little-endian, both directions:
  0x01 FRAME  ts_us:u32 id:u32 (bit 31 = extended) len:u8 data:8   (18 bytes)
  0x02 PING   seq:u32 t:u64                                        (13 bytes; echoed as 0x03 PONG)
Every CAN frame waiting when the socket is read goes out in one TCP write; Nagle is off.
"""
import errno, os, select, socket, struct, sys, time

IFACE = sys.argv[1] if len(sys.argv) > 1 else "hatcan2"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 2323
CAN_FMT = "=IB3x8s"
EFF = socket.CAN_EFF_FLAG
SO_RXQ_OVFL = 40


def now_ms(t0):
    return (time.monotonic() - t0) * 1000


def log(msg):
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


def serve(conn, can, dump):
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    conn.setblocking(False)
    rx = b""
    t0 = time.monotonic()
    stats = {"can_to_tcp": 0, "tcp_to_can": 0, "pings": 0, "writes": 0, "can_tx_retry": 0, "can_rx_dropped": 0}
    last = time.monotonic()
    out = bytearray()
    while True:
        r, w, _ = select.select([can, conn], [conn] if out else [], [], 0.5)
        if can in r:
            while True:
                try:
                    raw, anc, _, _ = can.recvmsg(16, 64)
                except BlockingIOError:
                    break
                for lvl, typ, d in anc:
                    if lvl == socket.SOL_SOCKET and typ == SO_RXQ_OVFL:
                        stats["can_rx_dropped"] = struct.unpack("I", d[:4])[0]
                cid, ln, data = struct.unpack(CAN_FMT, raw)
                ext = bool(cid & EFF)
                wire = (cid & 0x1FFFFFFF) | (0x80000000 if ext else 0)
                ts = int((time.monotonic() - t0) * 1e6) & 0xFFFFFFFF
                out += struct.pack("<BIIB8s", 1, ts, wire, ln, data)
                dump.write(f"{now_ms(t0):.1f} bus {cid & 0x1FFFFFFF:08X} {data[:ln].hex()}\n")
                stats["can_to_tcp"] += 1
        if conn in r:
            try:
                chunk = conn.recv(65536)
            except BlockingIOError:
                chunk = None
            if chunk == b"":
                return stats
            if chunk:
                rx += chunk
                while rx:
                    kind = rx[0]
                    if kind == 1 and len(rx) >= 18:
                        _, _, wire, ln, data = struct.unpack("<BIIB8s", rx[:18])
                        rx = rx[18:]
                        cid = (wire & 0x1FFFFFFF) | (EFF if wire & 0x80000000 else 0)
                        frame = struct.pack(CAN_FMT, cid, ln, data)
                        dump.write(f"{now_ms(t0):.1f} tcp {cid & 0x1FFFFFFF:08X} {data[:ln].hex()}\n")
                        while True:
                            try:
                                can.send(frame)
                                break
                            except OSError as e:
                                if e.errno not in (errno.ENOBUFS, errno.EAGAIN):
                                    raise
                                stats["can_tx_retry"] += 1
                                time.sleep(0.0005)
                        stats["tcp_to_can"] += 1
                    elif kind == 2 and len(rx) >= 13:
                        out += b"\x03" + rx[1:13]
                        rx = rx[13:]
                        stats["pings"] += 1
                    elif kind in (1, 2):
                        break  # wait for the rest of the record
                    else:
                        log(f"bad record type {kind}, dropping connection")
                        return stats
        if out:
            try:
                n = conn.send(out)
                del out[:n]
                stats["writes"] += 1
            except BlockingIOError:
                pass
        now = time.monotonic()
        if now - last >= 10:
            last = now
            log(f"{stats}")


def main():
    can = socket.socket(socket.AF_CAN, socket.SOCK_RAW, socket.CAN_RAW)
    can.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 << 20)
    can.setsockopt(socket.SOL_SOCKET, SO_RXQ_OVFL, 1)
    can.bind((IFACE,))
    can.setblocking(False)
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", PORT))
    srv.listen(1)
    log(f"relay {IFACE} <-> tcp {PORT}")
    while True:
        conn, addr = srv.accept()
        log(f"client {addr[0]}")
        while True:  # drop what queued on the bus while nobody was connected
            try:
                can.recv(16)
            except BlockingIOError:
                break
        try:
            with open("dump.log", "w") as dump:
                stats = serve(conn, can, dump)
        except OSError as e:
            stats = f"error {e}"
        conn.close()
        log(f"client gone: {stats}")


if __name__ == "__main__":
    main()
