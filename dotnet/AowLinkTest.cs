// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors
//
// Self-test of the gateway link: AowLink attached to VirtualTerminalStack, against a scripted gateway on
// loopback whose bus is the self-test's implement (channel 1, upstream's VT client in the native
// library). Checks the hello, frames both ways, the round trip, a gateway gone silent, refusals
// while busy, reconnecting, and that a soft key held when the link went is aborted when it is back.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpIsoTerm;
using SharpIsoTerm.Interop;

public static class AowLinkTest
{
    /// <summary>A gateway on loopback, enough of one to test the client: one connection at a time.</summary>
    sealed class FakeGateway : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly Thread thread;
        readonly object sendLock = new();
        readonly Stopwatch clock = Stopwatch.StartNew();
        volatile Socket? current;
        volatile bool stopping;
        public volatile bool Silent;   // the current connection hears nothing more from us
        public volatile int RefuseBusy; // refuse this many connections with CLOSE busy
        public int Connections, Refused;
        public readonly ConcurrentQueue<byte[]> FromClient = new(); // FRAME bodies
        public string? ClientName;
        public Action<string>? Trace;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public FakeGateway()
        {
            listener.Start();
            thread = new Thread(Run) { IsBackground = true, Name = "fake-gateway" };
            thread.Start();
        }

        static byte[] Msg(byte type, ReadOnlySpan<byte> body)
        {
            var m = new byte[2 + body.Length];
            m[0] = type;
            m[1] = (byte)body.Length;
            body.CopyTo(m.AsSpan(2));
            return m;
        }

        void Send(Socket s, byte[] m)
        {
            lock (sendLock) { try { s.Send(m); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { } }
        }

        /// <summary>A frame from the bus (the implement) to the client. False: dropped (no
        /// connection, or silent).</summary>
        public bool ToClient(uint id, ReadOnlySpan<byte> data, bool extended)
        {
            var s = current;
            if (s == null || Silent) return false;
            var body = new byte[10 + data.Length];
            body[1] = (byte)(extended ? 1 : 0);
            BitConverter.TryWriteBytes(body.AsSpan(2, 4), id);
            BitConverter.TryWriteBytes(body.AsSpan(6, 4), (uint)(clock.Elapsed.TotalMilliseconds * 1000));
            data.CopyTo(body.AsSpan(10));
            Send(s, Msg(2, body));
            return true;
        }

        void Run()
        {
            while (!stopping)
            {
                Socket s;
                try { s = listener.AcceptSocket(); }
                catch (Exception) { return; }
                // A connection the client already gave up on can throw even here (macOS: EINVAL).
                Trace?.Invoke("gateway: accepted");
                try { s.NoDelay = true; Serve(s); Trace?.Invoke("gateway: connection over"); }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException) { Trace?.Invoke($"gateway: connection over, {e.GetType().Name} {e.Message}"); }
                current = null;
                try { s.Close(); } catch { }
            }
        }

