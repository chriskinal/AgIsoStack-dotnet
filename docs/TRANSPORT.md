# Transport: CAN frames between a gateway and the terminal

How an implement bus reaches a stack that is not wired to it. The terminal (AgIsoStack++'s VT
server through this wrapper) runs in an application such as AgOpenWeb, on a tablet, a PC or a
Linux box; a **gateway** sits on the bus and carries every CAN frame to it and back. Nothing here
built so far is the protocol of section 5: its client in the package, its reference gateway and
checker in `bench/`. Decisions so far were taken on 2026-10-23/24 in the AgOpenWeb-HAT work that
started this repo.

## 1. The shape

```
implement ── CAN ── gateway ── TCP over Wi-Fi, Ethernet or loopback ── application ── isoshim
```

| Gateway | Notes |
|---|---|
| The AOW HAT on a CM4 (AgOpenWeb-HAT) | A small service on CAN2 (`hatcan2`), beside `aow-hatd`; planned in that repo's `docs/PLAN_VT.md` section 3 |
| Any Linux box with a CAN interface | The same service; SocketCAN |
| An ESP32 with a CAN transceiver, its own Wi-Fi access point | The DIY gateway, SolSteer's arrangement; firmware not written |
| An AgOpenGPS AiO board (Teensy) | Its GVRET is sniff-only (section 4); changing that firmware is for its maintainers, not this project |

Or no gateway at all: a CAN adapter plugged into the tablet or PC, speaking slcan
(`SlcanLink`, a CANable with its stock firmware and other serial adapters): the USB cable is the
link, on Windows, macOS, Linux and Android (USB host), not iOS. It has no heartbeat and reports no
bus errors; unplugging is its link cut.

When the application runs on the gateway itself (AgOpenWeb headless on the CM4), it can connect
to the gateway on loopback, so the code path is the same everywhere; opening SocketCAN directly
from .NET is the alternative.

**Why the terminal is not at the bus end.** The first plan (AgOpenWeb-HAT, 2026-10-13) kept the
ISOBUS stack wired to the bus and sent only a picture over Wi-Fi, on the reasoning that Wi-Fi
stalls would break ISOBUS timing. That was overstated: SolSteer View-T ships a terminal in a
tablet app with every frame over one Wi-Fi hop, and our bench (docs/RESULTS.md) stays inside the
timers with delay, loss and cuts of up to half a second. What remains is a trade-off: with the
terminal across the link, a lost link means the terminal leaves the bus (the implement sees no
VT status for 3 s and resets its connection), where a bus-end terminal would only freeze the
picture. That is acceptable once it is known behaviour. In return the gateway is dumb and cheap,
with no ISOBUS code to keep up to date, and the terminal is the same on every platform.

## 2. TCP, not UDP or WebSocket

**Not UDP.** ISOBUS stacks are written for CAN, which delivers every frame in order (the
controller retransmits until a frame gets through). The transport protocols count on it: one
missing or reordered data packet aborts the session (ETP abort reasons 6 and 7 on the bench) and
the implement starts its whole pool again; one lost frame in 22,000 costs the upload. Wi-Fi
retries unicast at the radio, so loss is low but not zero, and worse with interference or
roaming. Sequence numbers and retransmission over UDP would rebuild TCP. Cannelloni (CAN over
UDP) was considered for this reason and set aside.

**TCP fits.** Reliable and ordered, like CAN. A lost packet delays what is behind it until it is
resent, tens to a few hundred milliseconds, which the ISOBUS timers allow (section 6); a stall
long enough to break them would break UDP as badly. Bandwidth is trivial: a full 250 kbit/s bus
is about 1,800 frames/s, about 30 kB/s. The group members who own SolSteer's module say it uses
TCP.

**Not WebSocket.** TCP plus an HTTP handshake and its own framing, which buys a browser at the
other end. The gateway talks to the application's backend, not a browser, and the extra code on
an ESP32 gains nothing.

## 3. What the protocol needs

1. **Low latency.** `TCP_NODELAY`; frames waiting when the gateway reads its CAN socket go out in
   one write, never held for a timer. (The Teensy's GVRET flushes every 20 ms: fine for sniffing,
   a large cost on every request and reply.)
