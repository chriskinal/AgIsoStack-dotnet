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
  - `alarms`: every implement showing an alarm mask, shown on screen or not, sent whenever the
    list changes: `{"type":"alarms","list":[{"ws","address","mask","priority","signal"}]}`.
    Priority 0 high, 1 medium, 2 low; acoustic signal 0 highest to 2 lowest, 3 none (always 0
    for now: upstream gap 10). The terminal does not switch implements for an alarm: the host
    shows it over whatever is open and can bring that implement up with `isoshim_activate`.
    The implement clears it by leaving the alarm mask (usually on its own acknowledge key).
  - `audio`: Control Audio Signal, `{"type":"audio","ws","activations","frequency","on","off"}`
    (Hz and ms; activations 0 stops a sounding signal); `volume`: Set Audio Volume,
    `{"type":"volume","ws","percent"}`. Upstream acknowledges both and drops them (gap 5); the
    shim hears them alongside it.
  - The implement's ESC command closes its open field and is answered (upstream does not
    handle it, gap 8); the host gets the edit message with `"o":null`.
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
| `hit` | `x y w h`, `kind` (`button`, `input`, `key`), `latch`, `on` (enabled), `sel` (has the focus), `open` (open for input) | what the operator can touch |
| `box` | `x y w h`, `label` | an object the renderer does not draw: a labelled box, never dropped |

Fonts: the standard's fonts are fixed cells (6 x 8 up to 128 x 192); the painter fills the cell
with a monospace face, as espVT does with one scaled font. Numbers are formatted natively (scale,
offset, decimals, exponential, leading zeros, blank zero, truncation).

## Input fields

A tap on an `input` hit calls `isoshim_select(id, 1)`: the field takes the focus, the implement
hears it selected and open, and the host gets an edit message describing it, from inside that
call. The page shows an editor for it and answers with a value or an escape; either closes it,
and the host gets `{"type":"edit","ws":...,"o":null}`. The implement can also open a field
itself (Select Input Object); the same edit message follows.

```json
{"type":"edit","ws":"A0001000AFE00002","o":9000,"kind":"number","value":50,"min":0,"max":100,"offset":0,"scale":0.5,"decimals":1,"text":"25.0"}
{"type":"edit","ws":"...","o":8000,"kind":"string","value":"HELLO","length":10,"chars":"ABC...Z ","only":true}
{"type":"edit","ws":"...","o":7000,"kind":"bool","value":0}
{"type":"edit","ws":"...","o":10000,"kind":"list","value":0,"w":200,"h":20,"options":[[...items...],null,...]}
```

- **number**: values are raw; what the operator sees is `(raw + offset) * scale` to `decimals`
  places. `isoshim_set_number(id, raw)` takes `min <= raw <= max`.
- **string**: `value` has its padding spaces trimmed; `length` is the most characters it holds;
  `chars` with `only` true are the characters allowed, with `only` false the ones refused.
  `isoshim_set_string(id, utf8, len)`; stored in the field's own encoding (ISO 8859-1, or UTF-16
  when the pool's value is), padded with spaces.
- **bool**: `isoshim_set_number(id, 0 or 1)`; a touch UI toggles without an editor.
- **list**: each option is a draw list in the field's size at 0,0, `null` for an empty place.
  `isoshim_set_number(id, index)`.

The set calls return 1 (entered, sent), -1 (not a valid value: the field is unchanged and stays
open) or 0 (not an enabled input on the current screen). A value can be set without selecting
first; the field is selected on the way. `isoshim_escape()` abandons the open field (VT ESC).
Moving the focus away from an open field, or the mask changing under it, escapes it. Macros run
on selection, deselection, entry (of a value and of a new value) and escape. Messages the stack
cannot send at once (a multi-frame message while the last one to that implement is closing) wait
in order for a later step.

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

## The C# package

`dotnet/AgIsoStack.VirtualTerminal` (package `AgIsoStack.VirtualTerminal`; README.md has an
example). `VirtualTerminal` owns a thread that steps the stack every few milliseconds, at once
when a frame arrives or a call is queued. Every event and every `IPoolStore` call comes on that
thread, in order; a handler that throws is reported through `HandlerFailed`. Calls
(`SoftKeyAsync`, `SelectAsync`, `SetNumberAsync`, ...) come from any thread and complete when
the terminal has done them; from a handler they run at once. `InvokeAsync` runs code there, for
the raw interface (`AgIsoStack.Interop.IsoShim`). `Stop` from another thread waits for the
terminal to end; from the terminal's own thread (code that resumed there after awaiting
something a handler completed) it only asks. `Message` carries every message as JSON, for the
page; `WorkingSetsChanged`, `AlarmsChanged`, `Audio`, `Volume`, `AddressClaimed`, `PoolLoaded`,
`PoolRejected` and `ImplementLost` are the ones the host itself acts on, typed. Stores:
`FilePoolStore` (a folder per NAME, a file per label) and `MemoryPoolStore`. One terminal per
process: the native stack is one.
