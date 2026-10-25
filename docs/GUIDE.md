# Integration guide

How to put an ISOBUS virtual terminal into a .NET application with `AgIsoStack.VirtualTerminal`:
what the parts are, the code a host writes, and the rules it must keep. Every member, message and
field is in the reference, [API.md](API.md). For AI assistants, [llms.txt](../llms.txt) is the
short version.

## What you are building

An ISOBUS implement (a sprayer, a seeder) carries its own user interface, an **object pool**: masks,
soft keys, fields, pictures, in a binary format defined by ISO 11783-6. It uploads the pool to a
**virtual terminal** (VT) on the tractor's CAN bus, which shows it and sends the operator's input
back. This library is the terminal. Your application supplies the three things it cannot:

| You supply | The library does |
|---|---|
| A way to the bus: CAN frames in and out | The whole ISOBUS conversation: address claim, the VT protocol, transport of large messages, pool upload, parsing, storing and loading pool versions |
| A screen: a page that paints a list of shapes, and sends taps back | Turns the active mask into that list (positions, colours, text and numbers already worked out) |
| Storage for pools, if they should outlive the process | Calls your store when an implement asks the terminal to save or load a version |

Your application never interprets the pool. It forwards JSON to the page, paints it, and turns
the operator's touches into calls. You don't design any screens: implements bring their own.

Words used below:
- **Working set:** one connected implement, known by its 64-bit **NAME** (16 hex digits in JSON) and
  its address on the bus (0 to 253, can change).
- **Active working set:** the implement whose screen a terminal shows. One at a time per terminal;
  the operator picks it.
- **Several terminals:** a cab can have more than one VT (a display for the sprayer, another for
  boom levelling). Each is a terminal of its own on the bus, with its own NAME and address, and
  each implement chooses one, usually from a setting in its own menu. The library runs up to four
  (`Count`), each a `VirtualTerminal` with its own screen, events and calls.
- **Data mask / alarm mask:** a full screen. An implement shows an alarm mask to demand attention.
- **Soft key mask:** the column of keys beside the data mask, belonging to the same implement.
- **Object id:** every object in a pool has a 16-bit id. Taps and values are sent back by id.

```
 CAN bus ── gateway ──TCP── AowLink ──▶ VirtualTerminalStack
                               ▲              │   Terminals[0] ──JSON──▶ your page (vtpaint.js)
                               └── frames ────┘        ▲                       │
                                                       └──── calls ◀── taps ───┘
```

## Get the package

The package isn't on nuget.org yet. Build it from this repository:

```sh
native/build.sh mac        # and any of: ios android linux windows (see the README)
cd dotnet/AgIsoStack.VirtualTerminal && dotnet pack -c Release -o ~/nuget-local
dotnet nuget add source ~/nuget-local -n local    # once
```

```xml
<PackageReference Include="AgIsoStack.VirtualTerminal" Version="0.1.0" />
```

The package includes a native library for every platform that was built when it was packed:

| Platform | Runtime | Notes |
|---|---|---|
| macOS 12+ | `osx` (arm64 and x64) | |
| Linux | `linux-x64`, `linux-arm64` | glibc 2.36 or later (Raspberry Pi OS bookworm, Debian 12); not musl |
| Windows 10+ | `win-x64` | needs only the Windows C runtime (UCRT); no arm64 yet |
| iOS 15+ | `ios-arm64` | a static library, linked into the app by the package's build targets; full AOT works |
| Android 6+ (API 23) | arm64-v8a, armeabi-v7a, x86_64 | inside the package |

Target `net10.0`, `net10.0-ios` or `net10.0-android`. A `DllNotFoundException` means the
package has no library for your runtime (repack after `native/build.sh` for it).

## A minimal host

