# Gateways

A gateway sits on the CAN bus and carries every frame over TCP to a terminal (`AowLink` in the
package), in the AOW link protocol ([docs/TRANSPORT.md](../docs/TRANSPORT.md) section 5). It
knows nothing of ISOBUS. MIT, like the rest of this repo.

| | What | Status |
|---|---|---|
| [`linux/`](linux) | `aow-linkd`: any number of SocketCAN interfaces, C++, one thread | Runs on the AOW HAT (AgOpenWeb-HAT packages it) and any Linux box with SocketCAN; benched on the CM4 with the tablets and the Mac |
| [`../bench/aowlink_gateway.py`](../bench/aowlink_gateway.py) | The reference gateway, Python | The bench's own; stays in `bench/` with the bench scripts that run it |
| [`esp32/`](esp32) | An ESP32 with a CAN transceiver and its own Wi-Fi access point, PlatformIO | Passes `aowlink_check.py` on the bench Wi-Fi, announced by DNS-SD; frames both ways with the HAT on the bench bus; not yet run with a terminal |

`bench/aowlink_check.py HOST [PORT]` checks any gateway against the protocol.

## aow-linkd (Linux)

```sh
cmake -S gateway/linux -B build-gateway && cmake --build build-gateway
ctest --test-dir build-gateway                     # the protocol's unit tests (any OS)
build-gateway/aow-linkd --can can0 --listen 0.0.0.0
```

Each nightly and tagged release has it built for Linux x64 and arm64 (on Debian 12: glibc 2.36
and later, as Raspberry Pi OS bookworm).

- `--can IF[,IF...]`: the interfaces, numbered as channels in that order (default `can0`). One
  missing at start is looked for every second; one that goes down shows as down in STATUS.
- `--listen ADDR` (default `127.0.0.1`: a terminal on the same machine only; `0.0.0.0` for
  tablets on the network), `--port N` (2323), `--name TEXT` (the name terminals show; default the
  host name and interfaces).
- `--config FILE`: `name=` and `network=1` lines, read before the other options, for a settings
  page to write (the HAT's ISOBUS page does).
- `--status HOST:PORT`: how it is, one JSON datagram a second and when a terminal comes or goes
  (the gateway, its name, where it listens, the terminal and its counts, each channel's state).
  The HAT's ISOBUS page shows it.
- Under systemd it is `Type=notify` with a watchdog: [`linux/aow-linkd.service`](linux/aow-linkd.service)
  is an example unit. [`linux/aow-linkd.avahi.service`](linux/aow-linkd.avahi.service) announces it
  by DNS-SD (`_aowlink._tcp`) for `AowLinkBrowser`.
- One terminal at a time; a second is refused with CLOSE busy and the first one's name. Up to four
  monitors (listen-only clients) see every frame both ways.

Tests: `linux/tests/test_aow_link.cpp` (the protocol, no sockets), and
`linux/tests/link_probe.py` on vcan (frames both ways with kernel times, the drain on connect,
STATUS following the interface, a late interface, the status reports; with
`bench/aowlink_check.py` as a second argument, the conformance check too). CI runs both.

`aow_link.cpp` (`aow_link.h`) is the protocol's gateway end with no sockets and no Linux in it:
bytes, frames and the time in, what each client is owed out. Another platform's gateway can use it
as it is.

Came from AgOpenWeb-HAT (2026-10-26), where it was written for the HAT; relicensed MIT by its
author for this repo. What is the HAT's own (its unit, its settings page, the status on its
ISOBUS page) stays there.
