# CLAUDE.md - SharpIsoTerm

An ISOBUS virtual terminal for .NET, built on AgIsoStack++ (not part of that project; formerly
named AgIsoStack-dotnet, renamed 2026-10-26 so it isn't taken for one): a C interface (`native/isoshim.h`, `native/isoshim.cpp`) called by
P/Invoke from C# (`dotnet/`), and the work on carrying CAN frames between a gateway and the
stack over a network. Started 2026-10-24 out of the AgOpenWeb-HAT work (the user moved it here
to continue it in its own repo).

**Read first:**
- **[README.md](README.md)** - build and test
- **[docs/GUIDE.md](docs/GUIDE.md)** and **[docs/API.md](docs/API.md)** - for developers using the
  library: the integration guide and the reference (with [llms.txt](llms.txt) for AI assistants);
  keep them in step with the code
- **[docs/PLAN.md](docs/PLAN.md)** - decisions and why, known costs, what is next, the drawing
  boundary, upstream gaps
- **[docs/TRANSPORT.md](docs/TRANSPORT.md)** - the gateway link: why TCP, what the protocol needs,
  the AOW link protocol (ours, not GVRET), the timers, open questions
- **[docs/BENCH.md](docs/BENCH.md)** - the dev bench: hardware, networks and how to put them
  back, measuring, gotchas
- **[docs/RESULTS.md](docs/RESULTS.md)** - every measurement so far, and open observations

## Related repositories

| Repo | Local path | Role |
|---|---|---|
| AgIsoStack++ (upstream) | `/Users/chris/Code/AgIsoStack-plus-plus` | the stack; the build fetches it at the pin |
| AgOpenWeb | `/Users/chris/Code/AgOpenWeb` | the app that will host the terminal (C#); its plan for that is not written yet |
| AgOpenWeb-HAT | `/Users/chris/Code/AgOpenWeb-HAT` | the HAT daemon and the dev CM4; will build a gateway service against docs/TRANSPORT.md (its `docs/PLAN_VT.md`) |
| isobus-implement-sim | `/Users/chris/Code/isobus-implement-sim` | simulated implements (MIT), to test against |

## Rules

- **One thread.** Everything in the shim runs on the thread that calls `isoshim_step`;
  `isoshim_receive` only queues. Never link the stack's HardwareIntegration library or start a
  stack thread (`initialize(true)`): the shim supplies `send_can_message_frame_to_hardware` and
  drives `receive_`, `on_transmit_` and `periodic_update_from_hardware` itself.
- **Anything that owns a thread is joined before it is destroyed** (the VT server's pool
  parsing threads: the managed working set's destructor joins its own, upstream #805 fixing
  #802; the shim joined them itself until the pin carried it). The self-test's stop-while-parsing run
  is the check; after changing teardown, break it on purpose once and see that run crash.
- **The pin follows upstream HEAD** (`AGISOSTACK_GIT_TAG` in `native/CMakeLists.txt`): the
  user's rule, as for AgOpenWeb-HAT. Move it, then run the self-test on the Mac, the Android
  tablet and the iPad. Never hold back for older upstream. Do not patch the fetched tree for the
  shim; an upstream fix not yet merged goes in as a patch with a note to drop it.
- **MIT.** Nothing GPL comes in (AgOpenWeb-HAT, the Teensy firmware and AgIsoVirtualTerminal are
  GPL: read them, do not copy from them).
- **Nothing public without the user's OK**: upstream issues, pull requests, comments, making this
  repo public. The repo is private until the user says otherwise; the name is the user's call.
- **Test app IDs:** `com.agopenweb.isostack-tests` (iOS), `com.agopenweb.isostack_tests`
  (Android). Never sign a test as `com.agopenweb.ios`: that replaces the installed AgOpenWeb app.
- Device IDs, the Apple team, the gateway's address and the access point key stay out of the
  repo: `bench/devices.env` and `dotnet/Local.props` (both ignored).
- **No installed base.** AgOpenWeb and the HAT are entering beta; the protocol can change in
  step with them. No version fallbacks for our own components (things outside our control, such
  as SavvyCAN or existing AiO boards, are another matter).

## Working with the user

- Small changes are committed straight to `main` and pushed; ask before a large piece of work
  goes on a branch or not.
- The dev CM4 and both tablets are available for testing. **Change the CM4's network setup only
  with the user's say-so**; it is the AgOpenWeb-HAT dev unit (its hardware watchdog is live while
  `aow-hatd` runs: stop the service, never kill the process).
- Bench instructions for the user stand on their own: the user reads a scrolling terminal, so
  never refer to "the step above" or an earlier ID; say in full what to do each time.
- Run shell loops as bash scripts: zsh does not split `$VAR`s holding a command and its options.
