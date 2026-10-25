// Self-test of the C# classes, VirtualTerminalStack and VirtualTerminal: the host's view. Its frames are looped to the test
// implement on channel 1 (upstream's VT client, inside the native library), as in IsoShimSpike.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgIsoStack;
using AgIsoStack.Interop;

public static class VirtualTerminalTest
{
    /// <summary>Counts what the terminal asks of the store.</summary>
    sealed class CountingStore : IPoolStore
    {
        readonly MemoryPoolStore inner = new();
        public int Saves, Loads;
        public int Count => inner.Count;
        public bool Save(ulong name, ReadOnlySpan<byte> label, ReadOnlySpan<byte> data, bool append) { Saves++; return inner.Save(name, label, data, append); }
        public byte[]? Load(ulong name, ReadOnlySpan<byte> label) { var p = inner.Load(name, label); if (p != null) Loads++; return p; }
        public IEnumerable<byte[]> List(ulong name, int labelLength) => inner.List(name, labelLength);
        public bool Remove(ulong name, ReadOnlySpan<byte> label) => inner.Remove(name, label);
        public bool RemoveAll(ulong name, int labelLength) => inner.RemoveAll(name, labelLength);
    }

    static byte[] Pool(string name)
    {
        using Stream s = typeof(VirtualTerminalTest).Assembly.GetManifestResourceStream(name)!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    public static bool Run(Action<string> log) => Task.Run(() => RunAsync(log)).GetAwaiter().GetResult();

    static async Task<bool> RunAsync(Action<string> log)
    {
        log("--- the C# classes, VirtualTerminalStack and VirtualTerminal");
        int failures = 0;
        void Check(string what, bool pass)
        {
            if (!pass) failures++;
            log($"  {(pass ? "ok  " : "FAIL")} {what}");
        }
        var store = new CountingStore();
        byte[] pool = Pool("inputs.iop");

        // One terminal, start to stop. fromStore: the second, with the pool the first stored.
        async Task Session(bool fromStore)
        {
            long implementBytes = 0;
            var threads = new ConcurrentDictionary<int, bool>();
            var screens = new ConcurrentQueue<string>();
            var edits = new ConcurrentQueue<string>();
            IReadOnlyList<WorkingSetInfo>? sets = null;
            ulong? active = null;
            int address = -1, loaded = -1;
            bool inlineCall = false, failed = false;
            var stack = new VirtualTerminalStack(new VirtualTerminalOptions
            {
                Store = store,
                Send = (channel, id, data, extended) =>
                {
                    if (channel == 1) Interlocked.Add(ref implementBytes, data.Length);
                    unsafe { fixed (byte* p = data) IsoShim.isoshim_receive(1 - channel, id, p, data.Length, extended ? 1 : 0); }
                },
            });
            var vt = stack.Terminals[0];
            vt.Message += json =>
            {
                threads[Environment.CurrentManagedThreadId] = true;
                if (json.StartsWith("{\"type\":\"screen\"")) screens.Enqueue(json);
                if (json.StartsWith("{\"type\":\"edit\"")) edits.Enqueue(json);
            };
            vt.WorkingSetsChanged += (a, list) =>
            {
                threads[Environment.CurrentManagedThreadId] = true;
                active = a;
                sets = list;
                // A call from inside a handler runs at once, on this thread.
                inlineCall |= vt.InvokeAsync(() => true).IsCompleted;
            };
            vt.AddressClaimed += a => address = a;
            vt.PoolLoaded += (_, objects) => loaded = objects;
            stack.HandlerFailed += e => { failed = true; log($"  handler threw: {e.Message}"); };

            stack.Start();
            bool second = false;
            try { new VirtualTerminalStack(new VirtualTerminalOptions { Send = (_, _, _, _) => { } }).Start(); }
            catch (InvalidOperationException) { second = true; }
            Check("a second stack in the process is refused", second);
            await vt.InvokeAsync(() => { unsafe { fixed (byte* p = pool) return IsoShim.isoshim_test_implement(0, p, pool.Length); } });

            async Task<bool> Until(Func<bool> done, int ms = 15000)
            {
                var clock = Stopwatch.StartNew();
                while (!done() && clock.ElapsedMilliseconds < ms) await Task.Delay(5);
                return done();
            }
            bool up = await Until(() => active != null && sets?.Any(s => s.State == PoolState.Ready && s.Selectable == true) == true
                                        && screens.Any(s => s.Contains("\"mask\":1000,")));
            Check($"the implement is ready and shown{(fromStore ? ", its pool from the store" : "")}", up);
            Check($"address claimed: 0x{address:X2}; pool of {loaded} objects", address >= 0 && loaded == 25 && vt.Address == address);
            if (fromStore) Check($"loaded from the store, {implementBytes} bytes from the implement", store.Loads > 0 && implementBytes < 20000);
            else Check("stored", await Until(() => store.Saves > 0, 3000));
            if (up && !fromStore)
            {
                Check("select Rate open: true, an edit message", await vt.SelectAsync(9000, true) && edits.Any(e => e.Contains("\"o\":9000,\"kind\":\"number\"")));
                Check("Rate 101: Invalid", await vt.SetNumberAsync(9000, 101) == InputResult.Invalid);
                Check("Rate 80: Entered", await vt.SetNumberAsync(9000, 80) == InputResult.Entered);
                Check("Locked: NotAnInput", await vt.SetNumberAsync(9002, 1) == InputResult.NotAnInput);
                Check("Name hello: Invalid; WORLD: Entered", await vt.SetStringAsync(8000, "hello") == InputResult.Invalid
                                                         && await vt.SetStringAsync(8000, "WORLD") == InputResult.Entered);
                Check("the screen shows 40.0 and WORLD", await Until(() => screens.Any(s => s.Contains("\"s\":\"40.0\"") && s.Contains("\"s\":\"WORLD     \""))));
                Check("escape with nothing open: false", !await vt.EscapeAsync());
            }
            Check("a call from inside a handler runs at once", inlineCall);
            if (fromStore)
            {
                // Stop asked on the terminal's own thread (as when code resumes there after
                // awaiting something a handler completed): it stops after that step.
                await vt.InvokeAsync(() => { stack.Stop(); return 0; });
                Check("stop asked from the terminal's thread: stopped", await Until(() => !vt.IsRunning, 5000));
            }
            stack.Stop();
            Check("stopped", !vt.IsRunning && vt.Address == -1);
            bool refused = false;
            try { await vt.SoftKeyAsync(5000, KeyState.Pressed); }
            catch (InvalidOperationException) { refused = true; }
            Check("a call after stopping fails", refused);
            Check($"every event on the terminal's one thread, not the caller's ({threads.Count} thread)",
                threads.Count == 1 && !threads.ContainsKey(Environment.CurrentManagedThreadId));
            Check("no handler threw", !failed);
        }

        // Two terminals in one stack: each event reaches the terminal it concerns.
        async Task TwoTerminals()
        {
            bool tooMany = false;
            try { _ = new VirtualTerminalStack(new VirtualTerminalOptions { Send = (_, _, _, _) => { }, Count = 5 }); }
            catch (ArgumentException) { tooMany = true; }
            Check("five terminals: refused", tooMany);
            var stack = new VirtualTerminalStack(new VirtualTerminalOptions
            {
                Count = 2,
                Send = (channel, id, data, extended) =>
                {
                    unsafe { fixed (byte* p = data) IsoShim.isoshim_receive(1 - channel, id, p, data.Length, extended ? 1 : 0); }
                },
            });
            Check("two terminals, at 0 and 1", stack.Terminals.Count == 2 && stack.Terminals[0].Index == 0 && stack.Terminals[1].Index == 1);
            var sets = new IReadOnlyList<WorkingSetInfo>?[2];
            var shown = new ulong?[2];
            var screens = new[] { new ConcurrentQueue<string>(), new ConcurrentQueue<string>() };
            foreach (var t in stack.Terminals)
            {
                int i = t.Index;
                t.WorkingSetsChanged += (a, list) => { shown[i] = a; sets[i] = list; };
                t.Message += json => { if (json.StartsWith("{\"type\":\"screen\"")) screens[i].Enqueue(json); };
            }
            stack.Start();
            try
            {
                await stack.InvokeAsync(() => { unsafe { fixed (byte* p = pool) return IsoShim.isoshim_test_implement(1, p, pool.Length); } });
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 15000 && !(shown[1] != null && screens[1].Any(s => s.Contains("\"mask\":1000,"))))
                    await Task.Delay(5);
                Check("an implement for the second: shown there", shown[1] != null && screens[1].Any(s => s.Contains("\"mask\":1000,")));
                Check("and not on the first", shown[0] == null && sets[0]?.Count is null or 0);
                Check($"addresses 0x{stack.Terminals[0].Address:X2} and 0x{stack.Terminals[1].Address:X2}",
                    stack.Terminals[0].Address == 0x26 && stack.Terminals[1].Address == 0x27);
                Check("Rate 80 on the second: Entered; on the first: NotAnInput",
                    await stack.Terminals[1].SetNumberAsync(9000, 80) == InputResult.Entered
                    && await stack.Terminals[0].SetNumberAsync(9000, 80) == InputResult.NotAnInput);
                Check("the first can't show the second's implement", !await stack.Terminals[0].ActivateAsync(shown[1]!.Value));
            }
            finally { stack.Stop(); }
            Check("stopped: both", !stack.IsRunning && stack.Terminals.All(t => t.Address == -1));
        }

        // A key held down: the stack repeats it as held about every 200 ms, the host sends only the
        // press and its end; none while the link is down, and an abort when it is back.
        async Task HeldKeys()
        {
            var heard = new ConcurrentQueue<(long Ms, int State)>();
            var clock = Stopwatch.StartNew();
            bool shown = false;
            var stack = new VirtualTerminalStack(new VirtualTerminalOptions
            {
                Send = (channel, id, data, extended) =>
                {
                    unsafe { fixed (byte* p = data) IsoShim.isoshim_receive(1 - channel, id, p, data.Length, extended ? 1 : 0); }
                },
            });
            var vt = stack.Terminals[0];
            vt.Message += json =>
            {
                if (json.StartsWith("{\"type\":\"screen\"") && json.Contains("\"o\":5000,")) shown = true;
                if (!json.StartsWith("{\"type\":\"implement\",\"event\":\"key\"")) return;
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.GetProperty("o").GetInt32() == 5000)
                    heard.Enqueue((clock.ElapsedMilliseconds, doc.RootElement.GetProperty("state").GetInt32()));
            };
            byte[] vt3 = Pool("VT3TestPool.iop");
            stack.Start();
            try
            {
                await stack.InvokeAsync(() => { unsafe { fixed (byte* p = vt3) return IsoShim.isoshim_test_implement(0, p, vt3.Length); } });
                while (clock.ElapsedMilliseconds < 20000 && !shown) await Task.Delay(5);
                Check("the VT3 implement shown, with soft key 5000", shown);
                if (!shown) return;
                async Task<List<(long Ms, int State)>> Hold(int ms, Func<Task>? during = null)
                {
                    heard.Clear();
                    await vt.SoftKeyAsync(5000, KeyState.Pressed);
                    if (during != null) await during();
                    else await Task.Delay(ms);
                    return heard.ToList();
                }
                // Down 300 ms, the link gone for 600 ms of it, then back: an abort, no more held.
                var cut = await Hold(0, async () =>
                {
                    await Task.Delay(300);
                    stack.LinkDown();
                    await Task.Delay(50);
                    int before = heard.Count;
                    await Task.Delay(600);
                    Check($"no held while the link is down ({heard.Count - before})", heard.Count == before);
                    stack.LinkUp();
                    await Task.Delay(500);
                });
                Check($"the link back: the key aborted, and no held after ({string.Join(" ", cut.Select(h => h.State))})",
                    cut.Count >= 2 && cut[0].State == 1 && cut[^1].State == 3 && cut.Count(h => h.State == 3) == 1);
                // Down about a second, then released.
                var down = await Hold(1000);
                await vt.SoftKeyAsync(5000, KeyState.Released);
                await Task.Delay(500);
                var all = heard.ToList();
                var helds = all.Where(h => h.State == 2).Select(h => h.Ms).ToList();
                var gaps = helds.Zip(helds.Skip(1), (a, b) => b - a).ToList();
                Check($"held {helds.Count} times in a second, every {(gaps.Count > 0 ? $"{gaps.Min()} to {gaps.Max()}" : "-")} ms",
                    helds.Count is >= 3 and <= 6 && gaps.All(g => g is >= 150 and <= 400) && helds.Count > 0 && helds[0] - all[0].Ms is >= 150 and <= 400);
                Check($"pressed first, released last, nothing after ({string.Join(" ", all.Select(h => h.State))})",
                    all.Count >= 2 && all[0].State == 1 && all[^1].State == 0 && all.Count(h => h.State == 0) == 1);
            }
            finally { stack.Stop(); }
        }

        await Session(fromStore: false);
        await Session(fromStore: true);
        await TwoTerminals();
        await HeldKeys();
        log(failures == 0 ? "PASS: the C# classes" : $"FAIL: the C# classes, {failures} checks failed");
        return failures == 0;
    }
}
