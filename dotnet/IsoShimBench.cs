// Bench: the terminal on a real bus, its frames over TCP from bench/can_tcp_relay.py on the CM4.
// Waits for the example implement (NAME function 16), presses its alarm soft key (5000), and
// expects the alarm mask (2000) back. Measures the link with a ping every 50 ms, and counts upload attempts
// (ETP request-to-send aimed at us) and aborts as they cross the link.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

public static unsafe class IsoShimBench
{
#if __IOS__
    const string Lib = "__Internal";
#else
    const string Lib = "isoshim";
#endif
    [DllImport(Lib)] static extern int isoshim_start_terminal(delegate* unmanaged<int, uint, byte*, int, int, void> send,
                                                               delegate* unmanaged<int, int, int, void> ev, ushort wanted);
    [DllImport(Lib)] static extern void isoshim_set_log(delegate* unmanaged<int, byte*, void> fn, int level);
    [DllImport(Lib)] static extern void isoshim_set_packets_per_cts(byte packets);
    [DllImport(Lib)] static extern void isoshim_receive(int channel, uint id, byte* data, int len, int extended);
    [DllImport(Lib)] static extern void isoshim_step();
    [DllImport(Lib)] static extern int isoshim_press_soft_key(ushort objectId);
    [DllImport(Lib)] static extern void isoshim_stop();

    static Socket? sock;
    static readonly object sendLock = new();
    static readonly Stopwatch clock = new();
    static long framesOut, framesIn;
    static volatile int serverAddress = -1, poolObjects = -1, poolAddress = -1, poolBad = -1, mask = -1;
    static readonly List<string> events = new();
    static readonly List<string> warnings = new();
    static int warningCount;
    static readonly Dictionary<int, int> rtsBySource = new();
    static int aborts;
    static double firstRtsMs = -1;
    static readonly List<double> rtts = new();
    static double maxGapMs, maxGapAtMs;

    static void Note(string s) { lock (events) events.Add($"{clock.Elapsed.TotalMilliseconds,7:F0} ms  {s}"); }

    [UnmanagedCallersOnly]
    static void OnSend(int channel, uint id, byte* data, int len, int extended)
    {
        Span<byte> rec = stackalloc byte[18];
        rec.Clear();
        rec[0] = 1;
        uint wire = (id & 0x1FFFFFFF) | (extended != 0 ? 0x80000000u : 0);
        BitConverter.TryWriteBytes(rec.Slice(5, 4), wire);
        rec[9] = (byte)len;
        new ReadOnlySpan<byte>(data, len).CopyTo(rec.Slice(10));
        Interlocked.Increment(ref framesOut);
        WatchEtp(id, new ReadOnlySpan<byte>(data, len), outgoing: true);
        lock (sendLock) { try { sock?.Send(rec); } catch (SocketException) { } }
    }

    [UnmanagedCallersOnly]
    static void OnEvent(int kind, int a, int b)
    {
        switch (kind)
        {
            case 1: serverAddress = a; Note($"terminal claimed 0x{a:X2}"); break;
            case 3: poolObjects = a; poolAddress = b; Note($"implement 0x{b:X2}: pool parsed, {a} objects"); break;
            case 4: poolBad = a; Note($"pool rejected at object {a}"); break;
            case 5: mask = a; Note($"active mask {a}"); break;
            case 6: Note($"other implement 0x{b:X2}: pool parsed, {a} objects (not under test)"); break;
            case 7: Note($"pool from 0x{b:X2} arrived, parsing"); break;
            case 8: Note($"implement 0x{b:X2} gone ({(a == 0 ? "maintenance timeout" : "re-initiated")})"); break;
        }
    }

    [UnmanagedCallersOnly]
    static void OnLog(int level, byte* text)
    {
        if (level < 2) return;
        string s = Marshal.PtrToStringUTF8((IntPtr)text) ?? "";
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
                        fixed (byte* p = &buf[off + 10])
                            isoshim_receive(0, id, p, len, (wire & 0x80000000) != 0 ? 1 : 0);
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
        sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { sock.Connect(host, port); }
        catch (SocketException e) { log($"FAIL connect: {e.Message}"); return false; }
        Note("connected to relay");
        var stop = new CancellationTokenSource();
        var reader = new Thread(Reader) { IsBackground = true, Name = "relay-rx" };
        reader.Start();
        new Thread(() => Pinger(stop.Token)) { IsBackground = true, Name = "ping" }.Start();

        isoshim_set_log(&OnLog, 2);
        isoshim_set_packets_per_cts(255);
        int rc = isoshim_start_terminal(&OnSend, &OnEvent, 16); // the example implement: steering control
        if (rc != 0) { log($"FAIL start {rc}"); return false; }

        bool pressed = false, pass = false;
        double tPool = -1, tDone = -1;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            isoshim_step();
            if (poolBad >= 0) break;
            if (tPool < 0 && poolObjects >= 0) tPool = clock.Elapsed.TotalMilliseconds;
            if (tPool >= 0 && !pressed && mask == 1000) { pressed = isoshim_press_soft_key(5000) == 1; Note($"pressed soft key 5000: {(pressed ? "sent" : "not sent")}"); }
            if (pressed && mask == 2000) { tDone = clock.Elapsed.TotalMilliseconds; pass = true; break; }
            Thread.Sleep(2);
        }
        // A second more of the bus (still stepping), then the link goes first and the reader is joined, so nothing reaches the stack while it stops.
        for (var end = clock.Elapsed.TotalMilliseconds + 1000; clock.Elapsed.TotalMilliseconds < end;) { isoshim_step(); Thread.Sleep(2); }
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
        log($"longest gap between arrivals: {maxGapMs:F0} ms, at {maxGapAtMs:F0} ms");
        lock (warnings) { log($"stack warnings and errors: {warningCount}"); foreach (var w in warnings) log("  " + w); }
        log(pass ? $"PASS in {tDone:F0} ms" : "FAIL");
        isoshim_stop();
        log("stopped");
        return pass;
    }
}
