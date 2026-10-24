"""A small ISOBUS object pool writer for test pools, from ISO 11783-6 Annex B (VT version 3 and 4
object layouts). MIT. Each function appends one object; save() writes the pool.

Colours are indexes into the standard palette: 0 black, 1 white, 2 green, 3 teal, 4 maroon,
5 purple, 6 olive, 7 silver, 8 grey, 9 blue, 10 lime, 11 cyan, 12 red, 13 magenta, 14 yellow,
15 navy. Angles are in degrees (stored halved). NONE is the null object id.
"""
import struct

NONE = 0xFFFF
BLACK, WHITE, GREEN, TEAL, MAROON, PURPLE, OLIVE, SILVER, GREY, BLUE, LIME, CYAN, RED, MAGENTA, YELLOW, NAVY = range(16)

# Font sizes (Annex B font size codes) and styles.
F6x8, F8x8, F8x12, F12x16, F16x16, F16x24, F24x32, F32x32, F32x48, F48x64 = range(10)
BOLD, CROSSED, UNDERLINED, ITALIC, INVERTED = 1, 2, 4, 8, 16
# Justification: horizontal | vertical.
LEFT, CENTRE, RIGHT = 0, 1, 2
TOP, MIDDLE, BOTTOM = 0, 4, 8


class Pool:
    def __init__(self):
        self.objects = []

    def add(self, oid, kind, body):
        self.objects.append(struct.pack("<HB", oid, kind) + body)
        return oid

    def save(self, path):
        data = b"".join(self.objects)
        with open(path, "wb") as f:
            f.write(data)
        return len(self.objects), len(data)

    # ---- helpers ----
    @staticmethod
    def children(items):
        return b"".join(struct.pack("<Hhh", oid, x, y) for oid, x, y in items)

    @staticmethod
    def text(value):
        return value if isinstance(value, bytes) else value.encode("latin-1")

    @staticmethod
    def wide(value):
        return b"\xff\xfe" + value.encode("utf-16-le")

    # ---- top level ----
    def working_set(self, oid, mask, items=(), bg=WHITE, selectable=1):
        return self.add(oid, 0, struct.pack("<BBHBBB", bg, selectable, mask, len(items), 0, 0) + self.children(items))

    def data_mask(self, oid, items, keys=NONE, bg=WHITE):
        return self.add(oid, 1, struct.pack("<BHBB", bg, keys, len(items), 0) + self.children(items))

    def alarm_mask(self, oid, items, keys=NONE, bg=WHITE, priority=1, signal=0):
        return self.add(oid, 2, struct.pack("<BHBBBB", bg, keys, priority, signal, len(items), 0) + self.children(items))

    def container(self, oid, w, h, items, hidden=0):
        return self.add(oid, 3, struct.pack("<HHBBB", w, h, hidden, len(items), 0) + self.children(items))

    def soft_key_mask(self, oid, keys, bg=SILVER):
        return self.add(oid, 4, struct.pack("<BBB", bg, len(keys), 0) + b"".join(struct.pack("<H", k) for k in keys))

    def key(self, oid, code, items, bg=WHITE):
        return self.add(oid, 5, struct.pack("<BBBB", bg, code, len(items), 0) + self.children(items))

    # options: 1 latchable, 2 latched, 4 suppress border, 8 transparent, 16 disabled, 32 no border
    def button(self, oid, w, h, code, items, bg=SILVER, border=BLACK, options=0):
        return self.add(oid, 6, struct.pack("<HHBBBBBB", w, h, bg, border, code, options, len(items), 0) + self.children(items))

    # ---- inputs ----
    def input_boolean(self, oid, width, font, value=0, enabled=1, bg=WHITE, variable=NONE):
        return self.add(oid, 7, struct.pack("<BHHHBBB", bg, width, font, variable, value, enabled, 0))

    def input_string(self, oid, w, h, font, value, attributes=NONE, variable=NONE, options=0, just=0, bg=WHITE, enabled=1):
        value = self.text(value)
        return self.add(oid, 8, struct.pack("<HHBHHBHBB", w, h, bg, font, attributes, options, variable, just, len(value))
                        + value + struct.pack("<BB", enabled, 0))

    def input_number(self, oid, w, h, font, value, lo, hi, scale=1.0, offset=0, decimals=0, variable=NONE,
                     options=0, fmt=0, just=0, enabled=1, bg=WHITE):
        return self.add(oid, 9, struct.pack("<HHBHBHIIIifBBBBB", w, h, bg, font, options, variable, value, lo, hi, offset,
                                            scale, decimals, fmt, just, 1 if enabled else 0, 0))

    def input_list(self, oid, w, h, items, value=0, variable=NONE, enabled=1):
        return self.add(oid, 10, struct.pack("<HHHBBBB", w, h, variable, value, len(items), 1 if enabled else 0, 0)
                        + b"".join(struct.pack("<H", i) for i in items))

    # ---- outputs ----
    # options: 1 transparent, 2 auto wrap
    def output_string(self, oid, w, h, font, value, options=0, just=0, bg=WHITE, variable=NONE):
        value = self.text(value)
        return self.add(oid, 11, struct.pack("<HHBHBHBH", w, h, bg, font, options, variable, just, len(value)) + value + b"\0")

    # options: 1 transparent, 2 leading zeros, 4 zero as blank, 8 truncate; fmt 1: exponential
    def output_number(self, oid, w, h, font, value, scale=1.0, offset=0, decimals=0, options=0, fmt=0, just=0,
                      bg=WHITE, variable=NONE):
        return self.add(oid, 12, struct.pack("<HHBHBHIifBBBB", w, h, bg, font, options, variable, value, offset, scale,
                                             decimals, fmt, just, 0))

    def output_list(self, oid, w, h, items, value=0, variable=NONE):
        return self.add(oid, 37, struct.pack("<HHHBBB", w, h, variable, value, len(items), 0)
                        + b"".join(struct.pack("<H", i) for i in items))

    # direction 0: top left to bottom right, 1: bottom left to top right
    def line(self, oid, line_attr, w, h, direction=0):
        return self.add(oid, 13, struct.pack("<HHHBB", line_attr, w, h, direction, 0))

    # suppress: 1 top, 2 right, 4 bottom, 8 left
    def rectangle(self, oid, line_attr, w, h, fill=NONE, suppress=0):
        return self.add(oid, 14, struct.pack("<HHHBHB", line_attr, w, h, suppress, fill, 0))

    # kind 0 closed, 1 open arc, 2 closed segment, 3 closed section
    def ellipse(self, oid, line_attr, w, h, kind=0, start=0, end=0, fill=NONE):
        return self.add(oid, 15, struct.pack("<HHHBBBHB", line_attr, w, h, kind, start // 2, end // 2, fill, 0))

    # kind 0 convex, 1 non-convex, 2 complex, 3 open
    def polygon(self, oid, w, h, line_attr, points, fill=NONE, kind=0):
        return self.add(oid, 16, struct.pack("<HHHHBBB", w, h, line_attr, fill, kind, len(points), 0)
                        + b"".join(struct.pack("<HH", x, y) for x, y in points))

    # options: 1 arc, 2 border, 4 ticks, 8 clockwise
    def meter(self, oid, width, value, lo=0, hi=100, start=0, end=180, ticks=5, options=7,
              needle=RED, border=BLACK, arc=BLACK, variable=NONE):
        return self.add(oid, 17, struct.pack("<HBBBBBBBHHHHB", width, needle, border, arc, options, ticks, start // 2,
                                             end // 2, lo, hi, variable, value, 0))

    # options: 1 border, 2 target line, 4 ticks, 8 line not filled, 16 horizontal, 32 grows positive
    def linear_bar_graph(self, oid, w, h, value, lo=0, hi=100, target=0, ticks=0, options=0,
                         colour=GREEN, target_colour=RED, variable=NONE):
        return self.add(oid, 18, struct.pack("<HHBBBBHHHHHHB", w, h, colour, target_colour, options, ticks, lo, hi,
                                             variable, value, NONE, target, 0))

    # options: 1 border, 2 target line, 8 line not filled, 16 clockwise
    def arched_bar_graph(self, oid, w, h, value, bar_width, lo=0, hi=100, start=0, end=180, target=0, options=0,
                         colour=GREEN, target_colour=RED, variable=NONE):
        return self.add(oid, 19, struct.pack("<HHBBBBBHHHHHHHB", w, h, colour, target_colour, options, start // 2,
                                             end // 2, bar_width, lo, hi, variable, value, NONE, target, 0))

    # rows: lists of colour indexes. fmt 0 monochrome, 1 4-bit, 2 8-bit; options: 1 transparent, 4 run-length
    def picture(self, oid, width, rows, fmt=2, options=0, transparent=0):
        h, w = len(rows), len(rows[0])
        raw = bytearray()
        for row in rows:  # each row starts on a byte
            if fmt == 2:
                raw += bytes(row)
            elif fmt == 1:
                for i in range(0, w, 2):
                    raw.append((row[i] << 4) | (row[i + 1] if i + 1 < w else 0))
            else:
                for i in range(0, w, 8):
                    byte = 0
                    for b in range(8):
                        if i + b < w and row[i + b]:
                            byte |= 0x80 >> b
                    raw.append(byte)
        if options & 4:
            rle = bytearray()
            i = 0
            while i < len(raw):
                n = 1
                while i + n < len(raw) and raw[i + n] == raw[i] and n < 255:
                    n += 1
                rle += bytes([n, raw[i]])
                i += n
            raw = rle
        return self.add(oid, 20, struct.pack("<HHHBBBIB", width, w, h, fmt, options, transparent, len(raw), 0) + bytes(raw))

    def number_variable(self, oid, value):
        return self.add(oid, 21, struct.pack("<I", value))

    def string_variable(self, oid, value):
        value = self.text(value)
        return self.add(oid, 22, struct.pack("<H", len(value)) + value)

    # ---- attributes ----
    def font(self, oid, size, colour=BLACK, style=0, kind=0):
        return self.add(oid, 23, struct.pack("<BBBBB", colour, size, kind, style, 0))

    def line_attributes(self, oid, colour=BLACK, width=1, art=0xFFFF):
        return self.add(oid, 24, struct.pack("<BBHB", colour, width, art, 0))

    # kind 0 none, 1 line colour, 2 fill colour, 3 pattern
    def fill_attributes(self, oid, kind, colour=WHITE, pattern=NONE):
        return self.add(oid, 25, struct.pack("<BBHB", kind, colour, pattern, 0))

    # only: True when the characters listed are the valid ones
    def input_attributes(self, oid, chars, only=True):
        chars = self.text(chars)
        return self.add(oid, 26, struct.pack("<BB", 0 if only else 1, len(chars)) + chars + b"\0")

    def object_pointer(self, oid, target):
        return self.add(oid, 27, struct.pack("<H", target))