```csharp
using AgIsoStack;

var link = new AowLink("192.168.4.1", name: "My terminal");       // a gateway on the bus
var stack = new VirtualTerminalStack(new VirtualTerminalOptions
{
    Send = link.Send,                                              // the terminals' frames out
    Store = new FilePoolStore(Path.Combine(dataDir, "vt-pools")),  // pools kept across runs
    Log = (level, text) => Console.WriteLine($"[{level}] {text}"),
});
stack.Attach(link);                  // frames in, and the link's state (see "The bus")
stack.HandlerFailed += e => Console.WriteLine($"handler failed: {e}");
link.Disconnected += (why, text) => ShowBusGone(why, text);       // it reconnects by itself

var vt = stack.Terminals[0];                     // one terminal unless Count says more
vt.Message += json => page.Post(json);           // screens, editors, working sets, alarms
vt.Image += img => page.PostImage(img);          // pictures, before the screens that use them
vt.WorkingSetsChanged += (active, list) => ShowImplements(active, list);
vt.AlarmsChanged += alarms => ShowAlarms(alarms);
vt.Audio += signal => Beep(signal);

stack.Start();
link.Start();
// ... the application runs; the page's taps become calls (see "Input") ...
link.Stop();
stack.Stop();
```

That is the whole of the setup: an implement plugged into the bus now claims its address, finds
the terminal, uploads its pool (or loads it from the store), and the screen arrives as a `screen`
message.

`page.Post` stands for however your application reaches its UI: a web view's message bridge, a
WebSocket, or a native canvas in the same process. The library doesn't care.

### Options

All have defaults; most hosts set only `Send` and `Store`.

| Option | Default | What it is |
|---|---|---|
| `Send` | required | Where the terminals' frames go. Called on the stack's thread: queue the frame and return. |
| `Store` | in memory | Pool versions, one store for all the terminals (see "Storage"). |
| `Count` | 1 | Terminals, 1 to 4 (see "Several terminals"). |
| `Identity`, `Manufacturer`, `FunctionInstance` | 3, 1407, 0 | The first terminal's NAME on the bus; each next terminal has the next function instance. Give each product its own identity; terminals of two products on one bus also need different function instances. |
| `PreferredAddress` | 0x26 | The address the first terminal claims first (0x26 is the primary VT); the next one more. |
| `MaskWidth`, `MaskHeight` | 480, 480 | The data mask in pixels, as reported to implements. Implements lay out their screens for this size; the page scales the result. |
| `KeyWidth`, `KeyHeight` | 80, 80 | One soft key in pixels. |
| `PhysicalKeys`, `VirtualKeys`, `NavigationKeys` | 6, 64, 0 | Soft keys shown at once, keys a mask may hold, navigation keys. |
| `Version` | 5 | VT version reported, 3 to 6. |
| `SmallFonts`, `LargeFonts` | 0xFF, 0xFF | Font sizes reported as supported. |
| `PacketsPerCts` | 255 | Frames per clear-to-send in a transfer. 255 makes uploads several times faster over a network link; keep it. |
| `StepMilliseconds` | 5 | Longest gap between runs of the stack. A frame arriving wakes it at once. |
| `Log`, `LogLevel` | none, 2 | The stack's log, 0 debug to 4 critical, from `LogLevel` up. Called from the stack's thread and from a pool's parsing thread. |

### Several terminals

```csharp
var stack = new VirtualTerminalStack(new VirtualTerminalOptions { Send = link.Send, Store = store, Count = 2 });
foreach (var vt in stack.Terminals)
{
    var card = cards[vt.Index];                  // one screen on the page for each
    vt.Message += json => card.Post(json);
    vt.Image += img => card.PostImage(img);
    vt.Identify += () => card.ShowNumber(vt.Index + 1);
}
```

- Each terminal is a VT of its own on the bus: function instance `FunctionInstance + Index`,
  address `PreferredAddress + Index` (0x26, 0x27, ...). Implements see them all.
- **The implement chooses**, not the host: most offer a "VT number" or "preferred VT" setting in
  their own menu, and remember it. A new implement usually goes to the first terminal; the
  operator moves it from there. There is no call that moves an implement.
- Each terminal shows one implement at a time, as one display does; `ActivateAsync` switches among
  the implements on that terminal only. Calls and events are each terminal's own.
