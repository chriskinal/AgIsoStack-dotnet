# Plan

## Goal

AgIsoStack++ usable from .NET on every platform .NET runs on, starting with what AgOpenWeb needs
(a virtual terminal inside the app, its frames carried over the network from a CAN gateway), and
offered to the Open-Agriculture project once it is a library rather than a spike.

## Decisions so far

- **Wrap the C++ stack; do not port it.** AgOpenWeb wants a terminal in C#, and no C# ISOBUS stack
  exists. A port (MIT allows it) or a rewrite would stop receiving upstream's fixes: the VT
  server changed about 20 times in the month before 2026-10-22. A wrapper keeps them, at the cost
  of a native build per platform and native crashes (below).
- **A C interface and P/Invoke**, not C++/CLI (a mixed-mode .NET DLL needs Microsoft's compiler and
  runs only on Windows; AgOpenWeb ships for Linux, macOS, Android and iOS too) and not a
  generated binding. Callbacks are C function pointers to static C# methods marked
  `[UnmanagedCallersOnly]` (required under iOS AOT; it works there).
- **The caller is the bus.** Frames in through `isoshim_receive`, out through a callback. The
  wrapper never opens hardware; the host owns SocketCAN, a USB adapter or a network gateway
  (docs/TRANSPORT.md).
- **One thread.** The shim supplies the stack's hardware hooks and runs everything on the
  stepping thread. No stack threads, no hardware layer queues. Adopted 2026-10-24 after the
  stop crash (docs/RESULTS.md); v1 used the stack's hardware layer and its thread.
- **The screen leaves as a draw list built natively** (2026-10-24): AgOpenWeb's only UI is its
  web page, so C# never draws; the C++ side flattens the pool into primitives, C# relays them and
  the page paints them (docs/API.md). The object tree does not cross the boundary.
- **255 frames per clear-to-send and per DPO** on our side; the stack's default of 16 halves an
  in-process upload and pays a link round trip per 16 frames.
- **Upstream HEAD.** The pin in `native/CMakeLists.txt` follows upstream; upstream fixes not yet
  merged ride as patches until they are (none needed by the shim today).
- **MIT**, so it can go upstream unchanged. The user intends to offer it to Open-Agriculture.

## Known costs

- **A native crash takes the application down.** A bug in the stack's pool parsing would end
  AgOpenWeb itself, not a separate service. Open: accept it, or guard it (check a pool before it
  goes live, or parse in a child process on platforms that allow one).
- **The terminal is on the bus only while the application is.** If the app restarts or the
  tablet sleeps, the implement loses its terminal (and resets its connection after 3 s).
- **A build per platform**: linux-arm64 and x64, win-x64, osx-arm64, Android (NDK), iOS (static,
  linked into the app). The stack built unchanged for all of them on the first try.
- **The host must step often**: a multi-frame transfer moves one step per clear-to-send, so the
  host gives the stack its own thread stepping every few milliseconds.

## Next

1. **The real API** (docs/API.md, in progress on the `api` branch). The spike's surface was a test harness: a fixed terminal NAME, the implement
   under test picked by function code, one soft key. A host needs the VT server's side in full:
   its configuration (memory, soft keys, data mask size, fonts, version), the managed working
   sets and their object trees (to draw them), the events (mask and soft key mask changes,
   repaint, focus, audio, shutdowns), the operator's input (soft keys, buttons, numeric and
   string input), and storing pools (versions) through callbacks. Decided: a draw list built on
   the native side (above). Also names: `isoshim_` becomes the library's prefix, and the C# side a NuGet-shaped package
   with the native libraries per runtime.
2. **The client side**, for implements in .NET (the stack's VT client and task controller
   client), on the same one-thread model.
3. **Tests in CI**: the self-test on Linux and macOS runners; the stop-while-parsing run is the
   regression for the shutdown crash.
