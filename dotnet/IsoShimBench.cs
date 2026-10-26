// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors
//
// Bench: the terminal on a real bus through an AOW link gateway (bench/aowlink_gateway.py on the CM4,
// docs/TRANSPORT.md section 5), with the classes a host uses: AowLink attached to VirtualTerminalStack.
// Waits for the example implement (NAME function 16), shows it, presses its alarm soft key (5000),
// and expects the alarm mask (2000) back. Measures the link's round trip from its heartbeats, and
// counts upload attempts (ETP request-to-send aimed at us), aborts and link drops.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpIsoTerm;

public static class IsoShimBench
{
    static VirtualTerminalStack? stack;
    static VirtualTerminal? vt;
    static AowLink? link;
    static int links, drops;
    static readonly Stopwatch clock = new();
    static long framesOut, framesIn;
    static volatile int serverAddress = -1, poolBad = -1, mask = -1;
    static ulong wanted; // the example implement's NAME, once its pool is ready
    static readonly Regex MaskField = new("\"mask\":(\\d+)");
    static int screensDrawn;
    static readonly List<string> events = new();
    static readonly List<string> warnings = new();
    static int warningCount;
    static readonly Dictionary<int, int> rtsBySource = new();
    static int aborts;
    static double firstRtsMs = -1;
    static readonly List<double> rtts = new();
    static double maxGapMs, maxGapAtMs;

    static void Note(string s) { lock (events) events.Add($"{clock.Elapsed.TotalMilliseconds,7:F0} ms  {s}"); }

    static void OnSend(int channel, uint id, ReadOnlySpan<byte> data, bool extended)
    {
        Interlocked.Increment(ref framesOut);
        WatchEtp(id, data, outgoing: true, 0);
        link!.Send(channel, id, data, extended);
    }

    static double lastArrival = -1;

    static void OnFrame(int channel, uint id, ReadOnlySpan<byte> data, bool extended, uint timeUs)
    {
        double now = clock.Elapsed.TotalMilliseconds;
        if (lastArrival >= 0 && now - lastArrival > maxGapMs) { maxGapMs = now - lastArrival; maxGapAtMs = now; }
        lastArrival = now;
        WatchEtp(id, data, outgoing: false, timeUs);
        Interlocked.Increment(ref framesIn);
    }

    // The screens carry the active mask; the working set list finds the example implement, the
    // one whose NAME has function code 16.
    static void OnMessage(string json)
    {
        if (json.StartsWith("{\"type\":\"screen\""))
        {
            screensDrawn++;
            var m = MaskField.Match(json);
            if (m.Success && int.Parse(m.Groups[1].Value) is int now && now != mask) { mask = now; Note($"active mask {now}"); }
        }
    }

    static void OnWorkingSets(ulong? active, IReadOnlyList<WorkingSetInfo> list)
    {
        if (wanted != 0) return;
        foreach (var ws in list)
            if (((ws.Name >> 40) & 0xFF) == 16 && ws.State == PoolState.Ready) wanted = ws.Name;
    }

    static void OnLog(int level, string s)
    {
        lock (warnings) { warningCount++; if (warnings.Count < 12) warnings.Add($"{clock.Elapsed.TotalMilliseconds,7:F0} ms  {s}"); }
    }

    // ETP connection management (PGN 0xC800): control byte 20 = request to send, 255 = abort.
    // What comes from the bus carries the gateway's receive time (kernel, us, wrapping at 2^32),
    // to match against a capture on the gateway (bench/out/frames-NAME.log).
    static void WatchEtp(uint id, ReadOnlySpan<byte> d, bool outgoing, uint busUs)
    {
        if (((id >> 8) & 0x3FF00) != 0xC800 || d.Length < 1) return;
        int src = (int)(id & 0xFF), dst = (int)((id >> 8) & 0xFF);
        lock (rtsBySource)
        {
            if (d[0] == 20 && !outgoing && dst == serverAddress)
            {
                rtsBySource[src] = rtsBySource.TryGetValue(src, out int n) ? n + 1 : 1;
                if (firstRtsMs < 0) firstRtsMs = clock.Elapsed.TotalMilliseconds;
                uint size = BitConverter.ToUInt32(d.Slice(1, 4));
                Note($"upload attempt from 0x{src:X2}: {size} bytes, bus time {busUs / 1e6:F6}");
            }
            else if (d[0] == 255)
            {
                aborts++;
                Note($"ETP abort {(outgoing ? "sent" : $"received (bus time {busUs / 1e6:F6})")}, reason {(d.Length > 1 ? d[1] : 0)}");
            }
        }
    }

    static double Pct(List<double> s, double p) => s.Count == 0 ? double.NaN : s[Math.Min(s.Count - 1, (int)(p * s.Count))];

