# AgIsoStack-dotnet

[AgIsoStack++](https://github.com/Open-Agriculture/AgIsoStack-plus-plus) for .NET: a small C
interface over the stack (`native/`), called from C# by P/Invoke (`dotnet/`), on macOS, iOS,
Android, Linux and Windows. The caller carries the CAN frames, so the bus can be anything: a
SocketCAN interface, a USB adapter, or a CAN-to-network gateway over TCP or Wi-Fi.

**Status:** a working spike becoming a library ([docs/API.md](docs/API.md)). The C interface runs
a virtual terminal server with a configuration, storage of pool versions through callbacks, the
working sets as JSON, the active screen as a draw list that a page paints
([painter/vtpaint.js](painter/vtpaint.js)), soft keys, buttons and input fields, and (for the self-test) an
implement beside it. It is built
against upstream HEAD and has passed on a Mac, a Galaxy Tab S7 FE (Android 14) and an iPad Pro
(iPadOS 17.7, full AOT), in process and against a real implement over Wi-Fi with delay, loss and
link cuts ([docs/RESULTS.md](docs/RESULTS.md)). What comes next is in [docs/PLAN.md](docs/PLAN.md):
the real API surface, then the offer upstream.

MIT, like AgIsoStack++.

## How it works

- **One thread.** The stack runs on the thread that calls `isoshim_step` and nowhere else. The
  shim does not use the stack's hardware layer: it supplies the four hooks the stack calls to
  reach hardware (`can_hardware_abstraction.hpp`), so there are no stack threads and no
  transmit-queue races. `isoshim_receive` is the one call that is safe from any thread (it
  queues the frame); every callback except the log runs on the stepping thread.
- **Step often.** A multi-frame transfer moves one step per clear-to-send, so the host steps
  every few milliseconds on a thread of its own (2 ms in the bench), not from a UI frame timer.
- **Stop at any point.** The terminal joins any object pool parse still running before it is
  destroyed (upstream's managed working set does not; see docs/RESULTS.md).
- **255 frames per clear-to-send** (`isoshim_set_packets_per_cts`): with the stack's default of 16,
  an upload is held to about half the bus rate in process and pays a round trip per 16 frames
  over a network link.

## Build

```sh
native/build.sh mac        # native/build-mac/libisoshim.dylib
native/build.sh ios        # native/build-ios/libisoshim-all.a (the shim and the stack, one archive)
native/build.sh android    # native/build-android/libisoshim.so (arm64-v8a; $ANDROID_NDK or the newest NDK)
native/build.sh linux      # native/build-linux/libisoshim.so
```

The stack is fetched at the commit pinned in `native/CMakeLists.txt`;
`-DISOSHIM_STACK_DIR=/path/to/AgIsoStack-plus-plus` builds against a local checkout. Needs CMake
3.28, Ninja, and for the apps the .NET 10 SDK with the `ios` and `android` workloads.

```sh
cd dotnet/console && dotnet run -c Release                  # self-test on the desktop
cd dotnet/ios && dotnet build -c Release -p:DevTeam=XXXXXXXXXX
cd dotnet/android && dotnet build -c Release
```

The iOS app needs an Apple team to sign with: `-p:DevTeam=`, or a `dotnet/Local.props` (not
committed) with `<Project><PropertyGroup><DevTeam>…</DevTeam></PropertyGroup></Project>`.
With Xcode newer than the .NET iOS workload supports, add `-p:ValidateXcodeVersion=false`.
The test apps are `com.agopenweb.isostack-tests` (iOS) and `com.agopenweb.isostack_tests`
(Android).

## Test

**Self-test** (no bus): `IsoShimSpike.RunAll` puts a terminal and an implement in one process,
C# carrying every frame between them. Six runs: an upload of upstream's VT3 example pool
(150 kB) at 16 and at 255 frames per clear-to-send, each ending in a soft key round trip and
storing the pool; a stop the moment the pool starts parsing; a full run after it; and one where
the implement loads the stored pool instead of uploading. It checks that both masks were drawn
and that every callback came on the stepping thread. The sixth uploads `dotnet/InputTestPool.iop`
(ours, written by `dotnet/InputTestPool.py`: every kind of input field) and enters values in each,
checking what the implement hears, what is refused and what the screen shows.

With a pool file, `screens` uploads that pool instead and has the test implement show each of its
data and alarm masks in turn, for a look at the renderer on other pools: isobus-implement-sim's
`pools/base.iop` (the stack's seeder example, 206 objects, six masks with a meter, an input
list, polygons and ellipses) draws completely.

```sh
cd dotnet/console && dotnet run -c Release
cd dotnet/console && dotnet run -c Release -- screens ../../painter/out   # then open painter/index.html
cd dotnet/console && dotnet run -c Release -- screens ../../painter/out POOL.iop   # every mask of any pool
bench/selftest.sh            # on the iPad and the Android tablet (bench/devices.env)
```

**Bench** (`bench/`): the terminal on a tablet or the Mac, a Linux box with two CAN interfaces
wired together as the gateway. `can_tcp_relay.py` carries one interface's frames over TCP to the
terminal; upstream's VT example implement runs on the other (`bench/implement/build.sh`).
`netem.sh` delays, drops or cuts the relay's traffic in both directions.

```sh
cp bench/devices.env.example bench/devices.env   # the gateway, the tablets
bench/deploy.sh                                  # relay, capture, netem and implements to the gateway
bench/run.sh ios first VT3Example255             # one run, everything in bench/out/first.txt
bench/sweep.sh ios VT3Example255 3 d200 "delay 100ms 30ms"
bench/sweep.sh ios VT3Example255 3 cut5 none 8 5 # a 5 s link cut, 8 s after the implement starts
```

`bench/run.sh` takes `mac`, `android` or `ios`. The gateway's CAN interfaces are `hatcan2` (relay)
and `hatcan3` (implement) unless `RELAY_IF` and `IMPL_IF` say otherwise.