- `Identify` asks the host to show that terminal's number for about 3 s. Implements send it to
  every terminal while the operator is choosing one; show the number on each screen.
- Picture ids are unique across the terminals, so one picture store on the page will do.
- `Count` is fixed while the stack runs: to change it, stop the stack and make a new one. The
  implements see their terminal go and come back, and load their pools from the store.

## Threading: the rules

The stack owns one thread, which runs every terminal. These rules keep a host correct:

1. **Every event and every store call comes on the stack's thread, in order.** To touch UI,
   post to your UI thread (`Dispatcher.BeginInvoke`, `MainThread.BeginInvokeOnMainThread`,
   `SynchronizationContext.Post`). **Never use a synchronous invoke** (`Dispatcher.Invoke`,
   `InvokeOnMainThreadAsync(...).Wait()`) from a handler: if the UI thread is waiting on the
   stack at that moment, both stop.
2. **Keep handlers short.** The bus waits while a handler runs; a transfer stalls at one step per
   handler. Copy what you need and return.
3. **Calls work from any thread**, including from inside a handler (they run at once there). Each
   returns a task that completes when the stack has done it. Await them; don't block on
   `.Result` from a thread that a handler may be waiting on.
4. **A handler that throws doesn't stop the stack.** The exception goes to `HandlerFailed`.
   Subscribe to it, or failures in your handlers go unseen.
5. **`Receive` and `AowLink.Send` work from any thread** and return at once.
6. **One stack per process** (with up to four terminals). `Start` throws if another is running. A
   stopped stack can be started again.
7. **`Stop` waits** for the stack to finish, including any pool still parsing (up to a few
   seconds for a big pool). Called from the stack's own thread (a handler), it only asks and
   returns; the stack stops after the current step.

## The bus

### Through a gateway: `AowLink`

`AowLink` is the client for an AOW link gateway: a box on the CAN bus that carries every frame over
TCP (port 2323; the protocol is [TRANSPORT.md](TRANSPORT.md) section 5). AgOpenWeb-HAT's
`aow-linkd` is one; `bench/aowlink_gateway.py` is a reference gateway for any Linux machine with
SocketCAN.

- **It reconnects by itself.** After 1 s with nothing from the gateway it drops the link and tries
  again at once, then every 250 ms. `Connected` and `Disconnected` tell you, and
  `Disconnected` says why (`Busy`: the gateway already has a terminal; `Timeout`; `Lost`; ...).
- **Frames sent while the link is down are dropped**, never saved for later (`FramesDropped`
  counts them). The implements notice the terminal is gone after 3 s and reconnect when it comes
  back. A short cut usually goes unnoticed.
- **`stack.Attach(link)`** connects the two. When the link comes back after a cut, any key or button
  that was held is sent as aborted, so an implement never sees a press without its end.
- `StatusReceived` reports each bus's state and error counters; `RoundTrip` reports the
  heartbeat's round trip in ms. Both are for showing link health.
- The gateway's address is yours to find. Gateways announce themselves by DNS-SD as
  `_aowlink._tcp`; `AowLink` doesn't browse.
- On iOS, add `NSLocalNetworkUsageDescription` to `Info.plist`. On Android, the `INTERNET`
  permission.

### Any other way to the bus

Anything that moves CAN frames works: SocketCAN, a USB adapter, your own protocol.

```csharp
var stack = new VirtualTerminalStack(new VirtualTerminalOptions
{
    Send = (channel, id, data, extended) => adapter.Write(id, data.ToArray(), extended),
});
adapter.FrameReceived += (id, data, extended) => stack.Receive(id, data, extended);
```

- Frames are 29-bit (extended) ISOBUS frames on channel 0; the bus is 250 kbit/s.
- `Send` runs on the stack's thread: copy the span (it is valid only during the call), queue
  the frame and return.
- Deliver received frames promptly, in order. Hold-ups of more than a few hundred milliseconds
  make implements time out a transfer.
