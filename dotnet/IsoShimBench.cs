// Bench: the terminal on a real bus, its frames over TCP from bench/can_tcp_relay.py on the CM4,
// through the class a host uses (VirtualTerminal: its own thread, woken by each frame that
// arrives). Waits for the example implement (NAME function 16), shows it, presses its alarm soft
// key (5000), and expects the alarm mask (2000) back. Measures the link with a ping every 50 ms,
// and counts upload attempts (ETP request-to-send aimed at us) and aborts as they cross the link.
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
using AgIsoStack;

public static class IsoShimBench
{
    static Socket? sock;
    static VirtualTerminal? vt;
    static readonly object sendLock = new();
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
        Span<byte> rec = stackalloc byte[18];
        rec.Clear();
        rec[0] = 1;
        uint wire = (id & 0x1FFFFFFF) | (extended ? 0x80000000u : 0);
        BitConverter.TryWriteBytes(rec.Slice(5, 4), wire);
        rec[9] = (byte)data.Length;
        data.CopyTo(rec.Slice(10));
        Interlocked.Increment(ref framesOut);
        WatchEtp(id, data, outgoing: true);
        lock (sendLock) { try { sock?.Send(rec); } catch (SocketException) { } }
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
    static void WatchEtp(uint id, ReadOnlySpan<byte> d, bool outgoing)
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
                Note($"upload attempt from 0x{src:X2}: {size} bytes");
            }
            else if (d[0] == 255)
            {
                aborts++;
                Note($"ETP abort {(outgoing ? "sent" : "received")}, reason {(d.Length > 1 ? d[1] : 0)}");
            }
        }
    }

    static void Reader()
    {
        var buf = new byte[65536];
        int have = 0;
        double last = -1;
        try
        {
            while (true)
            {
                int n = sock!.Receive(buf, have, buf.Length - have, SocketFlags.None);
                if (n == 0) { Note("relay closed the connection"); return; }
                double now = clock.Elapsed.TotalMilliseconds;
                if (last >= 0 && now - last > maxGapMs) { maxGapMs = now - last; maxGapAtMs = now; }
                last = now;
                have += n;
                int off = 0;
                while (true)
                {
                    if (have - off < 1) break;
                    byte kind = buf[off];
                    if (kind == 1)
                    {
                        if (have - off < 18) break;
                        uint wire = BitConverter.ToUInt32(buf, off + 5);
                        int len = Math.Min((int)buf[off + 9], 8);
                        uint id = wire & 0x1FFFFFFF;
                        vt!.Receive(id, new ReadOnlySpan<byte>(buf, off + 10, len), (wire & 0x80000000) != 0);
                        WatchEtp(id, new ReadOnlySpan<byte>(buf, off + 10, len), outgoing: false);
                        Interlocked.Increment(ref framesIn);
                        off += 18;
                    }
                    else if (kind == 3)
                    {
                        if (have - off < 13) break;
                        long sent = BitConverter.ToInt64(buf, off + 5);
                        double rtt = (clock.ElapsedTicks - sent) * 1000.0 / Stopwatch.Frequency;
                        lock (rtts) rtts.Add(rtt);
                        off += 13;
                    }
                    else { Note($"bad record {kind} from relay"); return; }
                }
                Buffer.BlockCopy(buf, off, buf, 0, have - off);
                have -= off;
            }
        }
        catch (Exception e) when (e is SocketException || e is ObjectDisposedException) { Note($"link: {e.Message}"); }
    }

    static void Pinger(CancellationToken stop)
    {
        var rec = new byte[13];
        rec[0] = 2;
        uint seq = 0;
        while (!stop.IsCancellationRequested)
        {
            BitConverter.TryWriteBytes(rec.AsSpan(1, 4), seq++);
            BitConverter.TryWriteBytes(rec.AsSpan(5, 8), clock.ElapsedTicks);
            lock (sendLock) { try { sock?.Send(rec); } catch (SocketException) { return; } }
            Thread.Sleep(50);
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
        sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { sock.Connect(host, port); }
        catch (SocketException e) { log($"FAIL connect: {e.Message}"); return false; }
        Note("connected to relay");
        vt = new VirtualTerminal(new VirtualTerminalOptions { Send = OnSend, PacketsPerCts = 255, Log = OnLog, LogLevel = 2 });
        vt.Message += OnMessage;
        vt.WorkingSetsChanged += OnWorkingSets;
        vt.AddressClaimed += a => { serverAddress = a; Note($"terminal claimed 0x{a:X2}"); };
        vt.PoolLoaded += (a, n) => Note($"implement 0x{a:X2}: pool parsed, {n} objects");
        vt.PoolRejected += (a, o) => { poolBad = o; Note($"implement 0x{a:X2}: pool rejected at object {o}"); };
        vt.ImplementLost += (a, why) => Note($"implement 0x{a:X2} gone ({(why == 0 ? "maintenance timeout" : "re-initiated")})");
        vt.HandlerFailed += e => Note($"handler threw: {e.Message}");
        try { vt.Start(); }
        catch (InvalidOperationException e) { log($"FAIL start: {e.Message}"); return false; }
        // The link's reader hands each frame straight to the terminal (any thread may).
        var stop = new CancellationTokenSource();
        var reader = new Thread(Reader) { IsBackground = true, Name = "relay-rx" };
        reader.Start();
        new Thread(() => Pinger(stop.Token)) { IsBackground = true, Name = "ping" }.Start();

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
        // A second more of the bus, then the link goes first and the reader is joined, so nothing reaches the stack while it stops.
        Thread.Sleep(1000);
        stop.Cancel();
        lock (sendLock) { sock.Close(); sock = null; }
        reader.Join(2000);

        lock (events) foreach (var e in events) log(e);
        List<double> s;
        lock (rtts) s = rtts.OrderBy(x => x).ToList();
        log($"frames: from the bus {framesIn}, to the bus {framesOut}");
        if (firstRtsMs >= 0 && tPool >= 0) log($"upload: first request-to-send to pool parsed {tPool - firstRtsMs:F0} ms");
        lock (rtsBySource) log($"upload attempts: {string.Join(", ", rtsBySource.Select(kv => $"0x{kv.Key:X2} x{kv.Value}"))}; ETP aborts {aborts}");
        log($"ping rtt ms ({s.Count}): p50 {Pct(s, .5):F1}  p90 {Pct(s, .9):F1}  p99 {Pct(s, .99):F1}  max {(s.Count > 0 ? s[^1] : double.NaN):F1}");
        log($"screens drawn: {screensDrawn}");
        log($"longest gap between arrivals: {maxGapMs:F0} ms, at {maxGapAtMs:F0} ms");
        lock (warnings) { log($"stack warnings and errors: {warningCount}"); foreach (var w in warnings) log("  " + w); }
        log(pass ? $"PASS in {tDone:F0} ms" : "FAIL");
        vt.Stop();
        vt = null;
        log("stopped");
        return pass;
    }
}
