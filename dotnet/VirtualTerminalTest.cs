// Self-test of the C# class, VirtualTerminal: the host's view. Its frames are looped to the test
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
        log("--- the C# class, VirtualTerminal");
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
            VirtualTerminal? vt = null;
            vt = new VirtualTerminal(new VirtualTerminalOptions
            {
                Store = store,
                Send = (channel, id, data, extended) =>
                {
                    if (channel == 1) Interlocked.Add(ref implementBytes, data.Length);
                    unsafe { fixed (byte* p = data) IsoShim.isoshim_receive(1 - channel, id, p, data.Length, extended ? 1 : 0); }
                },
            });
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
                inlineCall |= vt!.InvokeAsync(() => true).IsCompleted;
            };
            vt.AddressClaimed += a => address = a;
            vt.PoolLoaded += (_, objects) => loaded = objects;
            vt.HandlerFailed += e => { failed = true; log($"  handler threw: {e.Message}"); };

            vt.Start();
            bool second = false;
            try { new VirtualTerminal(new VirtualTerminalOptions { Send = (_, _, _, _) => { } }).Start(); }
            catch (InvalidOperationException) { second = true; }
            Check("a second terminal in the process is refused", second);
            await vt.InvokeAsync(() => { unsafe { fixed (byte* p = pool) return IsoShim.isoshim_test_implement(p, pool.Length); } });

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
                await vt.InvokeAsync(() => { vt.Stop(); return 0; });
                Check("stop asked from the terminal's thread: stopped", await Until(() => !vt.IsRunning, 5000));
            }
            vt.Stop();
            Check("stopped", !vt.IsRunning && vt.Address == -1);
            bool refused = false;
            try { await vt.SoftKeyAsync(5000, KeyState.Pressed); }
            catch (InvalidOperationException) { refused = true; }
            Check("a call after stopping fails", refused);
            Check($"every event on the terminal's one thread, not the caller's ({threads.Count} thread)",
                threads.Count == 1 && !threads.ContainsKey(Environment.CurrentManagedThreadId));
            Check("no handler threw", !failed);
        }

        await Session(fromStore: false);
        await Session(fromStore: true);
        log(failures == 0 ? "PASS: the C# class" : $"FAIL: the C# class, {failures} checks failed");
        return failures == 0;
    }
}
