#!/usr/bin/env python3
"""Writes InputTestPool.iop: one data mask with every kind of input field, for the self-test's
input run. Encoded by hand from ISO 11783-6 Annex B (VT version 3 objects). MIT.

  python3 dotnet/InputTestPool.py   # writes dotnet/InputTestPool.iop
"""
import os
import struct

WHITE, BLACK = 1, 0
NONE = 0xFFFF
FONT, FONT_SMALL = 23000, 23001
objects = []


def obj(oid, kind, body):
    objects.append(struct.pack("<HB", oid, kind) + body)


def children(items):
    return b"".join(struct.pack("<Hhh", oid, x, y) for oid, x, y in items)


def font(oid, size):  # size code 3: 12 x 16, 1: 8 x 8
    obj(oid, 23, struct.pack("<BBBBB", BLACK, size, 0, 0, 0))


def out_string(oid, w, h, text):
    value = text.encode("latin-1")
    obj(oid, 11, struct.pack("<HHBHBHBH", w, h, WHITE, FONT, 0, NONE, 0, len(value)) + value + b"\0")


def in_number(oid, w, value, lo, hi, scale, decimals, variable=NONE, enabled=True):
    obj(oid, 9, struct.pack("<HHBHBHIIIifBBBBB", w, 20, WHITE, FONT, 0, variable, value, lo, hi, 0, scale,
                            decimals, 0, 0, 1 if enabled else 0, 0))


def in_string(oid, w, text, attributes=NONE, variable=NONE):
    value = text.encode("latin-1")
    obj(oid, 8, struct.pack("<HHBHHBHBB", w, 20, WHITE, FONT, attributes, 0, variable, 0, len(value)) + value + b"\1\0")


# The working set, its mask and the fields' labels.
obj(0, 0, struct.pack("<BBHBBB", WHITE, 1, 1000, 1, 0, 0) + children([(11000, 0, 0)]))
fields = [  # (field, label, y)
    (9000, "Rate", 40), (9001, "Count", 80), (9002, "Locked", 120), (8000, "Name", 160),
    (8001, "Note", 200), (7000, "Enable", 240), (10000, "Crop", 280),
]
mask = [(11000, 10, 4)]
for i, (field, label, y) in enumerate(fields):
    mask += [(11010 + i, 10, y), (field, 150, y)]
obj(1000, 1, struct.pack("<BHBB", WHITE, NONE, len(mask), 0) + children(mask))
font(FONT, 3)
font(FONT_SMALL, 1)
out_string(11000, 300, 20, "Input test")
for i, (_, label, _) in enumerate(fields):
    out_string(11010 + i, 130, 20, label)

# Rate: own value 50 (shown 25.0), 0..100, scale 0.5, one decimal. Count: in a number variable.
# Locked: disabled. Name: capitals and spaces only. Note: in a string variable.
in_number(9000, 120, 50, 0, 100, 0.5, 1)
in_number(9001, 120, 0, 0, 1000, 1.0, 0, variable=21000)
obj(21000, 21, struct.pack("<I", 7))
in_number(9002, 120, 3, 0, 10, 1.0, 0, enabled=False)
in_string(8000, 200, "HELLO     ", attributes=26000)
valid = b"ABCDEFGHIJKLMNOPQRSTUVWXYZ "
obj(26000, 26, struct.pack("<BB", 0, len(valid)) + valid + b"\0")
in_string(8001, 200, "        ", variable=22000)
note = b"abc     "
obj(22000, 22, struct.pack("<H", len(note)) + note)
obj(7000, 7, struct.pack("<BHHHBBB", WHITE, 20, FONT, NONE, 0, 1, 0))
crops = [11020, 11021, 11022]
obj(10000, 10, struct.pack("<HHHBBBB", 200, 20, NONE, 0, len(crops), 1, 0) + b"".join(struct.pack("<H", c) for c in crops))
for oid, text in zip(crops, ["Wheat", "Barley", "Canola"]):
    out_string(oid, 200, 20, text)

path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "InputTestPool.iop")
with open(path, "wb") as f:
    f.write(b"".join(objects))
print(f"{path}: {len(objects)} objects, {sum(map(len, objects))} bytes")
