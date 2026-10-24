# The bench

The development setup the results in docs/RESULTS.md come from: what is wired where, how the
network is set up and put back, how things are measured, and what has bitten. Device IDs, the
Apple team and the access point's key are in `bench/devices.env` and `dotnet/Local.props` (not
committed).

## Hardware

- **Gateway: the dev CM4** from AgOpenWeb-HAT (Raspberry Pi CM4 on the official IO board with the
  AOW HAT; Raspberry Pi OS Trixie Lite, PREEMPT_RT kernel, passwordless sudo). Three MCP2518FD
  CAN channels, `hatcan1` to `hatcan3`. **CAN2 (`hatcan2`) is wired to CAN3 (`hatcan3`)**, so the
  two make a real bus: the relay on CAN2, the test implement on CAN3. Which channels have their
  120 Ω termination jumpers closed is not recorded; CAN2 showed error passive (receive errors
  132) once early on, with no effect found since.
- **`aow-hatd` runs on the CM4 as a service** with its ISOBUS on CAN2. It is a VT client too: it
  uploads its own status screen (11,950 bytes, at 16 packets per DPO) to any terminal it finds,
  which is the "other implement 0xF7" in every run. Leave it running; it makes every run a
  two-implement case. **Its hardware watchdog is live while it runs: stop the service, never kill
  the process**, or the CM4 resets.
- **Terminals:** the Mac (console app; on the access point network only through the cable, see
  below); an iPad Pro 12.9 2nd generation (iPadOS 17.7, on USB to the Mac for `devicectl`); a
  Galaxy Tab S7 FE SM-T733 (Android 14, `adb` over USB). Both tablets are available for testing.
- **Kernel details on the CM4:** `txqueuelen` 10 on the CAN interfaces (the relay retries on
  `ENOBUFS`: `can_tx_retry` in its stats), `rmem_max` 4 MB, no overruns in the mcp251xfd
  counters.

## Networks

**Home Wi-Fi (two hops, other traffic).** The CM4's normal state: `wlan0` on the home network,
ssh at its home address, NTRIP and AgOpenWeb working. Every round trip crosses the air four
times and shares the channel. Variable from minute to minute: good for finding problems, poor
for comparing runs.

**The CM4 as access point (one hop).** Set up 2026-10-24 with the user's go-ahead, for repeatable
runs:

- NetworkManager profile `bench-ap`: SSID `AOW-BENCH`, WPA2 with the key in `devices.env`,
  5 GHz channel 149, regulatory domain US, IPv4 shared at `10.77.0.1/24` (the tablets get
  10.77.0.x by DHCP), `autoconnect no`. So **a reboot puts the CM4 back on home Wi-Fi.**
- While the access point is up the CM4 has no internet: NTRIP is off.
- ssh then goes over a cable: profile `bench-eth0` puts `eth0` at static `192.168.77.2/24`
  (priority 10, never the default route), to the Mac's built-in Ethernet (`en0`) at
  `192.168.77.1`. The `netplan-eth0` DHCP profile is left alone.
- Up: `sudo nmcli connection up bench-ap`. Back to home Wi-Fi: `sudo nmcli connection down
  bench-ap`, or reboot.
- **Change the CM4's network setup only with the user's say-so.**

**Repeatable bad networks: netem** (`bench/netem.sh`, on the gateway). Shapes only the relay's
TCP port (2323) in both directions (egress on the device, ingress through `ifb0`), so ssh, NTRIP
and the app are untouched. `NETEM_DEV=wlan0` (default) or `eth0` for the Mac on the cable.
`netem.sh set "delay 25ms 10ms loss 1%"`, `netem.sh stall AFTER FOR` (a full cut in the
background), `netem.sh off`. The approach agreed: develop against the hard network, and use the
access point plus netem when runs have to be compared.

## Running

`bench/devices.env` (from `devices.env.example`), then `bench/deploy.sh` copies the relay,
capture, checks, netem, `gateway.sh`, the pool and the built implements to `~/vtbench` on the
gateway. `bench/run.sh CLIENT NAME [IMPLEMENT]` runs one: relay up, the terminal app started,
the implement started 4 s later, the verdict awaited, every log gathered into `bench/out/NAME.txt`
and the relay's frame dump kept on the gateway as `dump-NAME.log`. `sweep.sh` runs N under one
impairment, `verify.sh` N without the capture with a frame-loss check, `selftest.sh` the
in-process self-test on the tablets.

**The test implements** (`bench/implement/build.sh`, Linux arm64; on the Mac in AgOpenWeb-HAT's
`aow-hat-dev` container): upstream's VT example at 16 packets per DPO (`VT3ExampleTarget`) and
at 255 (`VT3Example255`), both with upstream PR #793. They upload upstream's
`VT3TestPool.iop` (149,645 bytes) and switch to alarm mask 2000 on soft key 5000; the terminal
presses it once the pool is up, which makes the run's verdict. The terminal treats the implement
with NAME function code 16 as the one under test.

## Measuring

- **The terminal's own report** (`IsoShimBench`): upload attempts per source (request-to-send
  frames aimed at it), ETP aborts with their reasons, ping round trip percentiles, the longest gap
  between arrivals, the stack's warnings.
- **The relay** logs every frame both ways (`dump.log`: time, direction, id, data) and its stats:
  frames each way, `can_tx_retry`, `can_rx_dropped` (from `SO_RXQ_OVFL`).
- **The capture** (`sniff.py`): every frame on both CAN interfaces with kernel timestamps
  (`SO_TIMESTAMP`, 29) and drop counts (`SO_RXQ_OVFL`, 40; Python has neither constant), 4 MB
  receive buffer. Start it before the client, or early faults are missed.
- **`etpcheck.py`**: every ETP batch (DPO and its data packets 1..n) per interface in a capture
  or a relay dump; reports a batch with a packet missing and the aborts. A batch cut off at the
  end of a relay dump shows as bad: that one is an artefact.

Finding a lost frame, as done for the transmit-queue race: capture on CAN3 (the far end of the
wire) and CAN2 at once, count receive drops in the relay, repeat until a run fails, then see on
which side of the wire the packet is missing.

## Gotchas

- **Run the scripts with bash.** zsh does not split `$VAR` holding a command and its options
  (`ADB="adb -s X"`), and runs were wasted on it.
- **iOS local network permission:** the first run of the test app on the iPad shows a prompt that
  someone has to tap Allow on; until then it cannot reach the relay.
- `xcrun devicectl ... --console` prints "Waiting for the application to terminate": do not match
  "terminat" when looking for a crash (`libc++abi` or `signal 6` instead).
- Never let a `pkill -f` pattern match the shell running it (`[s]niff.py`).
- .NET's iOS workload with a newer Xcode needs `-p:ValidateXcodeVersion=false` (in
  `dotnet/Local.props`). Android NDK 30 is installed on the Mac.
- The App ID `com.agopenweb.isostack-tests` exists in the Apple account for the test app. **Never
  sign a test as `com.agopenweb.ios`**: it would replace the AgOpenWeb app installed on the iPad.
