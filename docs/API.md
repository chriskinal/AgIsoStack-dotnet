# API reference

Everything a host can call and everything it receives: the C# package `AgIsoStack.VirtualTerminal`,
the JSON messages and the draw list, and the C interface under them (`native/isoshim.h`). How to
use them together is in [GUIDE.md](GUIDE.md).

Contents: [VirtualTerminal](#virtualterminal) · [Options](#virtualterminaloptions) ·
[Types](#types) · [Pool stores](#pool-stores) · [AowLink](#aowlink) · [Messages](#messages) ·
[The draw list](#the-draw-list) · [The C interface](#the-c-interface) · [Design](#design)

All types are in namespace `AgIsoStack`; the raw interface is `AgIsoStack.Interop.IsoShim`.

## VirtualTerminal

`public sealed class VirtualTerminal : IDisposable`. An ISOBUS virtual terminal (server). It owns
one thread, which runs the stack and raises every event and every store call, in order. Calls may
come from any thread; from that thread (a handler) they run at once. One per process.

### Life

| Member | |
|---|---|
| `VirtualTerminal(VirtualTerminalOptions options)` | Throws `ArgumentException` if `options.Send` is null. |
| `void Start()` | Starts the terminal on its own thread; returns when it is running. Throws `InvalidOperationException` if another terminal is running in this process or the native library refuses the options. A stopped terminal can be started again. |
| `void Stop()` | Takes the terminal off the bus and ends its thread, waiting for any pool still parsing. Calls not yet done fail. From the terminal's own thread it only asks: the terminal stops after the current step. Safe at any point; does nothing if not running. |
| `void Dispose()` | `Stop()`. |
| `bool IsRunning` | |
| `int Address` | The terminal's address on the bus, -1 before it has claimed one. |

### Frames

| Member | |
|---|---|
| `void Receive(uint id, ReadOnlySpan<byte> data, bool extended = true, int channel = 0)` | A frame from the bus. Any thread; returns at once. At most 8 bytes (`ArgumentException`). |
| `void LinkDown()` | The link to the bus is gone. Any thread. |
| `void LinkUp()` | The link is back: every soft key or button held when it went, or pressed while it was down, is sent as aborted. Any thread. |

Frames out go through `VirtualTerminalOptions.Send`. `AowLink`'s `Attach` makes all of these calls for you.

### The operator

All of these return a task that completes once the terminal has acted. It fails with
`InvalidOperationException` if the terminal is not running. Object ids are in the shown
implement's pool.

| Member | Result |
|---|---|
| `Task<bool> SoftKeyAsync(ushort objectId, KeyState state)` | A Key object in the shown soft key mask. True: sent. |
| `Task<bool> ButtonAsync(ushort objectId, KeyState state)` | A Button object on the shown mask. True: sent. |
| `Task<bool> ActivateAsync(ulong name)` | Shows this implement. True: it is now the one shown. |
| `Task<bool> SelectAsync(ushort objectId, bool open)` | Gives an input field or button the focus; `open`: an editor is up for it (an `edit` message follows). True: done. |
| `Task<bool> DeselectAsync()` | Gives nothing the focus. |
| `Task<InputResult> SetNumberAsync(ushort objectId, uint value)` | Input number (raw value, before offset and scale), input boolean (0 or 1), input list (the index). Selects the field first if needed; closes it. |
| `Task<InputResult> SetStringAsync(ushort objectId, string text)` | Input string. |
| `Task<bool> EscapeAsync()` | Abandons the open field (VT ESC). True: one was open. |
| `Task<T> InvokeAsync<T>(Func<T> call)` | Runs `call` on the terminal's thread between steps: for the raw interface, which belongs to that thread. |

`KeyState`: `Pressed` (1), `Held` (2, repeat about every 200 ms while down: the host repeats it),
`Released` (0), `Aborted` (3, the press ended without a release). A press must end in a release or
an abort.

`InputResult`: `Entered` (1, sent to the implement), `Invalid` (-1, not a valid value: the field is
unchanged and stays open), `NotAnInput` (0, not an enabled input on the shown screen).

### Events

All on the terminal's thread.

| Event | |
|---|---|
| `Action<string> Message` | Every [message](#messages), as JSON. |
| `Action<VtImage> Image` | A picture, once, before the first screen that refers to it. |
| `Action<ulong?, IReadOnlyList<WorkingSetInfo>> WorkingSetsChanged` | The shown implement's NAME (null: none) and every implement connected. From the `workingSets` message. |
| `Action<IReadOnlyList<AlarmInfo>> AlarmsChanged` | Every implement showing an alarm mask, shown or not. From `alarms`. |
| `Action<AudioSignal> Audio` | Control Audio Signal from an implement. From `audio`. |
| `Action<ulong, int> Volume` | Set Audio Volume: the implement's NAME and the percentage. From `volume`. |
| `Action<int> AddressClaimed` | The terminal has this address. |
| `Action<int, int> PoolLoaded` | An implement's pool is up: its address, the number of objects. |
| `Action<int, int> PoolRejected` | An implement's pool was refused: its address, the object at fault. |
| `Action<int, int> ImplementLost` | An implement went away: its address, the reason (0 no maintenance message for 3 s, 1 it started over). |
| `Action<Exception> HandlerFailed` | A handler, `Send` or the store threw; the terminal carries on. |

## VirtualTerminalOptions

`public sealed class VirtualTerminalOptions`, init-only properties. Zero (or null) takes the default.

| Property | Type | Default | |
|---|---|---|---|
| `Send` | `FrameSender` | required | `delegate void FrameSender(int channel, uint id, ReadOnlySpan<byte> data, bool extended)`. Called on the terminal's thread; the span is valid only during the call. Queue and return. |
| `Store` | `IPoolStore?` | `MemoryPoolStore` | Pool versions. |
| `Identity` | `uint` | 3 | NAME identity number (21 bits). |
| `Manufacturer` | `ushort` | 1407 | NAME manufacturer code (11 bits). |
| `FunctionInstance` | `byte` | 0 | NAME function instance. |
| `PreferredAddress` | `byte` | 0x26 | The address claimed first. |
| `MaskWidth`, `MaskHeight` | `ushort` | 480 | Data mask in pixels. |
| `KeyWidth`, `KeyHeight` | `byte` | 80 | Soft key in pixels. |
| `PhysicalKeys` | `byte` | 6 | Soft keys shown at once. |
| `VirtualKeys` | `byte` | 64 | Soft keys a mask may hold. |
| `NavigationKeys` | `byte` | 0 | |
| `Version` | `byte` | 5 | VT version, 3 to 6. |
| `SmallFonts`, `LargeFonts` | `byte` | 0xFF | Supported font bitfields (Get Text Font Data). |
| `PacketsPerCts` | `byte` | 255 | Frames per clear-to-send and per DPO, received and sent. |
| `StepMilliseconds` | `int` | 5 | Longest gap between steps; an arriving frame or call wakes the terminal at once. |
| `Log` | `Action<int, string>?` | none | The stack's log: level (0 debug .. 4 critical), text. From the terminal's thread and a pool's parsing thread. |
| `LogLevel` | `int` | 2 | Lowest level passed to `Log`. |

## Types

| Type | |
|---|---|
| `record WorkingSetInfo(ulong Name, int Address, PoolState State, int Progress, int? Objects, bool? Selectable)` | One implement. `Progress`: upload percent. `Objects`: once parsed. `Selectable`: from its working set object, once parsed. |
| `enum PoolState { Waiting, Transferring, Parsing, Ready, Failed }` | |
| `record AlarmInfo(ulong Name, int Address, ushort Mask, int Priority, int Signal)` | Priority 0 high, 1 medium, 2 low. Signal 0 highest, 1 medium, 2 lowest, 3 none. |
| `record AudioSignal(ulong Name, int Activations, int FrequencyHz, int OnMilliseconds, int OffMilliseconds)` | `Activations` 0: stop the signal sounding. |
| `record VtImage(uint Id, int Width, int Height, byte[] Rgba)` | 4 bytes a pixel, rows top first. |
| `enum KeyState`, `enum InputResult` | Above. |

## Pool stores

`public interface IPoolStore`. Pool versions, keyed by the implement's NAME and its version label
(7 bytes, or 32 for VT 5 and 6 extended labels; the two lengths are separate versions). Called on
the terminal's thread: keep it quick. An exception counts as a failure and goes to `HandlerFailed`.

| Method | |
|---|---|
| `bool Save(ulong name, ReadOnlySpan<byte> label, ReadOnlySpan<byte> data, bool append)` | Stores a pool or a part of it: the first part with `append` false. False: not stored. |
| `byte[]? Load(ulong name, ReadOnlySpan<byte> label)` | The pool, or null. |
| `IEnumerable<byte[]> List(ulong name, int labelLength)` | The labels stored for this implement of this length. |
| `bool Remove(ulong name, ReadOnlySpan<byte> label)` | |
| `bool RemoveAll(ulong name, int labelLength)` | Every version of this label length. |

`FilePoolStore(string directory)`: `DIRECTORY/NAME/LABEL.iop`, NAME and LABEL in hex; creates the
folder. `MemoryPoolStore()`: in memory, `Count` versions; the default. A store that refuses costs
the implement its connection: upstream's VT client, which most implements use, waits out the
reply and starts over.

## AowLink

`public sealed class AowLink : IDisposable`. The client end of the AOW link protocol version 1
([TRANSPORT.md](TRANSPORT.md) section 5): one TCP connection to a CAN gateway. Its events come on
the link's own thread.

| Member | |
|---|---|
| `AowLink(string host, int port = 2323, string name = "", LinkRole role = LinkRole.Control)` | `name`: shown by the gateway. `Monitor`: receive only (a gateway may refuse it). |
| `void Start()` | Connects on its own thread, and connects again whenever the link drops: at once, then every 250 ms, each attempt giving up after 500 ms (5 s after a version refusal). |
| `void Stop()` | Closes the link and ends its thread. |
| `void Send(int channel, uint id, ReadOnlySpan<byte> data, bool extended)` | A frame to the bus. Any thread, returns at once. Dropped and counted while the link is down; never held for the next link. |
| `bool IsConnected` | |
| `GatewayInfo? Gateway` | The gateway of the current link, or of the last one. |
| `long FramesDropped` | Frames given to `Send` while the link was down. |
| `const int DefaultPort = 2323`, `const byte Version = 1` | |

| Event | |
|---|---|
| `LinkFrameHandler FrameReceived` | `(int channel, uint id, ReadOnlySpan<byte> data, bool extended, uint timeUs)`: `timeUs` is the gateway's receive time on its own clock, wrapping, for spacing only. |
| `Action<GatewayInfo> Connected` | The HELLOs are exchanged. |
| `Action<LinkCloseReason, string> Disconnected` | Down, and why. It is tried again by itself. |
| `Action<BusStatus> StatusReceived` | A bus's state, on connecting, on any change and every second. |
| `Action<double> RoundTrip` | Milliseconds, from each heartbeat. |
| `Action<Exception> HandlerFailed` | A handler threw; the link carries on. |

| Type | |
|---|---|
| `record GatewayInfo(string Name, IReadOnlyList<GatewayChannel> Channels)` | |
| `record GatewayChannel(int Channel, int Bitrate, bool ListenOnly, string Name)` | |
| `record BusStatus(int Channel, BusState State, int TxErrors, int RxErrors, long RxDropped, long TxDropped)` | Drop counts from the start of the link. |
| `enum BusState { ErrorActive, ErrorWarning, ErrorPassive, BusOff, Down }` | |
| `enum LinkCloseReason { Lost, Version, Busy, Timeout, ProtocolError, ShuttingDown }` | `Busy`: the gateway has a control client already. `Timeout`: nothing heard for 1 s. |
| `enum LinkRole { Control, Monitor }` | |

`static void VirtualTerminalLink.Attach(this VirtualTerminal terminal, AowLink link)`: frames from
the link to `Receive`, `Connected` to `LinkUp`, `Disconnected` to `LinkDown`. The terminal's frames
go out by its options: `Send = link.Send`.

## Messages

`VirtualTerminal.Message` (and the C `message` callback): one JSON object per call, UTF-8, `"type"`
first. NAMEs are 16 hex digits. Unknown fields may be added later; ignore them.

### `screen`

The shown implement's mask and soft keys as [draw lists](#the-draw-list). Sent whenever it differs
from the last one sent; always complete.

```json
{"type":"screen","ws":"A00084000C01FFFF","mask":1000,"kind":"data","w":480,"h":480,"bg":"#ffffff",
 "items":[...],
 "keys":{"mask":4000,"w":80,"h":80,"bg":"#c0c0c0","keys":[{"o":5000,"code":5,"bg":"#ff0000","items":[...]},null]}}
```

- `ws` null: no implement is shown (`{"type":"screen","ws":null}`).
- `kind`: `data` or `alarm`; null when the active mask is neither (then `items` is empty and `keys` null).
- An alarm mask adds `priority` and `signal` (as in `alarms`).
- `keys`: null when the mask has no soft key mask. `keys.keys` in order; null keeps an empty place.
  Each key: `o` its id (for `SoftKeyAsync`), `code` its key code, `bg`, `items` in key pixels.

### `edit`

An input field opened (the operator selected it, or the implement did) or closed (`"o":null`).

```json
{"type":"edit","ws":"A0001000AFE00002","o":9000,"kind":"number","value":50,"min":0,"max":100,"offset":0,"scale":0.5,"decimals":1,"text":"25.0"}
{"type":"edit","ws":"...","o":8000,"kind":"string","value":"HELLO","length":10,"chars":"ABC...Z ","only":true}
{"type":"edit","ws":"...","o":7000,"kind":"bool","value":0}
{"type":"edit","ws":"...","o":10000,"kind":"list","value":0,"w":200,"h":20,"options":[[...items...],null]}
{"type":"edit","ws":"...","o":null}
```

- **number**: `value`, `min`, `max` are raw; the operator sees `(raw + offset) * scale` to
  `decimals` places, as `text`.
- **string**: `value` with its padding trimmed; `length` the most characters; `chars` with `only`
  true are the characters allowed, with `only` false the ones refused (absent: any). Stored in the
  field's encoding (ISO 8859-1, or UTF-16 when the pool's value is), padded with spaces.
- **bool**: 0 or 1.
- **list**: each option a draw list of `w` x `h` at 0,0; null an empty place. Enter the index.

The field closes on entry, on escape, when the focus moves, when the mask changes under it, and on
the implement's ESC command. Macros run on selection, deselection, entry, a new value, and escape.

### `workingSets`

```json
{"type":"workingSets","active":"A00084000C01FFFF","list":[
 {"name":"A00084000C01FFFF","address":128,"state":"ready","progress":100,"objects":88,"selectable":true}]}
```

`active` null: none shown. `state`: `waiting`, `transferring` (with `progress` in percent),
`parsing`, `ready`, `failed`. `objects` once parsed; `selectable` once its working set object is
known. Sent when the list, a state, the shown implement, or the progress in whole percent changes.

### `alarms`, `audio`, `volume`

```json
{"type":"alarms","list":[{"ws":"A00084000C01FFFF","address":128,"mask":2000,"priority":0,"signal":1}]}
{"type":"audio","ws":"A00084000C01FFFF","activations":3,"frequency":1000,"on":200,"off":100}
{"type":"volume","ws":"A00084000C01FFFF","percent":80}
```

`alarms` lists every implement whose active mask is an alarm mask, shown or not, whenever that
changes. The terminal doesn't switch implements for an alarm. `audio`: Hz and ms; `activations` 0
stops a sounding signal.

## The draw list

Pixels in the area being drawn (the mask, a key, a list option), origin top left; the painter
scales the whole area. Colours `#rrggbb`, already resolved through the working set's colour
table. Items are painted in order, and each has `t` (its kind) and `o` (the object it came from).

| `t` | Fields | From |
|---|---|---|
| `clip` / `unclip` | `x y w h` | masks, containers, buttons, keys: children never draw outside them |
| `rect` | `x y w h`, `fill`, `line`, `lw`, `sides` (which of the four lines are drawn) | rectangles, backgrounds, buttons, bar graphs |
| `line` | `x1 y1 x2 y2 c lw` (`dash`: the 16 bit pattern when not solid) | lines, meters, ticks |
| `ellipse` | `x y w h`, `kind` (closed, open, segment, section), `a0 a1` (degrees), `fill line lw` | ellipses, meters, arched bar graphs |
| `poly` | `pts` (x, y pairs), `closed`, `fill line lw` | polygons, needles |
| `text` | `x y w h`, `s`, `c`, `bg` (absent: transparent), `fw fh` (font cell), `style` (bold, crossed, underlined, italic, inverted, flash), `hj vj` (0 left/top, 1 middle, 2 right/bottom), `wrap` | output and input strings, numbers, lists |
| `image` | `x y w h`, `img` (a `VtImage` id) | picture graphics, fill patterns |
| `hit` | `x y w h`, `kind` (`button` or `input`), `on` (enabled), `latch` and `latched` (latching buttons), `sel` (has the focus), `open` (being edited) | what the operator can touch on the mask; soft keys are touched by their cell in `keys` |
| `box` | `x y w h`, `label` | an object the renderer does not draw: a labelled box, never dropped |

Fonts are the standard's fixed cells (6 x 8 up to 128 x 192); fill the cell with a monospace face.
Numbers arrive formatted (scale, offset, decimals, exponential, leading zeros, blank zero,
truncation). The reference painter is `painter/vtpaint.js`: `VtImages`, `vtPaintMask`,
`vtPaintKey`, `vtPaintItems`, `vtHit`.

Not drawn yet, so shown as `box`: animations, graphics contexts, scaled graphics, external
objects, VT 6 window masks and key groups.

## The C interface

`native/isoshim.h`, for hosts not in .NET (the C# package is built on it; `AgIsoStack.Interop.IsoShim`
declares it for P/Invoke, callbacks as `[UnmanagedCallersOnly]` function pointers, as iOS AOT
requires). Library `isoshim` (`libisoshim.dylib`, `.so`, `isoshim.dll`; on iOS the static
`libisoshim-all.a`, imported as `__Internal`). `isoshim_version()` is 4.

**Threading.** The stack runs on the thread that calls `isoshim_step` and nowhere else. Every
function except `isoshim_receive` and `isoshim_set_log`, and every callback except the log, belongs
to that thread. `isoshim_receive` may be called from any thread: it queues the frame for the next
step. Call `isoshim_step` every few milliseconds (a multi-frame transfer moves one step per
clear-to-send).

| Function | |
|---|---|
| `int32_t isoshim_vt_start(const isoshim_vt_config *, const isoshim_host *)` | 0 started; -1 bad arguments; -2 already running. Zero config fields take the defaults above. |
| `void isoshim_step(void)` | Queued frames in, timers, frames out, the screen if it changed. |
| `void isoshim_stop(void)` | Safe at any point, including mid-parse; `isoshim_vt_start` may follow. |
| `void isoshim_receive(int32_t channel, uint32_t id, const uint8_t *data, int32_t len, int32_t extended)` | Any thread. The terminal is channel 0. |
| `void isoshim_reply(const uint8_t *data, int32_t len)` | From inside `load` (the pool) or `list` (each label): copied. |
| `void isoshim_set_log(isoshim_log_fn, int32_t level)` | |
| `int32_t isoshim_soft_key(uint16_t id, int32_t state)`, `isoshim_button(...)` | 1: sent. |
| `int32_t isoshim_activate(uint64_t name)` | 1: now shown. |
| `int32_t isoshim_select(uint16_t id, int32_t open)` | 0xFFFF selects nothing. 1: done. |
| `int32_t isoshim_set_number(uint16_t id, uint32_t value)`, `isoshim_set_string(uint16_t id, const char *utf8, int32_t len)` | 1 entered, -1 invalid, 0 not an input. |
| `int32_t isoshim_escape(void)` | 1: a field was open. |
| `isoshim_test_implement`, `isoshim_test_masks`, `isoshim_test_show`, `isoshim_test_send` | The self-test's implement on channel 1. Not for hosts. |

`isoshim_host` holds the callbacks (`send`, `event` and `message` required; `image`, `save`,
`load`, `list`, `remove` optional: a missing store callback means nothing is stored).

| Callback | |
|---|---|
| `send(channel, id, data, len, extended)` | A frame out. It may call `isoshim_receive`. |
| `event(kind, a, b)` | `ISOSHIM_SERVER_ADDRESS` (a: address), `ISOSHIM_POOL_OK` (a: objects, b: address), `ISOSHIM_POOL_BAD` (a: faulty object, b: address), `ISOSHIM_MASK` (a: mask shown, b: address), `ISOSHIM_PARSE_START` (b: address), `ISOSHIM_WS_SHUTDOWN` (a: reason, b: address), `ISOSHIM_ACTIVE` (a: address shown, -1 none). |
| `message(json, len)` | A [message](#messages); the text is valid during the call. |
| `image(id, width, height, rgba)` | |
| `save(name, label, labelLen, data, len, append)` | 1: saved. |
| `load(name, label, labelLen)` | Reply with the pool, return 1; 0: none. |
| `list(name, labelLen)` | One reply per label. |
| `remove(name, label, labelLen)` | `label` null: every version of `labelLen`. 1: removed. |
| `log(level, text)` | Can come from the pool parsing thread. |

## Design

The page is the only UI, so the pool is flattened **natively** into a draw list that C# relays
untouched and the page paints in order: one renderer, in C++ and tested here against real pools,
with a painter that knows nothing of ISOBUS. Passing the object tree through C, C# and into the
page instead would put the renderer in JavaScript and push about 40 object types through two
boundaries. The terminal runs the stack on one thread with no hardware layer: the host is the bus
([PLAN.md](PLAN.md) has the decisions and their reasons).

`VT3TestPool.iop` (in `dotnet/`) is upstream AgIsoStack++'s example pool, MIT, embedded in the
test apps.
