// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors
//
// Self-test of the slcan adapter link: SlcanLink attached to VirtualTerminalStack, through a
// simulated adapter whose bus is the self-test's implement (channel 1, upstream's VT client in
// the native library). Checks the adapter's setup commands, frames both ways in slcan text, the
// replies and lines that are not frames, an unplug and a replug (a key held when it went is
// aborted, the claims go out again), and taking the adapter off the bus on stop.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpIsoTerm;
using SharpIsoTerm.Interop;

public static class SlcanLinkTest
{
    /// <summary>An slcan adapter in memory, enough of one to test the link: it answers like a
    /// Lawicel adapter (CR for done, BEL for an error, Z after a sent extended frame) and puts
    /// the frames it sends on the test implement's channel.</summary>
    sealed class FakeAdapter
    {
        readonly object gate = new();
        readonly StringBuilder fromHost = new();
        readonly Queue<byte> toHost = new();
        public volatile bool Plugged = true;
        public bool OnBus;
        public readonly ConcurrentQueue<string> Commands = new();   // every line the host wrote
        public readonly ConcurrentQueue<byte[]> Frames = new();     // frames the host sent: id (4), data
        public int Opens;

        public ISerialPort Port() => new FakePort(this);

        void Reply(string text)
        {
            lock (gate) foreach (var c in Encoding.ASCII.GetBytes(text)) toHost.Enqueue(c);
        }

        /// <summary>Raw text from the adapter to the host, as if from the bus.</summary>
        public void Inject(string text) => Reply(text);

        /// <summary>A frame from the bus (the implement) to the host, while on the bus.</summary>
        public void FromBus(uint id, ReadOnlySpan<byte> data)
        {
            if (!OnBus || !Plugged) return;
            var line = new StringBuilder($"T{id:X8}{data.Length}");
            foreach (var b in data) line.Append(b.ToString("X2"));
            Reply(line.Append('\r').ToString());
        }

        void Line(string line)
        {
            Commands.Enqueue(line);
            if (line.Length == 0) return;
            switch (line[0])
            {
                case 'C': OnBus = false; Reply("\r"); break;
                case 'V': Reply("V1013\r"); break;
                case 'S': Reply(line == "S5" ? "\r" : "\a"); break;
                case 'O': OnBus = true; Reply("\r"); break;
                case 'T':
                    if (!OnBus || line.Length < 10) { Reply("\a"); break; }
                    uint id = Convert.ToUInt32(line.Substring(1, 8), 16);
                    int dlc = line[9] - '0';
                    var data = Convert.FromHexString(line.Substring(10, dlc * 2));
                    Frames.Enqueue(BitConverter.GetBytes(id).Concat(data).ToArray());
                    // Onto the bus: the implement on channel 1.
                    unsafe { fixed (byte* p = data) IsoShim.isoshim_receive(1, id, p, data.Length, 1); }
                    Reply("Z\r");
                    break;
                default: Reply("\a"); break;
            }
        }

        sealed class FakePort(FakeAdapter a) : ISerialPort
        {
            bool open;

            public void Open()
            {
                if (!a.Plugged) throw new IOException("not plugged in");
                open = true;
                a.Opens++;
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                for (var end = Environment.TickCount64 + 100; ;)
                {
                    if (!a.Plugged || !open) throw new IOException("unplugged");
                    lock (a.gate)
                    {
                        int n = 0;
                        while (n < count && a.toHost.Count > 0) buffer[offset + n++] = a.toHost.Dequeue();
                        if (n > 0) return n;
                    }
                    if (Environment.TickCount64 >= end) return 0;
                    Thread.Sleep(2);
                }
            }

            public void Write(byte[] buffer, int offset, int count)
            {
                if (!a.Plugged || !open) throw new IOException("unplugged");
                var lines = new List<string>();
                lock (a.gate)
                {
                    foreach (char c in Encoding.ASCII.GetString(buffer, offset, count))
                    {
                        if (c == '\r') { lines.Add(a.fromHost.ToString()); a.fromHost.Clear(); }
                        else a.fromHost.Append(c);
                    }
                }
                foreach (var line in lines) a.Line(line);
            }

