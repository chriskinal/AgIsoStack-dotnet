#!/usr/bin/env python3
"""Walk every ETP batch (DPO, then its data packets) in frames.log, per interface and sender, and
report batches whose sequence numbers are not 1..count, plus every abort. Also checks the relay's
dump.log the same way (its "bus" lines: what the relay read from hatcan2)."""
import collections, sys

def frames_from_capture(path):
    for line in open(path):
        iface, ts, cid, data = line.split()
        yield iface, float(ts), int(cid, 16), bytes.fromhex(data)

def frames_from_dump(path):
    for line in open(path):
        p = line.split()
        if len(p) == 4 and p[1] == "bus":
            yield "relay", float(p[0]) / 1000, int(p[2], 16), bytes.fromhex(p[3])

def check(frames):
    batch = {}  # (iface, src, dst) -> [dpo_ts, count, offset, seqs]
    bad = []
    aborts = []
    n = collections.Counter()
    def close(key):
        b = batch.pop(key, None)
        if b and b[3] != list(range(1, b[1] + 1)):
            bad.append((key, b))
        if b:
            n[key[0]] += 1
    for iface, ts, cid, d in frames:
        pf = (cid >> 16) & 0xFF
        dst, src = (cid >> 8) & 0xFF, cid & 0xFF
        if pf == 0xC8 and d:
            if d[0] == 0x16:
                close((iface, src, dst))
                batch[(iface, src, dst)] = [ts, d[1], int.from_bytes(d[2:5], "little"), []]
            elif d[0] == 0xFF:
                aborts.append((iface, ts, src, dst, d[1]))
                close((iface, dst, src)); close((iface, src, dst))
        elif pf == 0xC7:
            b = batch.get((iface, src, dst))
            if b is not None:
                b[3].append(d[0])
                if len(b[3]) >= b[1]:
                    close((iface, src, dst))
    for key in list(batch):
        close(key)
    return n, bad, aborts

for label, frames in (("capture", frames_from_capture(sys.argv[1])), ("relay dump", frames_from_dump(sys.argv[2]))):
    n, bad, aborts = check(frames)
    print(f"--- {label}: batches checked {dict(n)}")
    for (iface, src, dst), (ts, count, off, seqs) in bad:
        print(f"  BAD {iface} {src:02X}->{dst:02X} at {ts:.4f}: DPO {count} from packet {off + 1}, got seqs {seqs}")
    for iface, ts, src, dst, why in aborts:
        print(f"  abort {iface} {src:02X}->{dst:02X} at {ts:.4f} reason {why}")
