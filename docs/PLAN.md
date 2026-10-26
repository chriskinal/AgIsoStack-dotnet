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
- **Several terminals in one stack** (2026-10-25): a cab can have two or more VTs, each an
  implement's chosen display (a sprayer on one, its boom levelling on another, both worked at
  once). The standard way is several terminals on the bus, each with its own NAME (function
  instance) and address, and each implement choosing one; so the stack runs one to four, each
  with its own screen, events and calls (`VirtualTerminalStack.Terminals`). Not several screens of
  one terminal: one terminal has one active implement, and implements may ignore input while not
  active. One stack per process still (the network manager is one); the terminals share the
  stepping thread, the bus, and the pool store.
- **MIT**, so it can go upstream unchanged. The user intends to offer it to Open-Agriculture.

## Known costs

- **A native crash takes the application down.** A bug in the stack's pool parsing would end
  AgOpenWeb itself, not a separate service. Open: accept it, or guard it (check a pool before it
  goes live, or parse in a child process on platforms that allow one).
- **The terminal is on the bus only while the application is.** If the app restarts or the
  tablet sleeps, the implement loses its terminal (and resets its connection after 3 s).
- **A build per platform**: linux-arm64 and x64, win-x64, osx-arm64, Android (NDK), iOS (static,
  linked into the app). The stack built unchanged for all of them on the first try. Windows is
  built with llvm-mingw, not MSVC: a cross-compiler in Docker, and the DLL only exports C
  functions, so the C++ runtime it uses does not matter to the caller. Windows on ARM runs the
  x64 DLL under emulation (as AgOpenWeb's x64 build); a win-arm64 build is one more compiler
  name if it is ever wanted.
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
   with the native libraries per runtime. Done: config, storage, working sets, the draw list,
   soft keys and buttons, input fields (select, numbers, strings, booleans, lists, escape),
   alarms and audio, and the C# package (`AgIsoStack.VirtualTerminal`: macOS, iOS, three
   Android ABIs, Linux x64 and arm64, Windows x64; the self-test passes on Linux and Windows,
   and a fresh app using only the package runs on each). The self-test passes on the tablets and
   the bench passes through the class on the Mac and both tablets (RESULTS.md). Next:
   AgOpenWeb's side (its VT card), and the store-version gap in upstream's client below. The terminal is AgOpenWeb's VT card, in its backend
   (it replaces the `aow-vtd` daemon once planned): any implement, ISOBUS or DIY, talks to
   AgOpenWeb directly (decided 2026-10-24).
2. **The client side**, for implements in .NET (the stack's VT client and task controller
   client), on the same one-thread model.
3. **Tests in CI**: the self-test on Linux and macOS runners; the stop-while-parsing run is the
   regression for the shutdown crash. Until then every desktop platform runs on the Mac:
   `bench/selftest.sh linux windows` (Docker for Linux, the Parallels VM for Windows).
4. **Upstream.** The managed working set's unjoined parse thread is reported, with the fix
   (its destructor joins) and the never-called `transferred_object_pool_parse_start`:
   [#802](https://github.com/Open-Agriculture/AgIsoStack-plus-plus/issues/802), 2026-10-24.
   Fixed upstream by #805 (e932041, 2026-10-25), in our pin since 2026-10-25: `Terminal::~Terminal`
   no longer joins; built against 77b8dc9 without it, the stop-while-parsing run aborts (a
   joinable thread destroyed), against e932041 it passes on the Mac and both tablets. The
   never-called parse start hook is still open: `step()` still looks for a parse running.
   In the pin since 2026-10-26 (7b77127): #801's `object_pool_deleted`, which the shim uses to drop
   a deleted pool's pictures and close its open field without a VT ESC (the input self-test fails
   without it), and #809, a Store Version calls `delete_version` before `save_version` (the shim
   already told the host to start afresh). Then offer the wrapper.