2. **A heartbeat both ways**, about every 100 ms, so either end knows within about half a second
   that the link is gone (TCP's own detection takes minutes), and the terminal knows it is off the
   bus. The bench's ping, every 50 ms from the terminal and echoed by the relay, also times the
   round trip.
3. **A hello**: protocol version, bitrate, the gateway's identity and its interfaces.
4. **Bus status** from the gateway: error passive, bus-off, frames dropped or overflowed.
5. **Gateway timestamps** on frames from the bus.
6. **A stale-frame limit.** After a link cut TCP delivers everything queued during it: in the
   10 s cut runs, frames 10 s old reached the bus and the terminal. A soft key pressed during a
   cut would reach the implement seconds late. Both ends drop frames older than a limit (1 s to
   start: longer than a Wi-Fi retry burst, shorter than a held-button risk). The terminal side
   also has to send the release of any button held when the link went.
7. **Nothing queued while nobody is connected.** The bench relay drains the bus on accept, so a
   new client does not get old frames. Keep that.
8. **One client at a time to start**; say what a second gets (refused, or listen-only).
9. **Reconnect.** A client reconnects on its own after a drop; untested so far (section 7). Section 5 answers 1 to 9.

## 4. Existing protocols

- **GVRET** (SavvyCAN's protocol): what `aow-hatd` (TCP 23) and the Teensy AiO firmware serve, and
  what ESP32RET-style DIY Wi-Fi gateways speak. Both servers we have are **sniff-only**: they parse
  frames sent to them and drop them (the firmware: "read-only sniffer, don't transmit"), the
  Teensy serves one client and flushes every 20 ms, and `aow-hatd`'s carries the brand CAN through
  its control loop, not the ISOBUS channel. GVRET has a frame-send command, timestamps and a
  binary mode, but no heartbeat or bus status. Adopting it would let existing ESP32 gateways and
  SavvyCAN work; the gaps above need a place either way.
- **Proemion's Byte Command Protocol** (published at docs.proemion.com, no license stated): the
  host protocol of Proemion's CAN gateways over serial, USB, Ethernet, Bluetooth and Wi-Fi. Proven
  in use, but the same shape as GVRET: a command set with a checksum per message, frames with an
  optional 100 µs timestamp, transmit confirmations, an error-status byte (bus-off and overflow
  flags, the last controller error), and bus load in messages per second. No heartbeat or rule
  for a dead link, no error-passive state or error counters, no dropped counts, no hello or
  version handshake, nothing on more than one client, no TCP port. Looked at 2026-10-25 and not
  adopted: it would buy compatibility only with Proemion's own gateways, and lacks the parts
  section 5 is built around. If one turns up on a machine, an adapter in the client is the way.
- **Cannelloni**: UDP, set aside (section 2).
- **The bench relay's format** (its first relay, replaced by section 5's gateway), little-endian
  records:

  | Type | Record | Size |
  |---|---|---|
  | `0x01` frame | `ts_us:u32` `id:u32` (bit 31 = extended) `len:u8` `data:8` | 18 bytes |
  | `0x02` ping | `seq:u32` `t:u64` (sender's clock), echoed by the relay as `0x03` | 13 bytes |

  No hello, bus status or version yet. It is the measured starting point, not a specification.

**Decided 2026-10-24: our own protocol (section 5), GVRET left as it is for SavvyCAN.** GVRET is
the wrong shape: it is a command set for a sniffer and its PC tool, where we need a link with a
lifetime (a hello, a heartbeat, a rule for when it is dead) and a bus status. Every one of those
would be an addition only our own gateways and clients understood, and the GVRET servers we have
would need changing to transmit anyway, so the compatibility it seems to offer is not there.

## 5. The protocol, version 1 (approved and built 2026-10-24)

A small binary protocol over one TCP connection, MIT, written so an ESP32 can implement it in a
few hundred lines. Name: **AOW link** (the user's choice, 2026-10-25; the working name "CAN link"
was dropped because Proemion sells its gateways as CANlink). The bench relay's
format (section 4) is its starting point.

### The connection

- **TCP, `TCP_NODELAY`**, default port 2323 (the bench's). The gateway listens; the application
  (the client) connects. Everything waiting to be sent goes out in one write, never held for a
  timer.
- **Messages**, little-endian, back to back on the stream:

  | Field | Size | |
  |---|---|---|
  | `type` | u8 | below |
  | `len` | u8 | bytes of body that follow |
  | body | `len` | |

  A receiver skips a type it does not know, by `len`. New messages can be added that way without
  a new version; a change to an existing message is a new version.
- **Hello first.** The client sends HELLO; the gateway answers with its HELLO, or with CLOSE and
  closes. Nothing else is sent before the two HELLOs. A gateway that has no HELLO 2 s after
  accepting closes the connection.
- **Versions must match.** There is no installed base and no fallback: a peer that speaks another
  version gets CLOSE (version).

### The link is dead after 1 second of silence

Each side sends HEARTBEAT every 100 ms, whatever else it sends. **A side that has received
nothing for 1,000 ms closes the connection at once, discarding what it has not yet sent**
(`SO_LINGER` 0, so the kernel drops its send buffer), and the client starts a new one.

TCP stretches a cut: it resends lost data after 200 ms, then 400, 800 (doubling), so data lost
early in a cut of about half a second or more arrives only after 1 s of silence and the link is
remade. That costs an implement in use nothing (it allows 3 s, and the link is back within about
half a second of the cut ending); an upload is lost to any cut over 750 ms anyway (ETP T1).

That one rule is also the stale-frame limit (section 3, item 6), with no clock shared between
the two ends. Frames and heartbeats travel in one ordered stream, so a frame held up by a stall
holds up the heartbeats behind it. A stall shorter than 1 s is passed through: everything arrives
up to 1 s late, which the ISOBUS timers allow (section 6). A stall of 1 s or more ends the
connection on both sides, and what it held is never delivered. On the bench a cut of 1 s or more
aborted an upload anyway, so nothing that could have survived is lost. A frame never reaches the
bus or the terminal more than about 1.1 s after it was sent.

**Reconnecting.** The client connects again at once, then every 250 ms until it gets in, each
attempt given 500 ms (so one made during a cut does not hold up the next once it is over). A new
connection starts clean: the gateway drops whatever was queued from the bus before it, and the
client sends nothing that was meant for the old one.

### Messages

| Type | Name | Direction | Body |
|---|---|---|---|
| `0x01` | HELLO | both, first | `magic:4` (`AOWL`), `version:u8` (1), `role:u8`, `name:str`; from the gateway also `channels:u8` and per channel `bitrate:u32` (bit/s), `flags:u8` (bit 0 listen-only), `name:str` |
| `0x02` | FRAME | both | `channel:u8`, `flags:u8` (bit 0 extended 29-bit id), `id:u32`, `time:u32`, `data:0..8` (the rest of the body) |
| `0x03` | HEARTBEAT | both, every 100 ms | `time:u32` (sender's clock, ms), `echo:u32` (the `time` of the last HEARTBEAT received), `held:u16` (ms between receiving that one and sending this) |
| `0x04` | STATUS | gateway | `channel:u8`, `state:u8`, `txErrors:u8`, `rxErrors:u8`, `rxDropped:u32`, `txDropped:u32` |
| `0x05` | CLOSE | both, last | `reason:u8`, `text:str` |

`str` is `length:u8` and that many bytes of UTF-8.

- **HELLO.** `role` from a client: 0 control (sends and receives frames), 1 monitor (receives
  only). From the gateway: 0. `name` identifies the sender for logs ("AgOpenWeb 1.4 on Galaxy Tab",
  "AOW HAT CM4 hatcan2"). The gateway's channels are numbered from 0 in the order listed.
- **FRAME.** `time` from the gateway is the bus receive time in microseconds on the gateway's own
  clock (kernel timestamp where there is one; wraps after about 71 minutes): for spacing and
  logs, never compared with the client's clock. From the client, 0. 2 + 10 + 8 = 20 bytes for a
  full frame on the wire; a saturated 250 kbit/s bus is about 1,800 frames/s, 36 kB/s.
- **HEARTBEAT.** Round trip = now − `echo` − `held`, on the receiver's own clock, so both ends
  measure it with no clock in common (the bench's ping, made symmetric).
- **STATUS**, per channel: after the gateway's HELLO, on any change of `state`, and every 1 s.
  `state` 0 error active, 1 error warning, 2 error passive, 3 bus-off, 4 down (the interface is not
  up). The error counters are the controller's. `rxDropped` counts frames lost before the gateway
  read them (receive overflow), `txDropped` frames from the client the bus did not take: the
  gateway keeps order and drops a frame not sent within 100 ms (bus-off, or no other node to
  acknowledge it). Both count from the start of the connection.
- **CLOSE** says why, then the sender closes: 1 version, 2 busy, 3 timeout (silence), 4 protocol
  error, 5 shutting down. A side closing on silence may send it but cannot count on it arriving.

### Clients

- **One control client.** While one is connected, another control client gets CLOSE (busy). A
  dead one is closed by the gateway within 1 s, so the same application reconnecting after a drop
  gets in within about a second.
- **Monitors are optional.** A gateway may accept any number (a logger, a diagnostics page), or
  refuse them with CLOSE (busy). A monitor gets every FRAME both ways and STATUS; a FRAME from a
  monitor is a protocol error.

### What each end does around the link

- **The gateway** knows nothing of ISOBUS. It passes frames, drains the bus when a control client
  connects, sends nothing to the bus while none is connected, and reports STATUS.
- **The terminal** keeps the stack running while the link is down (the implements decide, by
  their 3 s timeouts) and drops what it sends meanwhile. A key or button the operator held when
  the link went is released (aborted) when it comes back, if that implement is still connected:
  a press is always followed by a release. The operator sees that the bus is gone.

### Finding a gateway

The address is configured (an ESP32 on its own access point has a fixed one; AgOpenWeb on the CM4
uses loopback). A gateway may also announce itself by DNS-SD as `_aowlink._tcp`, TXT `v=1`.

### Security

None in version 1. Anyone who can reach the port can put frames on the implement's bus, so a
gateway listens only where the network is the user's: loopback, a cable, or its own WPA2 access
point. A pairing key in HELLO is open (section 7) before a gateway joins a shared network.

### Built against it

- **The client**: `AowLink` in the package (docs/API.md), with `vt.Attach(link)` for a terminal.
  The self-test's ninth run tests it against a scripted gateway, on every platform.
- **The reference gateway**: `bench/aowlink_gateway.py` (Python, for the bench). Any
  number of SocketCAN interfaces, one control client, monitors, STATUS from `ip -details`.
- **The HAT's gateway**: `aow-linkd` in AgOpenWeb-HAT (C++, its `docs/PLAN_VT.md` section 2,
  2026-10-25): a service on the CM4, off by default, on `hatcan2`, listening on loopback unless
  the unit is told otherwise. What ships; the reference gateway stays the bench's fallback.
- **The checker**: `bench/aowlink_check.py HOST [PORT]` tests any gateway against this section:
  hello, status, heartbeat rate, busy, monitors, the silence rule, versions, bad input, unknown
  messages. Both gateways pass, on the CM4 and in a container.
- **To build**: the ESP32 gateway.

## 6. The timers that matter (ISO 11783-3 and -6)

| Timer | Limit | Measured worst (docs/RESULTS.md) |
|---|---|---|
| ETP Tr, respond to a request to send or a batch | 200 ms | round trip 14-21 ms on a dedicated access point; 9-13 ms median on home Wi-Fi |
| ETP T1, gap between data packets | 750 ms | 56-102 ms (access point); about 700 ms once on home Wi-Fi (Galaxy Tab) |
| ETP T2/T3, wait for the next clear-to-send or data | 1,250 ms | a cut of 1 s or more aborts the transfer |
| VT status timeout (client) / working set maintenance (terminal) | 3 s | a cut of 3 s or more resets VT connections |

Each batch of a transfer costs one round trip, so the implement's packets per DPO decides upload
time over a link: about 84 batches at 255 for a 150 kB pool, about 1,340 at the stack's default 16.

## 7. Open

1. A pairing key in HELLO, for gateways on networks that are not the user's own.
2. Bus-off and a second CAN channel on real hardware: built, not yet tested (the bench has one
   link channel; vcan has no error states).
3. Tablet power saving: it can hold packets for a few hundred milliseconds whatever the transport.
4. The field's worst case is not the bench's: a metal cab, a tablet behind glass, an ESP32's
   weaker radio, 2.4 GHz only. Expect long stalls and dropped connections more than a slow steady
   round trip.
