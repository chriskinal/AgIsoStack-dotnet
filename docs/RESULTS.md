# Results

What the spike and the bench have shown, newest last. All runs upload upstream's VT3 example pool
(149,645 bytes, 34 objects) from upstream's VT example implement and end with a soft key round
trip (key 5000 pressed on the terminal, the implement shows alarm mask 2000).

## 2026-10-24: in process, three platforms

The wrapper with a terminal on channel 0 and an implement on channel 1, C# carrying every frame.
Passed on macOS (arm64), a Galaxy Tab S7 FE (Android 14) and an iPad Pro 2nd generation
(iPadOS 17.7, full AOT), with stop and restart in one process. `[UnmanagedCallersOnly]` callbacks
work under iOS AOT. With the stack's default of 16 frames per clear-to-send an upload ran at about
1,050 frames/s, below a 250 kbit/s bus; at 255 it is bus speed.

## 2026-10-24: Wi-Fi bench

The terminal on the Mac or a tablet; frames over TCP to `can_tcp_relay.py` on a Raspberry Pi CM4
(Linux, MCP2518FD CAN), the implement on a second CAN interface wired to the first. Time from the
implement's first request-to-send to the pool parsed:

| Link | Packets per DPO | Mac | Galaxy Tab | iPad |
|---|---|---|---|---|
| Home Wi-Fi, device to access point to CM4 | 16 | 45 s | 46 s | 55 s |
| Home Wi-Fi | 255 | 14.5 s | 14.5 s | 14.7 s |
| The CM4 as access point, one hop | 16 | | 33.7 s | 34.0 s |
| The CM4 as access point | 255 | | 13.8 s | 13.8 s |

The bus alone needs about 12 s. Each batch costs a round trip over the link, so the implement's
DPO size decides it. Round trip p50 on the access point: 2.5 to 3 ms.

## 2026-10-24: two bugs found by capture (fixed outside this repo)

- **Lost frames in the stack's transmit queue.** The hardware layer's transmit queue is
  single-producer and lock-free, but the stack's own thread and an application thread both push
  to it (upstream issue #656). One frame is lost silently and an ETP transfer aborts (a missing
  data packet or DPO). Upstream PR #793 adds a lock; the bench's implements are built with it
  (`bench/implement/pr793-transmit-queue.patch`). This wrapper does not use the hardware layer.
- A VT client (AgOpenWeb-HAT's) gave up on a terminal 10 s after starting unless connected,
  killing slow uploads.

With both fixed: 12 of 12 iPad runs at 255 clean (about 19.2 s each, one upload attempt per
implement, no aborts), where 3 of 8 had failed before.

## 2026-10-24: impaired link (netem), iPad, 255 per DPO

netem on the CM4's access point, both directions, three runs each. Time from the terminal's start
to the soft key round trip (the clean figure is about 19 s):

| Impairment | Round trip p50 / max | Passed in | Notes |
|---|---|---|---|
| none | 2.5 / 140 ms | 18.5-19.2 s | |
| 25 ms each way | 53 / 330 ms | 23.5-25.1 s | |
| 25 ms ±10 | 58 / 360 ms | 24.2-25.3 s | |
| 100 ms ±30 | 215 / 540 ms | 39.9-40.8 s | about one round trip per batch, as expected |
| 1 % loss | 2.6 / 110 ms | 19.2-19.3 s | TCP repairs it inside the ETP timers |
| 5 % loss | 4 / 410 ms | 21.1-21.9 s | |
| 50 ms ±25 and 3 % loss | 140 / 720 ms | 35.0-36.7 s | |

Link cuts (100 % loss both ways) 8 s after the implement starts, mid-upload:

| Cut | Passed in | What happened |
|---|---|---|
| 0.5 s | 18.9-20.0 s | ridden through |
| 1 s | 28.3 s | the terminal times the transfer out (ETP T2, 1.25 s); the implement starts the whole pool again |
| 2 s | 29.9 s | the same |
| 3 s | 30.1 s | the same, and the other implement's upload restarts too |
| 5 s | 36.4-37.3 s | and VT status timeouts: the implements reset their connections |
| 10 s | 44.4-45.4 s | the same; TCP held the connection and everything recovered |

A cut of 1 s or more mid-transfer costs the whole pool again (ETP cannot resume). TCP never drops
the connection, but it delivers every frame queued during the cut afterwards: a gateway should
drop frames past an age limit (each relay record carries a timestamp), or a soft key pressed
during a cut reaches the bus seconds late.

## 2026-10-24: the stop crash, and wrapper v2

In the cut runs the iPad app aborted in `isoshim_stop` (`std::terminate`) about half the time,
and a Mac did every time with a 3 s cut. The cause: the VT server parses each pool on a thread
owned by its `VirtualTerminalServerManagedWorkingSet`, whose destructor is `= default`, so a
working set destroyed with a finished but unjoined parse (a pool that arrived just before stop)
terminates the process. Upstream HEAD's server destructor joins only working sets that already
shut down. Any application that stops a VT server as a pool arrives can hit it. Reported upstream
with a minimal repro and the fix:
[#802](https://github.com/Open-Agriculture/AgIsoStack-plus-plus/issues/802).

Wrapper v2 fixes it and changes the threading: no hardware layer, the stack's hooks supplied by
the shim, everything on the stepping thread, and the terminal joins parse threads before it is
destroyed. A self-test run that stops the stack the moment a pool starts parsing crashes without
the join and passes with it, on all three platforms.

v2 against the same bench, iPad over the access point (3 runs each):

| | Passed in | Crashes |
|---|---|---|
| clean | 18.0-19.3 s | 0 |
| 1 s cut | 28.2-29.2 s | 0 |
| 3 s cut | 30.2-31.3 s | 0 (Mac, wired, 6 runs: 0; was 6 of 6) |
| 5 s cut | 34.1-34.3 s | 0 |
| 10 s cut | 40.2-41.2 s | 0 |
| 50 ms ±25 and 3 % loss | 35.4-36.1 s | 0 |

In process at 16 frames per clear-to-send, v2 moves about 2,040 frames/s (v1: about 1,050). The
long cuts recover a few seconds sooner than v1; the runs are separate small sets, not a
controlled comparison, and the reason is not proven.

The Galaxy Tab with v2 (3 runs each): clean 19.2-19.4 s, 3 s cut 30.4-31.5 s, every run stopped
cleanly.
