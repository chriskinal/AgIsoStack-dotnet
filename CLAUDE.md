# CLAUDE.md - AgIsoStack-dotnet

AgIsoStack++ for .NET: a C interface (`native/isoshim.h`, `native/isoshim.cpp`) called by
P/Invoke from C# (`dotnet/`). README.md says how to build and test; docs/PLAN.md what comes next;
docs/RESULTS.md what the bench has shown.

## Rules

- **One thread.** Everything in the shim runs on the thread that calls `isoshim_step`;
  `isoshim_receive` only queues. Never link the stack's HardwareIntegration library or start a
  stack thread (`initialize(true)`): the shim supplies `send_can_message_frame_to_hardware` and
  drives `receive_`, `on_transmit_` and `periodic_update_from_hardware` itself.
- **Anything that owns a thread is joined before it is destroyed** (the VT server's pool
  parsing threads: `Terminal::~Terminal`). The self-test's stop-while-parsing run is the check;
  after changing teardown, break it on purpose once and see that run crash.
- **The pin follows upstream HEAD** (`AGISOSTACK_GIT_TAG` in `native/CMakeLists.txt`). Move it,
  then run the self-test on the Mac, the Android tablet and the iPad. Do not patch the fetched
  tree for the shim; upstream fixes not yet merged go in as patches with a note to drop them.
- **MIT.** Nothing GPL comes in (AgOpenWeb-HAT and the Teensy firmware are GPL).
- **Test app IDs:** `com.agopenweb.isostack-tests` (iOS), `com.agopenweb.isostack_tests`
  (Android). Never sign a test as `com.agopenweb.ios`: that replaces the installed AgOpenWeb app.
- Device IDs, the Apple team and the gateway's address stay out of the repo: `bench/devices.env`
  and `dotnet/Local.props` (both ignored).

## Bench

The dev gateway is AgOpenWeb-HAT's CM4 (CAN2 `hatcan2` wired to CAN3 `hatcan3`); its hardware
watchdog is live while `aow-hatd` runs (stop the service, never kill the process). The bench
leaves `aow-hatd` running. The CM4's network setup (its access point profile, the static
address on its Ethernet) is changed only with the user's say-so. The test implements are
built on Linux arm64 (`bench/implement/build.sh`, in AgOpenWeb-HAT's `aow-hat-dev` container on
the Mac). Run scripts with bash: zsh does not split `$ADB`-style variables.
