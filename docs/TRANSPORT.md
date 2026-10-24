# Transport: CAN frames between a gateway and the terminal

How an implement bus reaches a stack that is not wired to it. The terminal (AgIsoStack++'s VT
server through this wrapper) runs in an application such as AgOpenWeb, on a tablet, a PC or a
Linux box; a **gateway** sits on the bus and carries every CAN frame to it and back. Nothing here
is built beyond the bench relay. Decisions so far were taken on 2026-10-23/24 in the AgOpenWeb-HAT
work that started this repo.

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
resent, tens to a few hundred milliseconds, which the ISOBUS timers allow (section 5); a stall
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
9. **Reconnect.** A client reconnects on its own after a drop; untested so far (section 6).

## 4. Existing protocols

- **GVRET** (SavvyCAN's protocol): what `aow-hatd` (TCP 23) and the Teensy AiO firmware serve, and
  what ESP32RET-style DIY Wi-Fi gateways speak. Both servers we have are **sniff-only**: they parse
  frames sent to them and drop them (the firmware: "read-only sniffer, don't transmit"), the
  Teensy serves one client and flushes every 20 ms, and `aow-hatd`'s carries the brand CAN through
  its control loop, not the ISOBUS channel. GVRET has a frame-send command, timestamps and a
  binary mode, but no heartbeat or bus status. Adopting it would let existing ESP32 gateways and
  SavvyCAN work; the gaps above need a place either way.
- **Cannelloni**: UDP, set aside (section 2).
- **The bench relay's format** (`bench/can_tcp_relay.py`), little-endian records:

  | Type | Record | Size |
  |---|---|---|
  | `0x01` frame | `ts_us:u32` `id:u32` (bit 31 = extended) `len:u8` `data:8` | 18 bytes |
  | `0x02` ping | `seq:u32` `t:u64` (sender's clock), echoed by the relay as `0x03` | 13 bytes |

  No hello, bus status or version yet. It is the measured starting point, not a specification.

Proposed earlier: a small protocol of our own, documented and MIT so any gateway can implement
it, with GVRET left as it is for SavvyCAN. Open: own protocol or GVRET with additions; decide with
the ESP32 gateway in view.

## 5. The timers that matter (ISO 11783-3 and -6)

| Timer | Limit | Measured worst (docs/RESULTS.md) |
|---|---|---|
| ETP Tr, respond to a request to send or a batch | 200 ms | round trip 14-21 ms on a dedicated access point; 9-13 ms median on home Wi-Fi |
| ETP T1, gap between data packets | 750 ms | 56-102 ms (access point); about 700 ms once on home Wi-Fi (Galaxy Tab) |
| ETP T2/T3, wait for the next clear-to-send or data | 1,250 ms | a cut of 1 s or more aborts the transfer |
| VT status timeout (client) / working set maintenance (terminal) | 3 s | a cut of 3 s or more resets VT connections |

Each batch of a transfer costs one round trip, so the implement's packets per DPO decides upload
time over a link: about 84 batches at 255 for a 150 kB pool, about 1,340 at the stack's default 16.

## 6. Open

1. Own protocol or GVRET with additions (section 4).
2. The stale-frame limit and the held-button release (section 3).
3. **Disconnect and reconnect mid-upload and in use**: the bench client does not reconnect yet;
   the cuts so far kept TCP up.
4. What the gateway does with a second client.
5. Tablet power saving: it can hold packets for a few hundred milliseconds whatever the transport.
6. The field's worst case is not the bench's: a metal cab, a tablet behind glass, an ESP32's
   weaker radio, 2.4 GHz only. Expect long stalls and dropped connections more than a slow steady
   round trip.