- If your link can drop, call `stack.LinkDown()` when it goes and `stack.LinkUp()` when it is up,
  the first time too: each terminal then sends its address claim again and asks the bus for
  every address claim. Both go out at start, possibly before the link is up, and a terminal and
  an implement that never heard each other's claim don't talk.

## The page

The terminal flattens the active screen into a **draw list**: plain shapes in pixel coordinates,
painted in order. The page needs no ISOBUS knowledge. `painter/vtpaint.js` (Canvas 2D, MIT, about
230 lines) paints it and is the place to start; port it if your UI is not a browser.

### Messages to forward

`vt.Message` carries every message as one JSON object, `"type"` first. Forward `screen` and `edit`
to the page unchanged; the others are also raised as typed events for the host.

| `type` | When | Typical use |
|---|---|---|
| `screen` | The shown screen changed in any way | Paint it |
| `edit` | An input field opened or closed | Show or close an editor |
| `workingSets` | An implement came, went, or its pool progressed | An implement switcher, upload progress (also `WorkingSetsChanged`) |
| `alarms` | An implement raised or cleared an alarm mask | An alarm banner (also `AlarmsChanged`) |
| `audio`, `volume` | An implement asked for a sound | Play it (also `Audio`, `Volume`) |
| `identify` | An implement asked the terminals to show their numbers | Show this terminal's for about 3 s (also `Identify`) |

Pictures come separately through `vt.Image`, once each and before the first screen that uses
them, as RGBA pixels. Send them to the page in any form (base64 in JSON is fine) and keep them
for as long as the terminal runs: later screens refer to them only by id.

### Painting

```js
const images = new VtImages();            // one per page (ids are unique across terminals)
let screen = null;

function onHostMessage(m) {               // whatever your bridge delivers
  if (m.type === "image") images.add(m.id, m.w, m.h, base64ToBytes(m.rgba));   // your own format
  else if (m.type === "screen") { screen = m; paint(); }
  else if (m.type === "edit") m.o === null ? closeEditor() : openEditor(m);
}

function paint() {
  if (!screen || !screen.ws || !screen.kind) return showNoImplement();
  const s = canvas.width / screen.w;      // scale the mask to the canvas
  ctx.setTransform(s, 0, 0, s, 0, 0);
  vtPaintMask(ctx, screen, images);
  const keys = screen.keys;               // null: the mask has no soft keys
  (keys ? keys.keys : []).forEach((key, i) => {
    const kc = keyCanvases[i].getContext("2d");
    kc.setTransform(keyCanvases[i].width / keys.w, 0, 0, keyCanvases[i].height / keys.h, 0, 0);
    vtPaintKey(kc, keys, i, images);      // null entries paint as an empty key
  });
}
```

- `screen.ws` null: no implement is shown. `screen.kind` null: the implement has no mask up yet.
- Scale the whole mask uniformly; the coordinates are the terminal's `MaskWidth` x `MaskHeight`.
- Soft keys come in `screen.keys.keys`, in order. Show at least `PhysicalKeys` of them. A mask
  may hold more than fit (up to `VirtualKeys`), and how to page through the rest is up to you.
- Text uses fixed font cells (`fw` x `fh`). The painter stretches a monospace face to the cell,
  as other terminals do.
- An object the renderer doesn't draw arrives as a `box` with a `label`, so it shows as a box
  instead of vanishing.
- The same screen is never sent twice in a row. Any change sends the whole list again; just
  repaint.

### Touch

`vtHit(screen.items, x, y)` (in mask pixels) gives the topmost touchable item under a point. Its
`kind` says what it is. Use it only when `on` is true (enabled):

| Touched | Call |
|---|---|
| `kind: "button"` | `ButtonAsync(o, Pressed)` on touch down, `Released` on touch up, `Aborted` if the touch slides off or is cancelled |
| `kind: "input"` | `SelectAsync(o, open: true)`: the field opens and an `edit` message follows |
| A soft key cell `i` | `SoftKeyAsync(screen.keys.keys[i].o, Pressed)`, then `Released` or `Aborted` the same way |

