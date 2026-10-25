# Results

What the spike and the bench have shown, newest last. All runs upload upstream's VT3 example pool
(149,645 bytes, 34 objects) from upstream's VT example implement and end with a soft key round
trip (key 5000 pressed on the terminal, the implement shows alarm mask 2000).

## 2026-10-24: in process, three platforms

The wrapper with a terminal on channel 0 and an implement on channel 1, C# carrying every frame.
Passed on macOS (arm64), a Galaxy Tab S7 FE (Android 14) and an iPad Pro 2nd generation
(iPadOS 17.7, full AOT), with stop and restart in one process. The stack built unchanged for
Android (NDK) and iOS on the first try. `[UnmanagedCallersOnly]` callbacks work under iOS AOT,
called from the stack's own thread. With the stack's default of 16 frames per clear-to-send an
upload ran at about 1,050 frames/s (about 22 s for the pool), below a 250 kbit/s bus; at 255, C#
carried about 4,500 frames/s, so the boundary between C# and native code is not a bottleneck.

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
DPO size decides it. Round trip p50 / p99 on home Wi-Fi 9-13 / 37-180 ms (the Mac 9-12 / 21-26,
max 39), on the access point 2.5-3 / 13-21 ms. One bad patch on home Wi-Fi gave the Galaxy Tab
round trips up to 704 ms (p99 583): 6 aborts on `aow-hatd`'s upload, which still completed, and a
255 run of 18.2 s; the next run was 14.7 s. Early failures were all bench script bugs, and the
CAN2-CAN3 wire was cleared of them: a frame sent on CAN2 reaches CAN3 once, and Linux never
echoes it to the sending socket.

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

## Open observations

- **Two request-to-sends 2 ms apart** (2026-10-24, iPad, 25 ms each way, one run in three):
  `aow-hatd`'s RTS reached the terminal twice within 2 ms; the terminal logged an overwrite of the
  session, `aow-hatd` aborted with reason 4, and its third attempt worked about 1 s later. Seen
  once; that run's relay dump was overwritten. Relay dumps are now kept per run
  (`dump-NAME.log` on the gateway), so look again if it recurs.
  **Recurred 2026-10-25** through `aow-linkd` (iPad again, the first run of four, 1 ms apart, the
  same overwrite and abort, back on the third attempt), and not in two reruns of the same case.
  So not one gateway's doing; the two arrive in one TCP batch, so the bench's times say nothing of
  their spacing on the bus. `aow-linkd` keeps no dump: a capture on the bus (a monitor client, or
  `candump` on `hatcan3`) is what would show whether `aow-hatd` sent the RTS twice.
- **Speed at 16 per DPO over a quiet link** (34 s, against about 22 s in process): part of the
  difference was how often the v1 terminal looked for frames (10 ms in the bench, 4 ms in the
  stack). v2 steps every 2 ms; not re-measured at 16 over the link.

## 2026-10-24: the C# class on every platform, and on the bench

The self-test (eight runs, the last driving `VirtualTerminal` as a host would) passes on macOS,
the Galaxy Tab, the iPad (full AOT), Linux arm64 and x64 (.NET containers; x64 under Rosetta)
and Windows x64 (a Parallels VM, Windows on ARM, under its x64 emulation). A fresh app using only
the package claims its address on Linux arm64 and x64, Windows x64 and macOS.

The bench now runs through `VirtualTerminal` (its own thread, woken by each frame the link's
reader hands it) instead of stepping the raw interface itself. Home Wi-Fi, one run each, every
one passed with one upload attempt per implement, no aborts, the pools stored:

| Packets per DPO | | Mac | Galaxy Tab | iPad |
|---|---|---|---|---|
| 255 | first request-to-send to pool parsed | 17.9 s | 18.0 s | 15.5 s |
| 255 | start to soft key round trip | 19.1 s | 19.4 s | 18.5 s |
| 16 | first request-to-send to pool parsed | 33.4 s | 40.4 s | 38.1 s |

Round trip p50 / p99 5-10 / 14-48 ms. In line with the raw interface's runs (about 19 s to the
round trip at 255; 45-55 s to parse at 16, on a worse Wi-Fi day). The first runs failed: with no
pool store the terminal answered Store Version with an error, and upstream's VT client then
resets its connection and uploads again, every 12 s (aow-hatd) or 23 s (the example). The class
now keeps pools in memory when the host gives no store (PLAN.md, upstream gaps).