            public void Dispose() => open = false;
        }
    }

    static byte[] Pool(string name)
    {
        using Stream s = typeof(SlcanLinkTest).Assembly.GetManifestResourceStream(name)!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    public static bool Run(Action<string> log) => Task.Run(() => RunAsync(log)).GetAwaiter().GetResult();

    static async Task<bool> RunAsync(Action<string> log)
    {
        log("--- an slcan adapter, SlcanLink");
        int failures = 0;
        void Check(string what, bool pass)
        {
            if (!pass) failures++;
            log($"  {(pass ? "ok  " : "FAIL")} {what}");
        }
        async Task<bool> Until(Func<bool> done, int ms = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < ms) await Task.Delay(5);
            return done();
        }
        // The frames the host sent, as (id, data).
        static uint Id(byte[] f) => BitConverter.ToUInt32(f, 0);
        // A soft key activation from the terminal for key 5000 (code 1 pressed, 3 aborted).
        static bool Key(byte[] f, byte code) =>
            f.Length >= 8 && (Id(f) & 0x00FF0000) == 0x00E60000 && f[4] == 0x00 && f[5] == code && f[6] == 0x88 && f[7] == 0x13;
        static bool OwnClaim(byte[] f) => (Id(f) & 0x00FFFFFF) == 0x00EEFF26;
        static bool ClaimRequest(byte[] f) =>
            f.Length >= 7 && (Id(f) & 0x00FFFF00) == 0x00EAFF00 && f[4] == 0x00 && f[5] == 0xEE && f[6] == 0x00;

        bool badRate = false;
        try { _ = new SlcanLink(() => throw new InvalidOperationException(), 300000); }
        catch (ArgumentException) { badRate = true; }
        Check("a bitrate slcan doesn't have: refused", badRate);

        var adapter = new FakeAdapter { Plugged = false };
        using var link = new SlcanLink(adapter.Port, 250000, "fake0");
        int ups = 0, downs = 0;
        string? info = null;
        LinkCloseReason lastReason = LinkCloseReason.ShuttingDown;
        var heard = new ConcurrentQueue<(uint Id, bool Extended, byte[] Data)>();
        link.FrameReceived += (_, id, data, extended, _) => heard.Enqueue((id, extended, data.ToArray()));
        var openFailures = new ConcurrentQueue<string>();
        link.OpenFailed += why => openFailures.Enqueue(why);

        ulong? active = null;
        IReadOnlyList<WorkingSetInfo>? sets = null;
        var screens = new ConcurrentQueue<string>();
        var stack = new VirtualTerminalStack(new VirtualTerminalOptions
        {
            PacketsPerCts = 255,
            // Channel 0 is the terminal's, to the adapter; channel 1 is the implement's, the adapter's bus.
            Send = (channel, id, data, extended) =>
            {
                if (channel == 0) link.Send(0, id, data, extended);
                else adapter.FromBus(id, data);
            },
        });
        var vt = stack.Terminals[0];
        vt.WorkingSetsChanged += (a, list) => { active = a; sets = list; };
        vt.Message += json => { if (json.StartsWith("{\"type\":\"screen\"")) screens.Enqueue(json); };
        stack.Attach(link);
        // After Attach, so the stack has queued a link change before the checks act on it.
        link.Connected += i => { info = i; Interlocked.Increment(ref ups); };
        link.Disconnected += (why, _) => { lastReason = why; Interlocked.Increment(ref downs); };
        stack.Start();
        try
        {
            link.Start();
            await Task.Delay(300);
            Check($"no adapter: the link stays down and says why, once (\"{link.LastError}\")",
                !link.IsConnected && link.LastError.Length > 0 && openFailures.Count == 1);
            adapter.Plugged = true;
            Check("plugged in: link up", await Until(() => ups == 1 && link.IsConnected, 3000));
            var setup = adapter.Commands.Where(c => c.Length > 0).Take(4).ToArray();
            Check($"setup: {string.Join(", ", setup)} (close, version, 250 kbit/s, open)", setup.SequenceEqual(new[] { "C", "V", "S5", "O" }));
            Check($"the adapter's version reported: \"{info}\"", info == "fake0 (V1013)");
            Check("the bus is asked for address claims", await Until(() => adapter.Frames.Any(ClaimRequest), 2000));

            // Lines that are not frames, and a standard frame with a timestamp: only the frame comes through.
            int before = heard.Count;
            adapter.Inject("z\rZ\r\a\rV1013\rt1232AABB1234\rT1FFFFFFF0\rTXYZ\rt12\r");
            Check("a standard frame (with a timestamp) and an empty extended one come through; replies and junk don't",
                await Until(() => heard.Count >= before + 2, 1000)
                && heard.Skip(before).Count() == 2
                && heard.Skip(before).First() is var f1 && f1.Id == 0x123 && !f1.Extended && f1.Data.SequenceEqual(new byte[] { 0xAA, 0xBB })
                && heard.Skip(before + 1).First() is var f2 && f2.Id == 0x1FFFFFFF && f2.Extended && f2.Data.Length == 0);

            byte[] pool = Pool("VT3TestPool.iop");
            await stack.InvokeAsync(() => { unsafe { fixed (byte* p = pool) return IsoShim.isoshim_test_implement(0, p, pool.Length); } });
            bool ready = await Until(() => sets?.Any(s => s.State == PoolState.Ready) == true, 20000);
            Check($"the implement's pool came through the adapter ({adapter.Frames.Count} frames to the bus)", ready);
            if (ready)
            {
                await vt.ActivateAsync(sets!.First(s => s.State == PoolState.Ready).Name);
                Check("its screen is shown", await Until(() => screens.Any(x => x.Contains("\"mask\":1000,"))));
                Check("soft key 5000 pressed: sent", await vt.SoftKeyAsync(5000, KeyState.Pressed));
                Check("the press reached the bus", await Until(() => adapter.Frames.Any(f => Key(f, 1)), 2000));

                // Unplugged: the link drops at once, and is back when the adapter is.
                adapter.Plugged = false;
                Check("unplugged: the link drops, reason lost", await Until(() => downs == 1, 2000) && lastReason == LinkCloseReason.Lost);
                Check("the release while unplugged is dropped", await vt.SoftKeyAsync(5000, KeyState.Released) && link.FramesDropped > 0);
                int cutAt = adapter.Frames.Count;
                adapter.Plugged = true;
                Check("plugged in again: the link is back", await Until(() => ups == 2, 3000));
                var sinceUp = adapter.Frames.ToArray().Skip(cutAt).ToList();
                Check("the adapter was set up again", adapter.Commands.Count(c => c == "O") == 2);
                Check("the terminal claims its address again, then asks for everyone's",
                    await Until(() => { var l = adapter.Frames.ToArray().Skip(cutAt).ToList(); int c = l.FindIndex(OwnClaim), r = l.FindIndex(ClaimRequest); return c >= 0 && c < r; }, 2000));
                Check("the held key is aborted", await Until(() => adapter.Frames.Any(f => Key(f, 3)), 2000));
                Check("the terminal still has the implement", await Until(() => active != null && sets?.Any(s => s.State == PoolState.Ready) == true, 5000));
            }
        }
        finally
        {
            link.Stop();
            stack.Stop();
        }
        Check("stopped: link down, the adapter taken off the bus", !link.IsConnected && !adapter.OnBus && adapter.Commands.Last() == "C");
        log(failures == 0 ? "PASS: the slcan adapter link" : $"FAIL: the slcan adapter link, {failures} checks failed");
        return failures == 0;
    }
}
