# Plan

## Goal

AgIsoStack++ usable from .NET on every platform .NET runs on, starting with what AgOpenWeb needs
(a virtual terminal inside the app, its frames carried over the network from a CAN gateway), and
offered to the Open-Agriculture project once it is a library rather than a spike.

## Decisions so far

- **A C interface and P/Invoke**, not C++/CLI (Windows only) and not a generated binding.
- **The caller is the bus.** Frames in through `isoshim_receive`, out through a callback. The
  wrapper never opens hardware; the host owns SocketCAN, a USB adapter or a network gateway.
- **One thread.** The shim supplies the stack's hardware hooks and runs everything on the
  stepping thread. No stack threads, no hardware layer queues.
- **Upstream HEAD.** The pin in `native/CMakeLists.txt` follows upstream; upstream fixes not yet
  merged ride as patches until they are (none needed by the shim today).
- **MIT**, so it can go upstream unchanged.

## Next

1. **The real API.** The current surface is a test harness: a fixed terminal NAME, the implement
   under test picked by function code, one soft key. A host needs the VT server's side in full:
   its configuration (memory, soft keys, data mask size, fonts, version), the managed working
   sets and their object trees (to draw them), the events (mask and soft key mask changes,
   repaint, focus, audio, shutdowns), the operator's input (soft keys, buttons, numeric and
   string input), and storing pools (versions) through callbacks. Decide how much of the object
   tree crosses the boundary (C structs per object, or a draw list built on the native side).
   Also names: `isoshim_` becomes the library's prefix, and the C# side a NuGet-shaped package
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
5. **Gateway**: a frame age limit after a link cut (docs/RESULTS.md); the record format of
   `bench/can_tcp_relay.py` is the starting point of a gateway protocol, which belongs with the
   gateways rather than here.