## 2026-10-25: the HAT's gateway, `aow-linkd`

AgOpenWeb-HAT's C++ gateway (0.2.24, its `docs/PLAN_VT.md` section 2) as a service on the CM4, in
place of `aowlink_gateway.py`, home Wi-Fi, the Mac as the terminal. It passes `aowlink_check.py`
on the CM4 and in a container, and reads the bus's 250 kbit/s from rtnetlink.

| Implement | First request-to-send to pool parsed | Passed in | Heartbeat round trip p50 / p99 | Link | Frames from the bus / to it |
|---|---|---|---|---|---|
| 255 per DPO | 19.1 s | 20.3 s | 11 / 25 ms | up throughout | 24,439 / 314 |
| 16 per DPO | 46.0 s | 47.2 s | 10 / 22 ms | up throughout | 26,921 / 1,594 |

The tablets the same day (0.2.26, the gateway switched on from the ISOBUS page, listening on
every network), test apps rebuilt for the AOW link name (their self-tests pass):

| Terminal | Implement | First request-to-send to pool parsed | Passed in | Round trip p50 / p99 |
|---|---|---|---|---|
| iPad | 255 per DPO | 17.6 s (19.0, 18.8 in two reruns) | 18.7 s | 13 / 31 ms |
| iPad | 16 per DPO | 42.3 s | 46.5 s | 10 / 22 ms |
| Galaxy Tab | 255 per DPO | 14.4 s | 20.1 s | 12 / 30 ms |
| Galaxy Tab | 16 per DPO | 40.5 s | 46.0 s | 10 / 23 ms |

All with the link up throughout. On the iPad's first 255 run `aow-hatd`'s status screen (0xF7)
needed three attempts (open observations: two RTS 1 ms apart, as on 2026-10-24 with the relay);
every other run one attempt per implement, no aborts.

On the Mac: one upload attempt per implement (0x80, and `aow-hatd`'s own status screen from 0xF7, which found
the terminal through the gateway on the shared `hatcan2`), no ETP aborts, no frames dropped either
way. As the reference gateway's runs (255 per DPO, Mac: 18.4 s, and 17.9 s before the protocol).
In a container (`bench/local.sh` with `GATEWAY=`): the same run in 8.7 s, and a 1.5 s cut
mid-upload dropped the link after 1 s of silence, remade it 1.0 s later, and the upload was
redone and passed, as with the reference gateway.

## 2026-10-24: the AOW link protocol (version 1) on the bench

The terminal through `AowLink` (attached to `VirtualTerminal`) and the reference gateway
(`bench/aowlink_gateway.py`) on the CM4, home Wi-Fi. The gateway passes `aowlink_check.py` on the
CM4 (and reads the bus's 250 kbit/s from the kernel); the self-test's link run passes on the Mac,
the iPad, the Galaxy Tab, Linux arm64 and x64, and Windows x64.

Clean runs, one each, every one passed with the link up throughout, one upload attempt per
implement and no aborts:

| Packets per DPO | | Mac | Galaxy Tab | iPad |
|---|---|---|---|---|
| 255 | first request-to-send to pool parsed | 18.4 s | 14.8 s | 14.2 s |
| 255 | start to soft key round trip | 19.7 s | 20.2 s | 18.5 s |
| 16 | first request-to-send to pool parsed | | 39.2 s | 37.9 s |

Round trip from the heartbeats p50 / p99: Mac 6 / 136 ms, Galaxy Tab 9-16 / 23-88, iPad 7-9 /
19-23.

Link cuts (netem, 100 % loss both ways on the CM4's Wi-Fi) 5 s after the implement starts,
mid-upload, Galaxy Tab, 255 per DPO:

| Cut | Link | Passed in | What happened |
|---|---|---|---|
| 0.4 s | stayed up | 20.6 s | ridden through; `aow-hatd`'s small upload timed out (ETP, 750 ms) and was redone |
| 1.5 s | dropped after 1 s of silence, back 1.05 s later | 26.9 s | both uploads redone |
| 5 s | dropped after 1 s, back 0.4 s after the cut ended | 36.3 s | both uploads redone; as the old relay (36.4-37.3 s) |

The same in a container with no hardware (`bench/local.sh`, vcan, loopback cuts): a 0.8 s cut
also remade the link, because TCP's retransmission backoff (200 ms, then doubling) turned it into
more than 1 s of silence (TRANSPORT.md section 5). In every case nothing older than the cut was
delivered afterwards, which the old relay could not promise.
