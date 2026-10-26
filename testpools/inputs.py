#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 The SharpIsoTerm contributors

"""inputs.iop: one data mask with every kind of input field, for the self-test's input run
(dotnet/IsoShimSpike.cs RunInput checks these ids and positions). MIT.

  python3 testpools/inputs.py
"""
import os
from iop import *

p = Pool()
FONT, FONT_SMALL = 23000, 23001
fields = [  # (field, label, y)
    (9000, "Rate", 40), (9001, "Count", 80), (9002, "Locked", 120), (8000, "Name", 160),
    (8001, "Note", 200), (7000, "Enable", 240), (10000, "Crop", 280),
]
p.working_set(0, 1000, [(11000, 0, 0)])
mask = [(11000, 10, 4)]
for i, (field, label, y) in enumerate(fields):
    mask += [(11010 + i, 10, y), (field, 150, y)]
p.data_mask(1000, mask)
p.font(FONT, F12x16)
p.font(FONT_SMALL, F8x8)
p.output_string(11000, 300, 20, FONT, "Input test")
for i, (_, label, _) in enumerate(fields):
    p.output_string(11010 + i, 130, 20, FONT, label)

# Rate: own value 50 (shown 25.0), 0..100, scale 0.5, one decimal. Count: in a number variable.
# Locked: disabled. Name: capitals and spaces only. Note: in a string variable.
p.input_number(9000, 120, 20, FONT, 50, 0, 100, scale=0.5, decimals=1)
p.input_number(9001, 120, 20, FONT, 0, 0, 1000, variable=21000)
p.number_variable(21000, 7)
p.input_number(9002, 120, 20, FONT, 3, 0, 10, enabled=False)
p.input_string(8000, 200, 20, FONT, "HELLO     ", attributes=26000)
p.input_attributes(26000, "ABCDEFGHIJKLMNOPQRSTUVWXYZ ")
p.input_string(8001, 200, 20, FONT, "        ", variable=22000)
p.string_variable(22000, "abc     ")
p.input_boolean(7000, 20, FONT, value=0)
crops = [11020, 11021, 11022]
p.input_list(10000, 200, 20, crops)
for oid, text in zip(crops, ["Wheat", "Barley", "Canola"]):
    p.output_string(oid, 200, 20, FONT, text)

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "inputs.iop")
print(out, *p.save(out))