4. **Upstream.** The managed working set's unjoined parse thread is reported, with the fix
   (its destructor joins) and the never-called `transferred_object_pool_parse_start`:
   [#802](https://github.com/Open-Agriculture/AgIsoStack-plus-plus/issues/802), 2026-10-24.
   Keep `Terminal::~Terminal`'s join until a pin carries the fix (harmless after it). Then offer
   the wrapper.
5. **Transport** (docs/TRANSPORT.md): settle the gateway protocol (our own or GVRET with
   additions), with hello, heartbeat, bus status and a stale-frame limit; a reference gateway
   (the bench relay grown up, in C++ for Linux) and an ESP32 one; test disconnect and reconnect
   mid-upload. The HAT's gateway service is built in AgOpenWeb-HAT against this protocol.
6. **The terminal in AgOpenWeb** is the app's work and plan, in that repo (not written yet). What
   it needs from here is item 1. Rules agreed for it so far: a press is always followed by a
   release (also across a link cut); one operator at a time; an unsupported object is drawn as a
   labelled box, never dropped; a bad pool is refused; alarms reach the operator with the
   terminal closed; stored pools keyed by the implement's full 64-bit NAME and version label;
   480 x 480 to start (espVT: an implement fills more only if it scales its own pool).

## The drawing boundary: a starting design

Exposing the stack's object classes (about 8,600 lines) field by field through C would be
miserable. The design written for the first terminal plan (AgOpenWeb-HAT `docs/PLAN_VT.md`,
2026-10-13, in that repo's history) flattens on the native side instead, which keeps the C
interface small: frames in and out, a draw list and patches, input back, storage callbacks,
status.

- **Draw list** for the active screen: rectangles, lines, ellipses, polygons, text, images and
  tappable regions, each with the id of the object it came from. Positions already resolved
  (containers, pointers, hidden objects, enabled state), so the host paints in order and nothing
  else. The server's repaint event is per implement, not per object, so changes are found by
  comparing draw lists.
- **Out:** `screen` (on connect, a screen change, another implement active), `patch` (items
  changed, by id), `softkeys`, `image` (pixels once, then referred to by id), `alarm` (raised or
  cleared, priority, sound), `implements` (the list changed).
- **Back:** `press` and `release` by id, `softkey`, `value` (number or text for an input),
  `select` (focus an input), `activate` (which implement is shown), `alarmAck`.
- Unsupported objects become labelled boxes. Fonts: the standard's are fixed-size bitmap cells;
  espVT draws every size with one scaled monospace font. Reference renderers: AgIsoVirtualTerminal
  (GPL-3, desktop, same server class: its rules can be read, not copied into MIT code) and espVT
  (MIT, about 15 object types in about 3,000 lines).

Chosen on 2026-10-24 (docs/API.md has the format as built); the alternative, C structs per object
with the drawing elsewhere, was set aside.

## Upstream gaps in the VT server

Found by espVT (<https://github.com/MatNav1/espVT>, a terminal on an ESP32-S3 with its own copy
of the stack, read 2026-10-22) and still open upstream on 2026-10-19. Each wants its own
upstream pull request with a test; the wrapper's terminal will meet them.

| # | Gap | Phase it bites |
|---|---|---|
| 1 | After the terminal restarts, an implement carries on with maintenance messages without the init bit, and the server never takes it on (espVT: accept it, load its stored pool if there is one version) | stored pools |
| 2 | Status messages start at once after a restart, so an implement never sees the terminal go away (espVT: no status for the first 5 s) | stored pools |
| 3 | Changing the active mask does not run the pool's On Show and On Hide macros | drawing |
| 4 | `get_object_by_id` uses `operator[]`, so looking up a missing id adds an empty entry | drawing |
| 5 | Control Audio Signal and Set Audio Volume are acknowledged and dropped, with nothing to override | alarms |
| 6 | Every Change Numeric Value repaints, changed or not (upstream #796) | drawing |
| 7 | A long ETP stream during an upload is drained in one pass, starving other work (espVT: at most 32 per pass) | not seen on the bench |
| 8 | The implement's ESC command (0x92) is not handled: an open field is not closed and no VT ESC message is sent back (found here, 2026-10-24) | input |
| 9 | Sending a second multi-frame message to an implement while the last one is closing fails (one session per pair); the server's own sends do not retry. The wrapper queues the operator's messages (`Terminal::post`) (found here, 2026-10-24) | input |

Two more espVT found are not ours: the single-producer transmit queue (PR #793; the wrapper does
not use that queue) and the unlocked working-set list (the wrapper runs the server on one
thread). Also reported by us: #802 (above).