    /// <summary>One run against the relay at host:port. True when the soft key round trip works.</summary>
    public static bool Run(string host, int port, Action<string> log, int timeoutS = 120)
    {
        log($"bench to {host}:{port} on {RuntimeInformation.OSDescription}, .NET {Environment.Version}, " +
            $"dynamic code {(RuntimeFeature.IsDynamicCodeSupported ? "yes" : "no (AOT)")}");
        clock.Restart();
        wanted = 0;
        screensDrawn = 0;
        serverAddress = poolBad = mask = -1;
        links = drops = 0;
        lastArrival = -1;
        link = new AowLink(host, port, $"isoshim bench on {RuntimeInformation.OSDescription}");
        link.Connected += g =>
        {
            links++;
            Note($"link up: {g.Name}, {string.Join(", ", g.Channels.Select(c => $"{c.Name} {c.Bitrate / 1000} kbit/s"))}");
        };
        link.Disconnected += (why, text) => { drops++; Note($"link down: {why} {text}"); };
        link.StatusReceived += st =>
        {
            if (st.State != BusState.ErrorActive || st.RxDropped > 0 || st.TxDropped > 0)
                Note($"bus {st.Channel}: {st.State}, errors tx {st.TxErrors} rx {st.RxErrors}, dropped rx {st.RxDropped} tx {st.TxDropped}");
        };
        link.RoundTrip += ms => { lock (rtts) rtts.Add(ms); };
        link.FrameReceived += OnFrame;
        link.HandlerFailed += e => Note($"link handler threw: {e.Message}");
        stack = new VirtualTerminalStack(new VirtualTerminalOptions { Send = OnSend, PacketsPerCts = 255, Log = OnLog, LogLevel = 2 });
        vt = stack.Terminals[0];
        vt.Message += OnMessage;
        vt.WorkingSetsChanged += OnWorkingSets;
        vt.AddressClaimed += a => { serverAddress = a; Note($"terminal claimed 0x{a:X2}"); };
        vt.PoolLoaded += (a, n) => Note($"implement 0x{a:X2}: pool parsed, {n} objects");
        vt.PoolRejected += (a, o) => { poolBad = o; Note($"implement 0x{a:X2}: pool rejected at object {o}"); };
        vt.ImplementLost += (a, why) => Note($"implement 0x{a:X2} gone ({(why == 0 ? "maintenance timeout" : "re-initiated")})");
        stack.HandlerFailed += e => Note($"handler threw: {e.Message}");
        stack.Attach(link);
        try { stack.Start(); }
        catch (InvalidOperationException e) { log($"FAIL start: {e.Message}"); return false; }
        link.Start();
        for (int i = 0; i < 100 && links == 0; i++) Thread.Sleep(50);
        if (links == 0)
        {
            log("FAIL: no link to the gateway in 5 s");
            link.Stop();
            stack.Stop();
            return false;
        }

        bool shown = false, pressed = false, pass = false;
        double tPool = -1, tDone = -1;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            if (poolBad >= 0) break;
            if (!shown && wanted != 0)
            {
                tPool = clock.Elapsed.TotalMilliseconds;
                shown = vt.ActivateAsync(wanted).GetAwaiter().GetResult();
                Note($"example implement {wanted:X16} ready; shown: {(shown ? "yes" : "no")}");
            }
            if (shown && !pressed && mask == 1000)
            {
                pressed = vt.SoftKeyAsync(5000, KeyState.Pressed).GetAwaiter().GetResult()
                       && vt.SoftKeyAsync(5000, KeyState.Released).GetAwaiter().GetResult();
                Note($"pressed soft key 5000: {(pressed ? "sent" : "not sent")}");
            }
            if (pressed && mask == 2000) { tDone = clock.Elapsed.TotalMilliseconds; pass = true; break; }
            Thread.Sleep(2);
        }
        // A second more of the bus, then the link goes first, so nothing reaches the stack while it stops.
        Thread.Sleep(1000);
        link.Stop();

        lock (events) foreach (var e in events) log(e);
        List<double> s;
        lock (rtts) s = rtts.OrderBy(x => x).ToList();
        log($"frames: from the bus {framesIn}, to the bus {framesOut}; link up {links} times, down {drops}, frames dropped while down {link.FramesDropped}");
        if (firstRtsMs >= 0 && tPool >= 0) log($"upload: first request-to-send to pool parsed {tPool - firstRtsMs:F0} ms");
        lock (rtsBySource) log($"upload attempts: {string.Join(", ", rtsBySource.Select(kv => $"0x{kv.Key:X2} x{kv.Value}"))}; ETP aborts {aborts}");
        log($"ping rtt ms ({s.Count}): p50 {Pct(s, .5):F1}  p90 {Pct(s, .9):F1}  p99 {Pct(s, .99):F1}  max {(s.Count > 0 ? s[^1] : double.NaN):F1}");
        log($"screens drawn: {screensDrawn}");
        log($"longest gap between arrivals: {maxGapMs:F0} ms, at {maxGapAtMs:F0} ms");
        lock (warnings) { log($"stack warnings and errors: {warningCount}"); foreach (var w in warnings) log("  " + w); }
        log(pass ? $"PASS in {tDone:F0} ms" : "FAIL");
        stack.Stop();
        stack = null;
        vt = null;
        log("stopped");
        return pass;
    }
}
