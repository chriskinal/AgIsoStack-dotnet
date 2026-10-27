# AOW link gateway: ESP32

The AOW link ([docs/TRANSPORT.md](../../docs/TRANSPORT.md) section 5) on an ESP32: the ESP32's
own CAN controller (TWAI) through a 3.3 V transceiver, carried over its own Wi-Fi access point to
a terminal. The protocol is `../linux/aow_link.cpp`, the same file `aow-linkd` runs; `src/main.cpp`
is the sockets, the bus, the Wi-Fi and the settings. MIT.

**Passes `bench/aowlink_check.py`** (2026-10-27, joined to the bench network: every check; the
monitor's frame check had no traffic to see) and is found by DNS-SD beside the HAT. Of five runs
on the firmware with the TCP bridge, three in a row passed; one saw 2 heartbeats in a second
(10 s of heartbeats measured after it: median gap 100 ms, worst 146, against the HAT's 105 and
121 on the same Wi-Fi), and one, by host name, saw a second control client closed without its
CLOSE busy. Neither came back; the second is not explained. On the
bench bus with the HAT's `hatcan3`: frames both ways, 11- and 29-bit, no errors or drops. **Not
yet run with a terminal and an implement through it.** Built with PlatformIO for a plain
ESP32 (`esp32dev`); first board: an ESP32-WROOM relay board with a TI VP230 (SN65HVD230)
breakout.

## Wiring

| ESP32 | To |
|---|---|
| GPIO17 | transceiver D (CTX on a VP230 breakout) |
| GPIO16 | transceiver R (CRX) |
| 3V3, GND | transceiver 3V3, GND |
| GPIO21, GPIO22 | optional: an I2C device (SDA, SCL), see the serial port below |

CANH and CANL to the bus. Most VP230 breakouts carry a 120 Ω terminator across CANH and CANL; it
belongs there only if the gateway is at an end of the bus. 250 kbit/s (ISO 11783).

## Building and flashing

```sh
pio run -d gateway/esp32 -t upload --upload-port /dev/cu.usbserial-XXXX
```

A board without auto-reset (most relay boards) has to be put in download mode by hand: hold BOOT,
tap EN, let go of BOOT; tap EN again after the upload.

## The network

By default its own WPA2 access point, `AOW gateway XXXX` (the end of its MAC address), password
`aowlink-XXXX`, at 192.168.4.1; change the password before it goes near a machine. It listens on
port 2323 and announces itself by DNS-SD as `_aowlink._tcp`, TXT `v=1`, named `ISOBUS gateway XXXX`
unless set. `sta` makes it join a network instead: a machine network or the bench's, never an
uplink to a farm or office network (TRANSPORT.md, Security). Wi-Fi power save is off. A join
that has not worked after 10 s starts again: after a cold start (EN, or power) the first one
often fails, and the ESP32 core's auto-reconnect does not retry a join that never succeeded.

## The serial port, and the bridge on TCP port 2324

115200 baud, one command a line, one line back. Lines starting with `#` are the gateway's log.
TCP port 2324 answers the same way to `?`, `r`, `s` and `status` (anything else: `e`), so a
program on the network reads the I2C device too: isobus-implement-sim's `aux_input
--joystick-tcp`. Two connections at once. The DNS-SD record carries the port in TXT, `i2c=2324`.
The settings are changed on the serial port only.

| Command | Answer |
|---|---|
| `status` | Wi-Fi mode and address, name, terminal, monitors, bus state, error counters, frames |
| `name <text>` | The name terminals show (after `restart`) |
| `ap <ssid> <password>` | Its own access point (after `restart`; password 8 to 63 characters, no spaces) |
| `sta <ssid> <password>` | Join that network instead (after `restart`) |
| `restart` | |
| `?`, `r <addr> <reg>`, `s` | An I2C register bridge on GPIO21/22 (`i2c-bridge 1`, a byte in hex or `e`, a bus scan), for isobus-implement-sim's `aux_input --joystick-serial` or `--joystick-tcp`: a joystick module on the same board. No part of the gateway. Several commands may be sent in one write; the answers come in order |

## Differences from aow-linkd

- One channel, `CAN`. A frame's `time` is when it was read from the driver's queue, not when it
  arrived: the driver keeps no receive time. Close enough for spacing, within about 1 ms.
- `rxDropped` is the driver's missed-frame count (its queue of 128 full).
- Bus-off is recovered by itself; STATUS says bus-off until the controller is back.
- A client more than 32 kB behind is dropped (aow-linkd: 1 MB), about a second of a full bus.
- Up to six connections at once: a terminal, four monitors, and one more to be told busy.