5. **Transport** (docs/TRANSPORT.md): our own gateway protocol, not GVRET (decided 2026-10-24).
   Version 1 approved and built the same day: `AowLink` in the package, the reference gateway
   and a conformance checker in `bench/`, the bench on it (RESULTS.md: clean runs on all three
   devices, cuts mid-upload recovered). The HAT's gateway, `aow-linkd`, built in AgOpenWeb-HAT
   2026-10-25 (0.2.24) and benched; its status on the HAT's ISOBUS page in 0.2.25. Next: an
   ESP32 gateway, a pairing key. An adapter on the host itself: slcan first (`SlcanLink`, 2026-10-25:
   desktop serial ports and Android USB serial; chosen over gs_usb, PEAK and SocketCAN for the first
   round); iOS can't reach USB adapters.
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
| 5 | Control Audio Signal and Set Audio Volume are acknowledged and dropped, with nothing to override. The wrapper hears them from its own listener and passes them to the host | alarms |
| 6 | Every Change Numeric Value repaints, changed or not (upstream #796) | drawing |
| 7 | A long ETP stream during an upload is drained in one pass, starving other work (espVT: at most 32 per pass) | not seen on the bench |
| 8 | The implement's ESC command (0x92) is not handled: an open field is not closed and no answer is sent. The wrapper answers it from its own listener (found here, 2026-10-24) | input |
| 9 | Sending a second multi-frame message to an implement while the last one is closing fails (one session per pair); the server's own sends do not retry. The wrapper queues the operator's messages (`Terminal::post`) (found here, 2026-10-24) | input |
| 11 | The server takes every implement message addressed to any of the process's own control functions (the network manager's any-function callbacks), not only its own: with two terminals in one process each answers and takes on the other's implements, and neither screen comes up. The wrapper swaps upstream's listener for one that passes only messages to its own terminal or to everyone (`Terminal::listen`; the two-terminal self-test fails without it) (found here, 2026-10-25) | several terminals |
| 12 | Identify VT is handled only from a connected implement (in the connection-dependent messages), and `identify_vt` only logs; an implement asks while the operator is choosing a terminal, before it connects, and gets a NACK. The wrapper answers it for every implement from its own listener and passes `identify` to the host (found here, 2026-10-25) | several terminals |
| 10 | The pool parser checks an alarm mask's acoustic signal but never stores it (`set_signal_priority` is not called), so every alarm mask reports signal 0, the highest (found here, 2026-10-24; fixed upstream with the enum, which numbered lowest 3 and none 4: [#803](https://github.com/Open-Agriculture/AgIsoStack-plus-plus/pull/803), merged 2026-10-24, in the pin since) | alarms |

**In the VT client** (the implements' side, which aow-hatd and the example implement use):
when the terminal answers Store Version with an error, the client logs it but stays waiting for
the answer, times out after 3 s and resets the connection, then uploads again: a terminal that
cannot store pools never keeps an upstream implement (found on the bench, 2026-10-24; storing is
optional, so an error should leave it connected). The C# class keeps pools in memory when the host
gives no store; the raw interface with no save callback still answers "not stored". Fixed on the
user's fork, branch `vt-client-store-version-error` (chriskinal/AgIsoStack-plus-plus, 853083b:
the client goes on to Connected after any Store Version error; a test fails without it),
offered upstream as [#804](https://github.com/Open-Agriculture/AgIsoStack-plus-plus/pull/804), merged 2026-10-25 (93a8623). Implements already built keep the old behaviour, so the in-memory default stays.

**In the transport layer, threaded use** (found here 2026-10-25, chasing a double request-to-send
on the bench; still in upstream HEAD 7b77127): `TransportProtocolManager::protocol_transmit_message`
adds the new session under `activeSessionsMutex`, releases it, then runs `update_state_machine` on
it in the caller's thread; the stack's own thread can meanwhile run `update()` over the same
session. Both see `SendRequestToSend` and both send the RTS, back to back. The receiver overwrites
its session and answers twice, the sender aborts on the second CTS (reason 4), the transfer fails.
`ExtendedTransportProtocolManager` does the same and adds the session to `activeSessions` with no
lock at all while `update()` walks it. A stress program (`bench/tp_race.cpp`: two control functions on vcan, the
application thread sending 100-byte TP messages while the hardware thread updates) doubled 19
RTSs in 4,000, each a failed transfer; with the direct call removed (the RTS left to `update()`),
none in 4,000, at about 3.5 ms a message. It bites any implement that sends from its own thread:
`aow-hatd` (its ISOBUS thread) and upstream's examples (the VT client's worker), seen as a
repeated upload of their pools. The wrapper runs the stack on one thread and never starts it.
Reported 2026-10-25 as [#807](https://github.com/Open-Agriculture/AgIsoStack-plus-plus/issues/807),
fixed in [#808](https://github.com/Open-Agriculture/AgIsoStack-plus-plus/pull/808) (fork branch
`transport-transmit-race`): the first frame is checked again and sent under the sessions lock, so
the immediate send stays and one thread sends it; ETP gets TP's lock on its session list. Two
threaded unit tests fail without it (at least one in each of 5 runs) and pass with it; `tp_race`
on vcan: none in 4,000, at the same speed. AgOpenWeb-HAT carries it as a patch from 0.2.27; the
bench's example implements are built without it and can still double an RTS.

Two more espVT found are not ours: the single-producer transmit queue (PR #793; the wrapper does
not use that queue) and the unlocked working-set list (the wrapper runs the server on one
thread). Also reported by us: #802 (above).