A `hit` also carries `sel` (has the focus), `open` (being edited) and, for latching buttons,
`latch` and `latched`. Draw a focus ring or the latched state from these if you like.

## Input

### Soft keys and buttons: press, hold, release

Send `Pressed` when the touch goes down, and `Released` when it comes up (or `Aborted` if it
slides off or is cancelled). Every press must end in one of the two:

```csharp
await vt.SoftKeyAsync(5000, KeyState.Pressed);     // touch down
await vt.SoftKeyAsync(5000, KeyState.Released);    // touch up
```

While a key or button is down, the stack reports it to the implement as **held** about every
200 ms (`VirtualTerminalStack.HeldRepeatMilliseconds`), as the standard asks, so the host doesn't
repeat anything. A `Held` the host sends itself is accepted and restarts the count. The repeats
pause while the link is down; when it comes back, a key still down is sent as aborted. A key that
leaves the screen (the mask changed under the finger) stops repeating.

The calls return `true` when the message was sent. `false` means the object is not on the shown
screen (the screen changed under the finger). A call made while the terminal is stopped fails with
`InvalidOperationException`.

### Input fields

1. The operator taps an `input` hit: call `SelectAsync(o, open: true)`.
2. An `edit` message arrives describing the field (kind, current value, limits). Show an editor.
3. The operator enters a value: `SetNumberAsync` or `SetStringAsync`. Or cancels: `EscapeAsync()`.
4. Either closes the field, and an `edit` message with `"o": null` follows. Close the editor.

```json
{"type":"edit","ws":"A0001000AFE00002","o":9000,"kind":"number","value":50,"min":0,"max":100,"offset":0,"scale":0.5,"decimals":1,"text":"25.0"}
```

| `kind` | Show | Enter |
|---|---|---|
| `number` | `text`; values are **raw**: the operator sees `(raw + offset) * scale` to `decimals` places | `SetNumberAsync(o, raw)` with `min <= raw <= max`; convert back from what the operator typed: `raw = round(shown / scale) - offset` |
| `string` | `value`; at most `length` characters; `chars` with `only` true are the characters allowed, with `only` false those refused | `SetStringAsync(o, text)` |
| `bool` | `value` 0 or 1 | `SetNumberAsync(o, 0 or 1)`; a touch UI usually toggles without an editor |
| `list` | `options`: one draw list per entry, `w` x `h`, painted like a screen; `null` is an empty place | `SetNumberAsync(o, index)` |

The set calls return `InputResult.Entered` (sent to the implement), `Invalid` (out of range or
characters not allowed: the field stays open, so show the operator why) or `NotAnInput` (not an
enabled field on this screen). A value can be set without selecting first.

The implement can open a field itself, and it can cancel one with its ESC command. The same `edit`
messages follow, so drive the editor from the messages, not from your own calls.

### Choosing the implement

`WorkingSetsChanged` gives the implement the terminal shows (its NAME; `null`: none) and the ones
connected to that terminal, each with its address, pool state (`Waiting`, `Transferring` with `Progress` in percent, `Parsing`, `Ready`,
`Failed`) and object count. `ActivateAsync(name)` shows another one. While none is shown, the
first selectable implement whose pool is ready is shown by itself. An implement whose
`Selectable` is false asks not to be offered to the operator.

## Alarms and sound

`AlarmsChanged` lists every implement showing an alarm mask, shown on screen or not, each with
the mask, a priority (0 high, 1 medium, 2 low) and an acoustic signal (0 highest, 1 medium,
2 lowest, 3 none). The terminal **does not switch implements** for an alarm: show it over whatever
is open, and offer `ActivateAsync(alarm.Name)` to bring that implement up. The implement clears
the alarm by leaving the alarm mask (usually on its own acknowledge soft key), and an updated
list follows.

`Audio` is the implement's Control Audio Signal: sound `Activations` times at `FrequencyHz`, on
for `OnMilliseconds`, off for `OffMilliseconds`; `Activations` 0 stops a signal that is sounding.
`Volume` is a percentage for later sounds from that implement.

