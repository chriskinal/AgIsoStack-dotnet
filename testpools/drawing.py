#!/usr/bin/env python3
"""drawing.iop: the drawing code's test pool. One data mask per theme (shapes, text, numbers,
gauges, pictures, structure), an alarm mask and soft keys (masks of 12 and of 14); each sample is labelled with what it
should look like. Plain, not pretty: a test fixture. MIT.

  python3 testpools/drawing.py
"""
import os
from iop import *

p = Pool()
_next = [20000]


def nid():
    _next[0] += 1
    return _next[0]


LABEL = p.font(nid(), F6x8)
TITLE = p.font(nid(), F8x12, style=BOLD)
TEXT = p.font(nid(), F8x8)
THIN = p.line_attributes(nid(), BLACK, 1)
THICK = p.line_attributes(nid(), BLUE, 4)
NOLINE = p.line_attributes(nid(), BLACK, 0)
FILL_YELLOW = p.fill_attributes(nid(), 2, YELLOW)
FILL_LINE = p.fill_attributes(nid(), 1)
FILL_NONE = p.fill_attributes(nid(), 0)


def label(text, w=150, h=10, font=None):
    return p.output_string(nid(), w, h, font or LABEL, text, options=1)


def title(text):
    return p.output_string(nid(), 400, 14, TITLE, text, options=1)


# Pictures: a 16 x 16 test card (red, green, blue and yellow quarters, a black diagonal), and an
# 8 x 8 black and yellow check for fill patterns.
card = [[0 if x == y else (RED if y < 8 else BLUE) if x < 8 else (GREEN if y < 8 else YELLOW) for x in range(16)]
        for y in range(16)]