        void Serve(Socket s)
        {
            var buf = new byte[65536];
            int have = 0;
            s.ReceiveTimeout = 50;
            byte[]? Next()
            {
                while (true)
                {
                    if (have >= 2 && have >= 2 + buf[1])
                    {
                        var m = buf.AsSpan(0, 2 + buf[1]).ToArray();
                        Buffer.BlockCopy(buf, m.Length, buf, 0, have - m.Length);
                        have -= m.Length;
                        return m;
                    }
                    int n;
                    try { n = s.Receive(buf, have, buf.Length - have, SocketFlags.None); }
                    catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) { return null; }
                    if (n == 0) throw new IOException("closed");
                    have += n;
                }
            }
            byte[]? hello = null;
            for (var end = clock.ElapsedMilliseconds + 2000; hello == null && clock.ElapsedMilliseconds < end;) hello = Next();
            if (hello == null || hello[0] != 1) return;
            ClientName = Encoding.UTF8.GetString(hello, 9, hello[8]);
            if (RefuseBusy > 0)
            {
                RefuseBusy--;
                Refused++;
                Send(s, Msg(5, new byte[] { 2, 4, (byte)'b', (byte)'u', (byte)'s', (byte)'y' }));
                return;
            }
            Connections++;
            Silent = false;
            var name = Encoding.UTF8.GetBytes("fake");
            var channel = Encoding.UTF8.GetBytes("fake0");
            var body = new List<byte>(Encoding.ASCII.GetBytes("AOWL")) { 1, 0, (byte)name.Length };
            body.AddRange(name);
            body.Add(1);
            body.AddRange(BitConverter.GetBytes(250000));
            body.Add(0);
            body.Add((byte)channel.Length);
            body.AddRange(channel);
            Send(s, Msg(1, body.ToArray()));
            Send(s, Msg(4, new byte[12]));
            current = s;
            uint heardTime = 0;
            long heardAt = -1, nextBeat = 0;
            while (!stopping)
            {
                long now = clock.ElapsedMilliseconds;
                if (!Silent && now >= nextBeat)
                {
                    nextBeat = now + 100;
                    var beat = new byte[10];
                    BitConverter.TryWriteBytes(beat.AsSpan(0, 4), (uint)Math.Max(1, now));
                    BitConverter.TryWriteBytes(beat.AsSpan(4, 4), heardAt < 0 ? 0u : heardTime);
                    BitConverter.TryWriteBytes(beat.AsSpan(8, 2), (ushort)(heardAt < 0 ? 0 : now - heardAt));
                    Send(s, Msg(3, beat));
                }
                var m = Next();
                if (m == null) continue;
                if (m[0] == 3)
                {
                    heardTime = BitConverter.ToUInt32(m, 2);
                    heardAt = clock.ElapsedMilliseconds;
                }
                else if (m[0] == 2)
                {
                    var frame = m.AsSpan(2).ToArray();
                    FromClient.Enqueue(frame);
                    // Onto the bus: the implement on channel 1.
                    uint id = BitConverter.ToUInt32(frame, 2);
                    unsafe { fixed (byte* p = &frame[10]) IsoShim.isoshim_receive(1, id, p, frame.Length - 10, frame[1] & 1); }
                }
                else if (m[0] == 5) return;
            }
        }

        public void Drop() { try { current?.Close(); } catch { } }

        public void Dispose()
        {
            stopping = true;
            listener.Stop();
            Drop();
            thread.Join(2000);
        }
    }

    static byte[] Pool(string name)
    {
        using Stream s = typeof(AowLinkTest).Assembly.GetManifestResourceStream(name)!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    public static bool Run(Action<string> log) => Task.Run(() => RunAsync(log)).GetAwaiter().GetResult();

    static async Task<bool> RunAsync(Action<string> log)
    {
        log("--- the gateway link, AowLink");
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
        // A soft key activation from the terminal for key 5000: function 0, code (1 pressed, 3 aborted), id 5000.
        static bool Key(byte[] frame, byte code) =>
            frame.Length >= 14 && frame[10] == 0x00 && frame[11] == code && frame[12] == 0x88 && frame[13] == 0x13;
        // The terminal's address claim: PGN 0xEE00 from 0x26 to everyone.
        static bool OwnClaim(byte[] frame) =>
            frame.Length >= 10 && (BitConverter.ToUInt32(frame, 2) & 0x00FFFFFF) == 0x00EEFF26;
        // A request for address claim to everyone: PGN 0xEA00 to 0xFF asking for PGN 0xEE00.
        static bool ClaimRequest(byte[] frame) =>
            frame.Length >= 13 && (BitConverter.ToUInt32(frame, 2) & 0x00FFFF00) == 0x00EAFF00
            && frame[10] == 0x00 && frame[11] == 0xEE && frame[12] == 0x00;

        using var gateway = new FakeGateway();
        using var link = new AowLink("127.0.0.1", gateway.Port, "isoshim self-test");
        int ups = 0, downs = 0, statuses = 0;
        LinkCloseReason lastReason = LinkCloseReason.Lost;
        var rtts = new ConcurrentQueue<double>();
        var timeouts = new List<double>();
        var downAt = Stopwatch.StartNew();
        GatewayInfo? info = null;
        link.StatusReceived += _ => Interlocked.Increment(ref statuses);
        link.RoundTrip += ms => rtts.Enqueue(ms);

        ulong? active = null;
        IReadOnlyList<WorkingSetInfo>? sets = null;
        var screens = new ConcurrentQueue<string>();
        // What happened, for a check that fails: the working sets as they changed, and the stack's warnings.
        var clock = Stopwatch.StartNew();
        var trace = new ConcurrentQueue<string>();
        long maintainedAt = -1;
        // Each cut starts just after the implement's working set maintenance (once a second) has
        // crossed the link: the terminal drops a working set it hears none from for 3 s (ISO
        // 11783-6), and a cut of 1.3 s at the wrong moment of that second swallows two of them.
        async Task<bool> Maintained()
        {
            long since = clock.ElapsedMilliseconds;
            bool crossed = await Until(() => Interlocked.Read(ref maintainedAt) > since, 2000);
            await Task.Delay(50);
            return crossed;
        }
        gateway.Trace = text => trace.Enqueue($"{clock.ElapsedMilliseconds,6} ms  {text}");
        var stack = new VirtualTerminalStack(new VirtualTerminalOptions
        {
            PacketsPerCts = 255,
            Log = (level, text) => trace.Enqueue($"{clock.ElapsedMilliseconds,6} ms  log {level}: {text}"),
            LogLevel = 2,
            // Channel 0 is the terminal's, to the gateway; channel 1 is the implement's, the gateway's bus.
            Send = (channel, id, data, extended) =>
            {
                if (channel == 0) link.Send(0, id, data, extended);
                else if (gateway.ToClient(id, data, extended) && ((id >> 8) & 0xFF00) == 0xE700 && data.Length > 0 && data[0] == 0xFF)
                    maintainedAt = clock.ElapsedMilliseconds;  // the implement's working set maintenance, across the link
            },
        });
        var vt = stack.Terminals[0];
        vt.WorkingSetsChanged += (a, list) =>
        {
            active = a;
            sets = list;
            if (list.Any(w => w.State == PoolState.Transferring)) return;  // progress
            trace.Enqueue($"{clock.ElapsedMilliseconds,6} ms  working sets: active {(a == null ? "none" : $"{a:X16}")}, {string.Join(", ", list.Select(w => $"{w.Address} {w.State}"))}");
        };
        link.Connected += _ => trace.Enqueue($"{clock.ElapsedMilliseconds,6} ms  link up");
        link.Disconnected += (why, _) => trace.Enqueue($"{clock.ElapsedMilliseconds,6} ms  link down: {why}");
        vt.Message += json => { if (json.StartsWith("{\"type\":\"screen\"")) screens.Enqueue(json); };
        stack.Attach(link);
        // After Attach, so the stack has queued a link change before the checks act on it.
        link.Connected += g => { info = g; Interlocked.Increment(ref ups); };
        link.Disconnected += (why, _) => { lastReason = why; Interlocked.Increment(ref downs); };
        int busyTries = 0; string busyText = "";
        link.ConnectFailed += (why, text) => trace.Enqueue($"{clock.ElapsedMilliseconds,6} ms  connect failed: {why} {text}");
        link.ConnectFailed += (why, text) => { if (why == LinkCloseReason.Busy) { busyText = text; Interlocked.Increment(ref busyTries); } };
        stack.Start();
        try
        {
            link.Start();
            Check("link up", await Until(() => ups == 1 && link.IsConnected, 2000));
            Check("the bus is asked for address claims once the link is up", await Until(() => gateway.FromClient.Any(ClaimRequest), 2000));
            Check($"the gateway's HELLO: \"{info?.Name}\", {info?.Channels.FirstOrDefault()}", info?.Name == "fake"
                && info.Channels.Count == 1 && info.Channels[0].Name == "fake0" && info.Channels[0].Bitrate == 250000);
            Check($"the client's name reached the gateway: \"{gateway.ClientName}\"", gateway.ClientName == "isoshim self-test");
            Check("STATUS received", await Until(() => statuses > 0, 1000));
            Check("round trips measured", await Until(() => rtts.Count >= 3, 2000));

            byte[] pool = Pool("VT3TestPool.iop");
            trace.Enqueue($"{clock.ElapsedMilliseconds,6} ms  the implement starts");
            await vt.InvokeAsync(() => { unsafe { fixed (byte* p = pool) return IsoShim.isoshim_test_implement(0, p, pool.Length); } });
            bool ready = await Until(() => sets?.Any(s => s.State == PoolState.Ready) == true, 20000);
            Check($"the implement's pool came over the link ({gateway.FromClient.Count} frames to the bus)", ready);
            if (ready)
            {
                await vt.ActivateAsync(sets!.First(s => s.State == PoolState.Ready).Name);
                Check("its screen is shown", await Until(() => screens.Any(x => x.Contains("\"mask\":1000,"))));
                Check("soft key 5000 pressed: sent", await vt.SoftKeyAsync(5000, KeyState.Pressed));
                Check("the press crossed the link", await Until(() => gateway.FromClient.Any(f => Key(f, 1)), 2000));

                // The gateway goes silent: the link drops after about 1 s and comes back.
                Check("the implement's maintenance crossed the link", await Maintained());
                var t = Stopwatch.StartNew();
                trace.Enqueue($"{clock.ElapsedMilliseconds,6} ms  the gateway goes silent");
                gateway.Silent = true;
                Check("silent gateway: the link drops", await Until(() => downs == 1, 3000));
                double took = t.Elapsed.TotalMilliseconds;
                Check($"after {took:F0} ms (1 s of silence), reason timeout", took is > 900 and < 1600 && lastReason == LinkCloseReason.Timeout);
                Check("the release while the link is down is lost", await vt.SoftKeyAsync(5000, KeyState.Released) && link.FramesDropped > 0);
                int requests = gateway.FromClient.Count(ClaimRequest), cutAt = gateway.FromClient.Count;
                Check("the link is back", await Until(() => ups == 2, 2000));
                Check("and the bus is asked for address claims again", await Until(() => gateway.FromClient.Count(ClaimRequest) > requests, 2000));
                // Its own claim before the request, not as an answer to some other request.
                var sinceUp = gateway.FromClient.ToArray().Skip(cutAt).ToList();
                int claimAt = sinceUp.FindIndex(OwnClaim), requestAt = sinceUp.FindIndex(ClaimRequest);
                Check("and the terminal claims its address again first", claimAt >= 0 && claimAt < requestAt);
                Check("the held key is aborted on the new link", await Until(() => gateway.FromClient.Any(f => Key(f, 3)), 2000));

                // Busy twice, then let in: the client keeps trying.
                Check("the implement's maintenance crossed the new link", await Maintained());
                gateway.RefuseBusy = 2;
                trace.Enqueue($"{clock.ElapsedMilliseconds,6} ms  dropped by the gateway, busy twice");
                gateway.Drop();
                Check("dropped, refused busy twice, then back", await Until(() => ups == 3, 4000) && gateway.Refused == 2);

                bool kept = active != null && sets?.Any(s => s.State == PoolState.Ready) == true;
                Check("the terminal still has the implement", kept);
                // Busy for good (another terminal has it): after 1.5 s of refusals, one try every 5 s.
                gateway.RefuseBusy = 1000;
                int refusedBefore = gateway.Refused;
                gateway.Drop();
                await Task.Delay(4000);
                int fastAndSlow = gateway.Refused - refusedBefore;
                Check($"busy for good: {fastAndSlow} refusals in 4 s, quick for 1.5 s then every 5 s", fastAndSlow >= 3 && fastAndSlow <= 9);
                Check("each refusal told by ConnectFailed, with the gateway's text", busyTries == gateway.Refused && busyText == "busy");
                gateway.RefuseBusy = 0;
                Check("free again: back within 5 s", await Until(() => ups == 4, 6000));
                Check("and has its implement again", await Until(() => active != null && sets?.Any(s => s.State == PoolState.Ready) == true, 20000));
            }
        }
        finally
        {
            link.Stop();
            stack.Stop();
        }
        Check("stopped: link down, terminal stopped", !link.IsConnected && !vt.IsRunning);
        if (failures > 0) foreach (var line in trace) log("         " + line);
        log(failures == 0 ? "PASS: the gateway link" : $"FAIL: the gateway link, {failures} checks failed");
        return failures == 0;
    }
}
