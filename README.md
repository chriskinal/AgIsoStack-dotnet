# AgIsoStack-dotnet

[AgIsoStack++](https://github.com/Open-Agriculture/AgIsoStack-plus-plus) for .NET: a small C
interface over the stack (`native/`), called from C# by P/Invoke (`dotnet/`), on macOS, iOS,
Android, Linux and Windows. The caller carries the CAN frames, so the bus can be anything: a
SocketCAN interface, a USB adapter, or a CAN-to-network gateway over TCP or Wi-Fi.

**Status:** a virtual terminal for AgOpenWeb's VT card ([docs/API.md](docs/API.md)): a
configuration, stored pool versions, the connected implements, the active screen as a draw list
that a page paints ([painter/vtpaint.js](painter/vtpaint.js)), soft keys, buttons, input fields,
alarms and audio; in C# one class, `VirtualTerminal`, packaged with its native libraries
(`AgIsoStack.VirtualTerminal`). Built against upstream HEAD. The first surface (the spike) passed
on a Mac, a Galaxy Tab S7 FE (Android 14) and an iPad Pro (iPadOS 17.7, full AOT), in process and
against a real implement over Wi-Fi with delay, loss and link cuts
([docs/RESULTS.md](docs/RESULTS.md)); the current one so far on the Mac. What comes next is in
[docs/PLAN.md](docs/PLAN.md).

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
- **255 frames per clear-to-send** by default (the config's `packets_per_cts`): with the stack's
  own 16, an upload is held to about half the bus rate in process and pays a round trip per 16
  frames over a network link.

## Build

```sh
native/build.sh mac        # native/build-mac/libisoshim.dylib (arm64 and x86_64)
native/build.sh ios        # native/build-ios/libisoshim-all.a (the shim and the stack, one archive)
native/build.sh android    # native/build-android-ABI/libisoshim.so (arm64-v8a, armeabi-v7a, x86_64)
native/build.sh linux      # native/build-linux-RID/libisoshim.so (x64, arm64; on a Mac in Docker)
native/build.sh windows    # native/build-windows/isoshim.dll (x64; llvm-mingw in Docker)
cd dotnet/AgIsoStack.VirtualTerminal && dotnet pack -c Release -o OUT   # the package
```

The package (`net10.0`, `net10.0-ios`, `net10.0-android`) takes each native library that is
built: macOS under `runtimes/osx`, Linux under `runtimes/linux-x64` and `linux-arm64`, Windows
under `runtimes/win-x64`, iOS under `runtimes/ios-arm64` with a `buildTransitive` target that
links it into the app, Android inside the `.aar`. All of them build on the Mac: Linux and Windows
in Docker (OrbStack). Linux is built on Debian 12, so it loads on glibc 2.36 (Raspberry Pi OS
bookworm) and later. Windows is cross-compiled with llvm-mingw, the C++ runtime linked in: the DLL
needs only the Windows C runtime (UCRT, in Windows 10 and later).

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

## Use

```csharp
using AgIsoStack;

var link = new CanLink("192.168.4.1", name: "AgOpenWeb");             // a CAN link gateway on the bus
var vt = new VirtualTerminal(new VirtualTerminalOptions
{
    Send = link.Send,                                                  // the terminal's frames out
    Store = new FilePoolStore(Path.Combine(appData, "pools")),         // stored pool versions
});
vt.Attach(link);                                  // frames in; keys held across a link drop released
vt.Message += json => page.Post(json);           // screens, editors, working sets, alarms: as JSON
vt.Image += image => page.PostImage(image);      // pictures, RGBA, before the screens that use them
vt.AlarmsChanged += alarms => ShowOverAnyCard(alarms);
link.Disconnected += (why, text) => ShowBusGone(why);   // reconnects on its own
vt.Start();
link.Start();
// The page's taps and entries:
await vt.SoftKeyAsync(5000, KeyState.Pressed);
await vt.SelectAsync(9000, open: true);
var result = await vt.SetNumberAsync(9000, 80);   // Entered, Invalid, NotAnInput
link.Stop();
vt.Stop();
```

The terminal has one thread of its own: it runs the stack and raises every event, in order;
calls come from any thread. One terminal per process. Details in [docs/API.md](docs/API.md).
Without a gateway (SocketCAN, a USB adapter), `Send` and `vt.Receive(id, data, extended,
channel)` carry the frames instead; `vt.LinkDown()` and `vt.LinkUp()` are for any link that can
drop.

## Test

**Self-test** (no bus): `IsoShimSpike.RunAll` puts a terminal and an implement in one process,
C# carrying every frame between them. Eight runs: an upload of upstream's VT3 example pool
(150 kB) at 16 and at 255 frames per clear-to-send, each ending in a soft key round trip and
storing the pool; a stop the moment the pool starts parsing; a full run after it; and one where
the implement loads the stored pool instead of uploading. It checks that both masks were drawn
and that every callback came on the stepping thread. The sixth uploads `testpools/inputs.iop`
(ours, written by `testpools/inputs.py`: every kind of input field) and enters values in each,
checking what the implement hears, what is refused and what the screen shows. The seventh uploads
`testpools/drawing.iop` and checks the alarms message as its alarm mask comes and goes, and that
the implement's audio and volume commands reach the host. The eighth drives the C# class,
`VirtualTerminal`, as a host would: two sessions with one store (the second loads the pool
the first stored), input results, a call from inside a handler, stop from either thread, and
every event on the terminal's one thread. The ninth puts `CanLink` between the terminal and the
implement, through a scripted gateway on loopback: the hello, the pool over the link, a gateway
gone silent (dropped after 1 s, back at once), refusals while busy, and a soft key held when the
link went, aborted when it is back.

With a pool file, `screens` uploads that pool instead and has the test implement show each of its
data and alarm masks in turn. `bench/pools.sh` does that for every pool in `testpools/`: our
drawing and input pools (their screens committed in `testpools/expected/`, so a renderer change
shows in `git diff`), upstream's VT3 pool, and other people's pools kept locally in the ignored
`testpools/local/` (PoolEdit's twelve examples, the seeder). It reports what changed and prints
the address of one page showing every screen ([testpools/README.md](testpools/README.md)).

```sh
cd dotnet/console && dotnet run -c Release
cd dotnet/console && dotnet run -c Release -- screens ../../painter/out   # then open painter/index.html
cd dotnet/console && dotnet run -c Release -- screens ../../painter/out POOL.iop   # every mask of any pool
bench/pools.sh               # every test pool: what changed, and one page with every screen
bench/selftest.sh            # on the iPad and the Android tablet (bench/devices.env)
bench/selftest.sh linux windows   # Linux arm64 and x64 in Docker; Windows x64 in the Parallels VM
```

**Bench** (`bench/`): the terminal on a tablet or the Mac, a Linux box with two CAN interfaces
wired together as the gateway. `canlink_gateway.py`, the reference CAN link gateway
(docs/TRANSPORT.md section 5), carries one interface's frames over TCP to the terminal; upstream's
VT example implement runs on the other (`bench/implement/build.sh`). `netem.sh` delays, drops or
cuts the link's traffic in both directions. `canlink_check.py` checks any gateway against the
protocol. `local.sh` runs the whole bench in one Linux container on a virtual CAN bus, with link
cuts if asked: no hardware.

```sh
cp bench/devices.env.example bench/devices.env   # the gateway, the tablets
bench/local.sh VT3Example255 5 1.5                # no hardware: one run, the link cut for 1.5 s at 5 s
CHECK=1 bench/local.sh                           # and the gateway's conformance check
bench/deploy.sh                                  # gateway, checker, capture, netem, implements to the CM4
bench/run.sh ios first VT3Example255             # one run, everything in bench/out/first.txt
bench/sweep.sh ios VT3Example255 3 d200 "delay 100ms 30ms"
bench/sweep.sh ios VT3Example255 3 cut5 none 8 5 # a 5 s link cut, 8 s after the implement starts
```

`bench/run.sh` takes `mac`, `android` or `ios`. The gateway's CAN interfaces are `hatcan2` (the link)
and `hatcan3` (implement) unless `RELAY_IF` and `IMPL_IF` say otherwise.