## Storage

Implements upload their pool once, then ask the terminal to store it under a **version label**.
Next time they connect they ask for the label, and the terminal loads it from your store, which
takes about a second instead of an upload (about 15 s for a 150 kB pool at 250 kbit/s).

- `FilePoolStore(directory)` keeps `DIRECTORY/NAME/LABEL.iop`. Use your app's data folder, one
  that survives updates.
- With no store, pools are kept **in memory** for the life of the process. Don't replace that
  with a store that refuses: an implement told "not stored" drops its connection and uploads
  again.
- Your own `IPoolStore` (a database, the cloud) must implement five methods. They are called on
  the stack's thread, so keep them quick. `Save` may arrive in parts: the first with
  `append` false, the rest appended. Labels are 7 bytes, or 32 for VT 5 and 6 extended labels,
  and the two lengths are separate versions. An exception counts as a failure and goes to
  `HandlerFailed`.

## Lifecycle

- **Start** before the link, so no frame arrives at a stopped stack. `Start` returns once the
  terminals are running; each one's `AddressClaimed` follows when it has its address (about 250 ms after
  frames flow).
- **Stop** the link first, then the stack. `Stop` is safe at any moment, even mid-upload or
  while a pool is parsing.
- **App to background** (mobile): the simplest is to stop both and start both again when the
  app returns. The implements see a terminal that went away and came back, and load their pools
  from the store.
- `Dispose` is `Stop`.

## Troubleshooting

| Symptom | Look at |
|---|---|
| Nothing happens, no `AddressClaimed` | Frames aren't reaching the bus: `Send` is wired? `link.IsConnected`? `Disconnected` with `Busy` means another terminal holds the gateway. |
| An implement uploads every time it connects | The store isn't kept: the `FilePoolStore` folder is temporary or cleared, or a custom store returns false. |
| An implement uploads again after a few seconds | It lost the terminal for 3 s: a link cut, or a handler blocking the stack's thread. |
| Upload stalls or fails partway | Handlers or the store too slow; frames held up in your transport; `HandlerFailed`. Turn on `Log` at level 1. |
| `DllNotFoundException` / `EntryPointNotFoundException` | No native library for this runtime in the package. On iOS, reference the package (its build targets link the static library), not just the DLL. |
| "A terminal stack is already running in this process." | One per process: stop the first, or give one stack a higher `Count`. |
| An implement is on the wrong terminal | It chose it: change its VT setting in the implement's own menu. |
| A field gives `Invalid` | Number: the value is raw (before offset and scale) and outside `min`..`max`. String: too long, or characters the field refuses. |
| Something on screen is a dashed magenta box with a label | An object the renderer doesn't draw yet (see Limits). |

The stack's log (`Log`, level 1 or 0) shows the protocol. `AowLink.StatusReceived` shows bus
errors.

## Limits

- One stack per process, up to four terminals, on one bus (channel 0).
- **Not drawn yet** (shown as labelled boxes): animations, graphics contexts, scaled graphics,
  VT 6 window masks and key groups, and other newer objects. Fonts are approximations of the
  standard's fixed cells.
- **Auxiliary control** (joysticks assigned to implement functions) is not supported.
- On Show and On Hide macros don't run when the mask changes (an upstream gap); other macros do.
- The terminal reports VT version 5 by default; implements needing 6-only features may fall back
  or refuse.

Known upstream gaps and their status are in [PLAN.md](PLAN.md#upstream-gaps-in-the-vt-server).

## Going further

- [API.md](API.md): every type, member, message and draw-list field; the C interface for hosts not
  in .NET.
- [TRANSPORT.md](TRANSPORT.md): the gateway protocol, for writing a gateway.
- `dotnet/VirtualTerminalTest.cs`: the self-test that drives the classes as a host would, including
  inputs, stores, two terminals and stopping from either thread. A working example of everything above.
- `painter/index.html`: a page that paints screens with `vtpaint.js`.