mono = [[1 if x == y or x == 15 - y else 0 for x in range(16)] for y in range(16)]
ring = [[BLACK if 5 <= ((x - 7.5) ** 2 + (y - 7.5) ** 2) ** 0.5 <= 7.5 else WHITE for x in range(16)] for y in range(16)]
check = [[BLACK if (x // 2 + y // 2) % 2 else YELLOW for x in range(8)] for y in range(8)]
PATTERN = p.picture(nid(), 8, check)
FILL_PATTERN = p.fill_attributes(nid(), 3, WHITE, PATTERN)

masks = []

# ---- 1000 shapes ----
items = [(title("1000 shapes"), 4, 2)]
y = 24
items.append((label("rect: line 1, line 4 blue, no line+yellow fill, line+fill, pattern fill", 470), 4, y))
for i, (line, fill) in enumerate([(THIN, NONE), (THICK, NONE), (NOLINE, FILL_YELLOW), (THICK, FILL_YELLOW), (THIN, FILL_PATTERN)]):
    items.append((p.rectangle(nid(), line, 70, 40, fill), 4 + i * 90, y + 12))
y = 84
items.append((label("rect line 4, suppressed: top, right, bottom, left, all but top", 470), 4, y))
for i, s in enumerate([1, 2, 4, 8, 14]):
    items.append((p.rectangle(nid(), THICK, 70, 40, FILL_YELLOW, suppress=s), 4 + i * 90, y + 12))
y = 144
items.append((label("lines: solid, dashes F0F0, dots AAAA, FF00 (w4); \\ / and flat, upright", 470), 4, y))
for i, art in enumerate([0xFFFF, 0xF0F0, 0xAAAA, 0xFF00]):
    la = p.line_attributes(nid(), [BLACK, RED, GREEN, BLUE][i], 4 if i == 3 else 2, art)
    items.append((p.line(nid(), la, 60, 40, direction=i % 2), 4 + i * 70, y + 12))
items.append((p.line(nid(), THIN, 60, 0), 290, y + 30))
items.append((p.line(nid(), THIN, 0, 40), 370, y + 12))
y = 204
items.append((label("ellipses: closed, open 0-90, segment 45-315, section 30-300, start=end, circle", 470), 4, y))
specs = [(0, 0, 0, FILL_YELLOW), (1, 0, 90, NONE), (2, 45, 315, FILL_YELLOW), (3, 30, 300, FILL_YELLOW),
         (3, 90, 90, FILL_YELLOW), (0, 0, 0, FILL_LINE)]
for i, (kind, a0, a1, fill) in enumerate(specs):
    items.append((p.ellipse(nid(), THIN if i != 5 else THICK, 70 if i != 5 else 40, 40, kind, a0, a1, fill), 4 + i * 78, y + 12))
y = 264
items.append((label("polygons: convex, non-convex star, complex bow tie (filled), open zigzag", 470), 4, y))
items.append((p.polygon(nid(), 60, 50, THIN, [(0, 50), (30, 0), (60, 50)], FILL_YELLOW, 0), 4, y + 12))
star = [(30, 0), (37, 18), (58, 18), (41, 30), (48, 50), (30, 38), (12, 50), (19, 30), (2, 18), (23, 18)]
items.append((p.polygon(nid(), 60, 50, THIN, star, FILL_YELLOW, 1), 90, y + 12))
items.append((p.polygon(nid(), 60, 50, THIN, [(0, 0), (60, 50), (60, 0), (0, 50)], FILL_YELLOW, 2), 180, y + 12))
items.append((p.polygon(nid(), 60, 50, THICK, [(0, 50), (15, 0), (30, 50), (45, 0), (60, 50)], NONE, 3), 270, y + 12))
p.data_mask(1000, items, keys=4000)
masks.append(1000)

# ---- 1001 text ----
items = [(title("1001 text"), 4, 2)]
y = 22
for size, name in [(F6x8, "6x8"), (F8x8, "8x8"), (F8x12, "8x12"), (F12x16, "12x16"), (F16x16, "16x16"), (F16x24, "16x24"), (F24x32, "24x32")]:
    f = p.font(nid(), size)
    h = [8, 8, 12, 16, 16, 24, 32][size]
    items.append((p.output_string(nid(), 240, h, f, "Aa " + name, options=1), 4, y))
    y += h + 2
x = 250
y = 22
for style, name in [(BOLD, "bold"), (ITALIC, "italic"), (UNDERLINED, "underlined"), (CROSSED, "crossed out"), (INVERTED, "inverted")]:
    f = p.font(nid(), F8x12, BLUE, style)
    items.append((p.output_string(nid(), 200, 14, f, name, bg=YELLOW), x, y))
    y += 16
items.append((label("justified in grey boxes: row = top/middle/bottom", 230), x, y + 2))
y += 12
grey = p.font(nid(), F6x8)
for r, v in enumerate([TOP, MIDDLE, BOTTOM]):
    for c, h in enumerate([LEFT, CENTRE, RIGHT]):
        items.append((p.output_string(nid(), 72, 26, grey, "TMB"[r] + "LCR"[c], just=v | h, bg=SILVER), x + c * 76, y + r * 30))
y = 230
items.append((label("auto wrap in 150 x 40 (yellow); no wrap clipped (cyan); transparent over red", 470), 4, y))
items.append((p.output_string(nid(), 150, 40, TEXT, "The quick brown fox jumps over the lazy dog", options=2, bg=YELLOW), 4, y + 12))
items.append((p.output_string(nid(), 150, 40, TEXT, "The quick brown fox jumps over the lazy dog", bg=CYAN), 164, y + 12))
items.append((p.rectangle(nid(), NOLINE, 140, 40, p.fill_attributes(nid(), 2, RED)), 324, y + 12))
items.append((p.output_string(nid(), 140, 40, p.font(nid(), F8x12, WHITE), "see-through", options=1), 324, y + 24))
y = 290
items.append((label("Latin-1 and UTF-16 (wide) text", 470), 4, y))
items.append((p.output_string(nid(), 230, 16, p.font(nid(), F8x12), "Gr\xfc\xdfe 20\xb0C \xbd \xa9"), 4, y + 12))
items.append((p.output_string(nid(), 230, 16, p.font(nid(), F8x12), p.wide("Düse 7 Ω → OK")), 240, y + 12))
y = 320
items.append((label("colours 0-15 as font colours", 470), 4, y))
for i in range(16):
    items.append((p.output_string(nid(), 28, 16, p.font(nid(), F8x12, i), "%02d" % i, bg=SILVER if i == 1 else WHITE), 4 + i * 29, y + 12))
p.data_mask(1001, items, keys=4000)
masks.append(1001)

# ---- 1002 numbers ----
items = [(title("1002 numbers: shown | expected"), 4, 2)]
NUM = p.font(nid(), F8x12)
cases = [
    (12345, dict(scale=0.01, decimals=2), "123.45"),
    (0, dict(offset=-500, scale=0.1, decimals=1), "-50.0"),
    (123456, dict(decimals=2, fmt=1), "1.23e+05"),
    (42, dict(options=2), "0000000042 (fills the field)"),
    (0, dict(options=4), "(blank)"),
    (1999, dict(scale=0.001, decimals=1, options=8), "1.9 (truncated)"),
    (1999, dict(scale=0.001, decimals=1), "2.0 (rounded)"),
    (7, dict(just=RIGHT), "7 at the right"),
    (7, dict(just=CENTRE), "7 in the middle"),
    (100, dict(scale=-1.5), "-150"),
    (0xFFFFFFFF, dict(), "4294967295"),
    (5, dict(variable=p.number_variable(nid(), 77)), "77 (from a variable)"),
]
for i, (value, kw, expect) in enumerate(cases):
    y = 24 + i * 22
    items.append((p.output_number(nid(), 120, 16, NUM, value, bg=SILVER, **kw), 4, y))
    items.append((label("expect " + expect, 300, 10, TEXT), 140, y + 4))
y = 24 + len(cases) * 22
items.append((p.output_list(nid(), 120, 16, [label("list item 0"), label("list item 1"), label("list item 2")], value=1), 4, y))
items.append((label("expect list item 1", 300, 10, TEXT), 140, y + 4))
p.data_mask(1002, items, keys=4000)
masks.append(1002)

# ---- 1003 gauges ----
items = [(title("1003 gauges"), 4, 2)]
items.append((label("meters: 0-180 anticlockwise v0; clockwise v25; 320-220 clockwise v50; ticks only v100; v150 over max", 470), 4, 18))
specs = [(0, dict(start=0, end=180, options=7)), (25, dict(start=0, end=180, options=15)),
         (50, dict(start=320, end=220, options=15, ticks=11)), (100, dict(start=0, end=180, options=4)),
         (150, dict(start=0, end=180, options=15))]
for i, (value, kw) in enumerate(specs):
    items.append((p.meter(nid(), 84, value, **kw), 4 + i * 94, 32))
items.append((label("linear: up, down, right, left (border, ticks, target 70); line type up; no border", 470), 4, 126))
x = 4
for i, options in enumerate([33, 1, 49, 17, 41, 32]):
    gw, gh = (120, 16) if options & 16 else (16, 80)
    options |= 6 if i < 4 else 0  # target line and ticks on the first four
    items.append((p.linear_bar_graph(nid(), gw, gh, 40, target=70, ticks=5, options=options), x, 140))
    x += gw + 14
items.append((label("arched: anticlockwise filled, clockwise filled, clockwise line type; border, target 70", 470), 4, 236))
for i, options in enumerate([3, 19, 27]):
    items.append((p.arched_bar_graph(nid(), 100, 100, 40, 14, start=0, end=180, target=70, options=options), 4 + i * 120, 250))
items.append((p.arched_bar_graph(nid(), 100, 100, 75, 10, start=320, end=220, options=17), 364, 250))
items.append((label("last: 320-220 clockwise v75", 120), 364, 352))
p.data_mask(1003, items, keys=4000)
masks.append(1003)

# ---- 1004 pictures ----
items = [(title("1004 pictures"), 4, 2)]
items.append((label("test card 16x16 at 64: 8-bit, 4-bit, 8-bit RLE, 4-bit RLE", 470), 4, 20))
for i, (fmt, options) in enumerate([(2, 0), (1, 0), (2, 4), (1, 4)]):
    items.append((p.picture(nid(), 64, card, fmt, options), 4 + i * 80, 34))
items.append((label("monochrome X at 64, and RLE; at natural size 16", 470), 4, 110))
items.append((p.picture(nid(), 64, mono, 0, 0), 4, 124))
items.append((p.picture(nid(), 64, mono, 0, 4), 84, 124))
items.append((p.picture(nid(), 16, card, 2, 0), 164, 124))
items.append((label("ring, white transparent, over red: red shows inside and around", 470), 4, 200))
items.append((p.rectangle(nid(), NOLINE, 140, 70, p.fill_attributes(nid(), 2, RED)), 4, 214))
items.append((p.picture(nid(), 64, ring, 2, 1, transparent=WHITE), 40, 217))
items.append((label("fill pattern in a rectangle and an ellipse", 470), 4, 300))
items.append((p.rectangle(nid(), THIN, 120, 60, FILL_PATTERN), 4, 314))
items.append((p.ellipse(nid(), THIN, 120, 60, 0, fill=FILL_PATTERN), 140, 314))
p.data_mask(1004, items, keys=4000)
masks.append(1004)

# ---- 1005 structure ----
items = [(title("1005 structure"), 4, 2)]
items.append((label("clipped child; hidden container (nothing between the bars); nested at +10,+10", 470), 4, 18))
inner = p.rectangle(nid(), THIN, 80, 40, FILL_YELLOW)
items.append((p.container(nid(), 100, 60, [(p.rectangle(nid(), NOLINE, 100, 60, p.fill_attributes(nid(), 2, SILVER)), 0, 0), (inner, 60, 30)]), 4, 32))
items.append((p.rectangle(nid(), THIN, 4, 60, FILL_LINE), 160, 32))
items.append((p.container(nid(), 100, 60, [(p.rectangle(nid(), THIN, 100, 60, FILL_YELLOW), 0, 0)], hidden=1), 168, 32))
items.append((p.rectangle(nid(), THIN, 4, 60, FILL_LINE), 272, 32))
c3 = p.container(nid(), 40, 40, [(p.rectangle(nid(), THICK, 30, 30, FILL_YELLOW), 10, 10)])
c2 = p.container(nid(), 60, 60, [(c3, 10, 10)])
items.append((p.container(nid(), 80, 80, [(p.rectangle(nid(), THIN, 80, 80), 0, 0), (c2, 10, 10)]), 300, 32))
items.append((label("object pointers: to a yellow rectangle, to nothing", 470), 4, 120))
items.append((p.object_pointer(nid(), p.rectangle(nid(), THIN, 60, 30, FILL_YELLOW)), 4, 134))
items.append((p.object_pointer(nid(), NONE), 80, 134))
items.append((label("buttons: plain, latched, no border, transparent, disabled", 470), 4, 176))
for i, options in enumerate([0, 3, 32, 8, 16]):
    face = p.output_string(nid(), 70, 12, TEXT, ["plain", "latched", "no border", "transp.", "disabled"][i], options=1)
    items.append((p.button(nid(), 84, 40, i, [(face, 4, 10)], bg=CYAN, options=options), 4 + i * 92, 190))
items.append((label("input fields: number, string, boolean on, list (value 1)", 470), 4, 244))
items.append((p.input_number(nid(), 80, 16, TEXT, 42, 0, 100, bg=SILVER), 4, 258))
items.append((p.input_string(nid(), 100, 16, TEXT, "text", bg=SILVER), 90, 258))
items.append((p.input_boolean(nid(), 16, TEXT, value=1, bg=SILVER), 196, 258))
items.append((p.input_list(nid(), 100, 16, [label("item 0"), label("item 1")], value=1), 220, 258))
p.data_mask(1005, items, keys=4000)
masks.append(1005)

# ---- 1006, 1007 soft keys: as many as the terminal shows (12), and more than that (14) ----
for oid, keys_id, n in [(1006, 4002, 12), (1007, 4003, 14)]:
    items = [(title("%d soft keys: %d" % (oid, n)), 4, 2)]
    items.append((label("keys 0 to %d in order, code = number; the default terminal shows 12" % (n - 1), 470), 4, 24))
    p.data_mask(oid, items, keys=keys_id)
    masks.append(oid)

# ---- the working set, an alarm mask, soft keys ----
p.working_set(0, 1000, [(label("drawing", 60), 0, 0)])
alarm_text = p.output_string(nid(), 440, 60, p.font(nid(), F16x24, WHITE), "ALARM high priority", options=3, just=CENTRE)
p.alarm_mask(2000, [(alarm_text, 20, 100)], keys=4001, bg=RED, priority=0, signal=1)
key_text = lambda t: p.output_string(nid(), 76, 20, TEXT, t, options=1, just=CENTRE)
keys = [p.key(nid(), i, [(key_text("key %d" % i), 2, 30)], bg=[CYAN, YELLOW, LIME][i]) for i in range(3)]
keys.append(p.object_pointer(nid(), p.key(nid(), 3, [(key_text("by ptr"), 2, 30)], bg=SILVER)))
p.soft_key_mask(4000, keys)
p.soft_key_mask(4001, [p.key(nid(), 9, [(key_text("ack"), 2, 30)], bg=WHITE)])
for keys_id, n in [(4002, 12), (4003, 14)]:
    p.soft_key_mask(keys_id, [p.key(nid(), i, [(key_text("key %d" % i), 2, 30)], bg=[CYAN, YELLOW, LIME, SILVER][i % 4])
                              for i in range(n)])

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "drawing.iop")
print(out, *p.save(out))
