# The terminal API

The C interface a host uses to run a virtual terminal: what goes in, what comes out, and where the
drawing is done. Replaces the spike's fixed test surface (PLAN.md, Next item 1). Written
2026-10-24, on the `api` branch.

## Where the drawing happens

AgOpenWeb's only UI is its web client (CanvasKit in a browser or web view), so C# never draws: it
carries messages between the stack and the page. The pool is therefore flattened **on the native
side** into a draw list of plain primitives, which C# relays untouched and the page paints in
order. One renderer, in C++, MIT, tested here against upstream's pools; the painter on the page is
small and has no ISOBUS knowledge. The alternative (the object tree across C, C# and into the page)
would put the renderer in JavaScript and push about 40 object types through two boundaries.

## Shape

```
host ── isoshim_receive (frames in, any thread) ─────────┐
     ── isoshim_step (every 2 ms, one thread) ───────────┤ shim: VT server, renderer
     ── input: soft key, button, value, select, activate ┤
     ◀─ send (frames out)                                │
     ◀─ event (numbers: addresses, pools, shutdowns)     │
     ◀─ message (JSON: screen, working sets, alarms)     │
     ◀─ image (RGBA pixels, once per picture)            │
     ◀─ storage (save, load, list, delete pool versions) ┘
```

Everything except `isoshim_receive` and the log runs on the stepping thread, as now.

- **Start:** `isoshim_vt_start(config, host)`. The config is the terminal's NAME (identity,
  manufacturer, function instance), preferred address, data mask size, soft key descriptor size,
  physical and virtual soft key counts, VT version, supported fonts, packets per clear-to-send.
  The host struct holds the callbacks. One terminal per process.
- **The self-test's implement** stays as a test helper: `isoshim_test_implement(pool)` puts
  upstream's VT client on channel 1 beside the terminal.
- **Messages out** are JSON (UTF-8, one object per call, `"type"` first), so the host can forward
  them to the page as they are and C# needs no decoder:
  - `workingSets`: every implement connected, with its NAME (16 hex digits), address, pool state
    (`transferring`, `parsing`, `ready`, `failed`, with progress), which one is active.
  - `screen`: the active implement's data or alarm mask and its soft key mask as draw lists. Sent
    when the server repaints and the list differs from the last one sent.
  - `alarm`: an implement (active or not) has shown an alarm mask, with priority and acoustic
    signal; cleared when it leaves it. (Later.)
  - `audio`: Control Audio Signal (upstream acknowledges and drops it, gap 5). (Later.)
- **Images** come by their own callback, `image(id, width, height, rgba)`, once per picture graphic
  (decoded natively: 1, 4 and 8 bit, run-length, the working set's colour table, transparency);
  the draw list refers to them by id.
- **Input back:** `isoshim_soft_key(id, state)`, `isoshim_button(id, state)` (state: released,
  pressed, still held, aborted; a press is always followed by a release or an abort),
  `isoshim_select(id)`, `isoshim_set_number(id, value)` (input number, boolean, list),
  `isoshim_set_string(id, utf8)`, `isoshim_activate(name)`. The terminal updates its own copy
  (the object or its variable), sends the message to the implement, and runs the object's macros.
- **Storage** by callback, keyed by the implement's 64-bit NAME and the version label: `save`,
  `load`, `list`, `delete`. A callback that returns data hands it back with
  `isoshim_reply(data, len)` before it returns (the shim copies it), so no memory crosses the
  boundary in the other direction.

## The draw list

Coordinates are pixels in the area being drawn, origin top left, the painter scales the whole
area. Colours are `#rrggbb`, already resolved through the working set's colour table. Items are
painted in order; each carries the id of the object it came from (`o`), so the page can match a
tap to an object and a later version can patch by id.

```json
{"type":"screen","ws":"A00084000C01FFFF","mask":1000,"kind":"data","w":480,"h":480,"bg":"#ffffff",
 "items":[...],
 "keys":{"mask":4000,"w":80,"h":80,"bg":"#c0c0c0","keys":[{"o":5000,"code":5,"bg":"#ff0000","items":[...]}]}}
```

| `t` | Fields | From |
|---|---|---|
| `clip` / `unclip` | `x y w h` | masks, containers, buttons, keys: children never draw outside them |
| `rect` | `x y w h`, `fill`, `line`, `lw`, `sides` (which of the four lines are drawn) | rectangles, backgrounds, buttons, bar graphs |
| `line` | `x1 y1 x2 y2 c lw` (`dash`: the 16 bit pattern when not solid) | lines, meters, ticks |
| `ellipse` | `x y w h`, `kind` (closed, open, segment, section), `a0 a1` (degrees), `fill line lw` | ellipses, meters, arched bar graphs |
| `poly` | `pts` (x, y pairs), `closed`, `fill line lw` | polygons, needles |
| `text` | `x y w h`, `s`, `c`, `bg` (absent: transparent), `fw fh` (font cell), `style` (bold, crossed, underlined, italic, inverted, flash), `hj vj` (0 left/top, 1 middle, 2 right/bottom), `wrap` | output and input strings, numbers, lists |
| `image` | `x y w h`, `img` | picture graphics, fill patterns |
| `hit` | `x y w h`, `kind` (`button`, `input`, `key`), `latch`, `on` (enabled) | what the operator can touch |
| `box` | `x y w h`, `label` | an object the renderer does not draw: a labelled box, never dropped |

Fonts: the standard's fonts are fixed cells (6 x 8 up to 128 x 192); the painter fills the cell
with a monospace face, as espVT does with one scaled font. Numbers are formatted natively (scale,
offset, decimals, exponential, leading zeros, blank zero, truncation).

A reference painter (`painter/vtpaint.js`, Canvas 2D, MIT) lives here for the tests and as the
starting point for AgOpenWeb's page.

## Steps

1. The config, the host struct, working sets and storage; the self-test and the bench on the new
   surface.
2. The renderer: masks, containers, pointers, shapes, text, numbers, variables, lists, pictures,
   soft keys, buttons; meters and bar graphs; labelled boxes for the rest. The self-test writes
   the VT3 pool's screens as JSON and an HTML page that paints them, to look at.
3. Input: soft keys and buttons by id, selecting an input, entering values, macros.
4. Alarms and audio; then the upstream gaps the terminal meets (PLAN.md), as upstream pull
   requests.
5. The C# package: one `VirtualTerminal` class over the C interface, native libraries per
   runtime, NuGet-shaped.
