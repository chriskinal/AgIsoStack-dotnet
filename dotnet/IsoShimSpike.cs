// Self-test: drives the native isoshim (AgIsoStack++) from C#. C# is the CAN bus: every frame the
// stack sends arrives in OnSend, on the thread that calls isoshim_step, and is handed to the other
// channel (the terminal on 0, upstream's VT client on 1).
// Pass: the implement uploads its pool to the terminal, the terminal draws its data mask, C# presses
// the alarm soft key, the implement switches to the alarm mask and the terminal draws that.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static IsoShim;

public static unsafe class IsoShimSpike
{
    static long framesFrom0, framesFrom1, bytesFrom1;
    static int stepThread = -1;
    static bool callbackOffThread;
    static volatile int serverAddress = -1, clientAddress = -1, poolObjects = -1, poolBad = -1, mask = -1, parsing = 0;

    // What the terminal reported: the last working set list, every distinct screen, the pictures.
    static string? workingSets;
    static readonly List<string> screens = new();
    static readonly Dictionary<uint, (int W, int H, byte[] Rgba)> images = new();
    // Stored pool versions, by NAME and label, as a host would keep them on disk.
    static readonly Dictionary<(ulong Name, string Label), List<byte>> store = new();
    static int saves, loads;

    /// <summary>When set (the desktop self-test), the screens are written here with a page that paints them.</summary>
    public static string? ScreenDir;

    static void OffThread() { if (Environment.CurrentManagedThreadId != stepThread) callbackOffThread = true; }

    [UnmanagedCallersOnly]
    static void OnSend(int channel, uint id, byte* data, int len, int extended)
    {
        if (channel == 0) Interlocked.Increment(ref framesFrom0);
        else { Interlocked.Increment(ref framesFrom1); Interlocked.Add(ref bytesFrom1, len); }
        OffThread();
        isoshim_receive(1 - channel, id, data, len, extended);
    }

    [UnmanagedCallersOnly]
    static void OnEvent(int kind, int a, int b)
    {
        OffThread();
        switch (kind)
        {
            case ServerAddress: serverAddress = a; break;
            case ClientAddress: clientAddress = a; break;
            case PoolOk: poolObjects = a; break;
            case PoolBad: poolBad = a; break;
            case Mask: mask = a; break;
            case ParseStart: parsing++; break;
        }
    }

    [UnmanagedCallersOnly]
    static void OnMessage(byte* json, int len)
    {
        OffThread();
        string s = Encoding.UTF8.GetString(json, len);
        if (s.StartsWith("{\"type\":\"workingSets\"")) workingSets = s;
        else if (s.StartsWith("{\"type\":\"screen\"") && !screens.Contains(s)) screens.Add(s);
    }

    [UnmanagedCallersOnly]
    static void OnImage(uint id, int w, int h, byte* rgba)
    {
        OffThread();
        images[id] = (w, h, new ReadOnlySpan<byte>(rgba, w * h * 4).ToArray());
    }

    static string Label(byte* label, int len) => Convert.ToHexString(new ReadOnlySpan<byte>(label, len));

    [UnmanagedCallersOnly]
    static int OnSave(ulong name, byte* label, int labelLen, byte* data, int len, int append)
    {
        OffThread();
        var key = (name, Label(label, labelLen));
        if (append == 0 || !store.ContainsKey(key)) store[key] = new List<byte>();
        store[key].AddRange(new ReadOnlySpan<byte>(data, len).ToArray());
        saves++;
        return 1;
    }

    [UnmanagedCallersOnly]
    static int OnLoad(ulong name, byte* label, int labelLen)
    {
        OffThread();
        if (!store.TryGetValue((name, Label(label, labelLen)), out var pool)) return 0;
        byte[] bytes = pool.ToArray();
        fixed (byte* p = bytes) isoshim_reply(p, bytes.Length);
        loads++;
        return 1;
    }

    [UnmanagedCallersOnly]
    static void OnList(ulong name, int labelLen)
    {
        OffThread();
        foreach (var key in store.Keys.Where(k => k.Name == name && k.Label.Length == labelLen * 2))
        {
            byte[] label = Convert.FromHexString(key.Label);
            fixed (byte* p = label) isoshim_reply(p, label.Length);
        }
    }

    [UnmanagedCallersOnly]
    static int OnRemove(ulong name, byte* label, int labelLen)
    {
        OffThread();
        string? one = label == null ? null : Label(label, labelLen);
        foreach (var key in store.Keys.Where(k => k.Name == name && (one == null ? k.Label.Length == labelLen * 2 : k.Label == one)).ToList())
            store.Remove(key);
        return 1;
    }

    static byte[] LoadPool()
    {
        using Stream s = typeof(IsoShimSpike).Assembly.GetManifestResourceStream("VT3TestPool.iop")
            ?? throw new InvalidOperationException("VT3TestPool.iop not embedded");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>Runs the self-test five times in one process, stopping the stack in between: with
    /// the stack's 16 frames per clear-to-send, then with 255; then stopped the moment the pool
    /// starts parsing (its thread still running); once more in full; and last with the pool the
    /// previous run stored, which the implement loads instead of uploading. Every line of the
    /// report goes to log.</summary>
    public static bool RunAll(Action<string> log)
    {
        bool ok = Run(log, 16);
        ok &= Run(log, 255);
        ok &= Run(log, 255, stopWhenParsing: true);
        ok &= Run(log, 255);
        ok &= Run(log, 255, fromStore: true);
        if (ScreenDir != null) WriteScreens(log);
        log(ok ? "ALL PASS" : "SOME FAILED");
        return ok;
    }

    static bool Run(Action<string> log, byte packetsPerCts, bool stopWhenParsing = false, bool fromStore = false)
    {
        framesFrom0 = framesFrom1 = bytesFrom1 = 0;
        stepThread = Environment.CurrentManagedThreadId;
        callbackOffThread = false;
        serverAddress = clientAddress = poolObjects = poolBad = mask = -1;
        parsing = 0;
        saves = loads = 0;
        workingSets = null;
        if (!fromStore) store.Clear();
        log($"--- {packetsPerCts} frames per clear-to-send{(stopWhenParsing ? ", stopped while the pool is parsing" : "")}{(fromStore ? ", the pool stored by the run before" : "")}");
        log($"isoshim {isoshim_version()} on {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, " +
            $".NET {Environment.Version}, dynamic code {(RuntimeFeature.IsDynamicCodeSupported ? "yes" : "no (AOT)")}");
        byte[] pool = LoadPool();
        var clock = Stopwatch.StartNew();
        var config = new Config { PacketsPerCts = packetsPerCts };
        var host = new Host
        {
            Send = &OnSend, Event = &OnEvent, Message = &OnMessage, Image = &OnImage,
            Save = &OnSave, Load = &OnLoad, List = &OnList, Remove = &OnRemove,
        };
        int rc = isoshim_vt_start(&config, &host);
        if (rc == 0)
            fixed (byte* p = pool)
                rc = isoshim_test_implement(p, pool.Length);
        if (rc != 0) { log($"FAIL start returned {rc}"); isoshim_stop(); return false; }

        double tServer = -1, tClient = -1, tPool = -1, tMask = -1, tAlarm = -1;
        bool pressed = false;
        while (clock.Elapsed.TotalSeconds < 60)
        {
            isoshim_step();
            double now = clock.Elapsed.TotalMilliseconds;
            if (stopWhenParsing && parsing > 0)
            {
                isoshim_stop();
                log($"{now,7:F0} ms  pool arrived, parsing; stopped the stack then: {(poolObjects < 0 ? "before the parse finished" : "after the parse finished")}");
                log("PASS: stopped while parsing");
                return true;
            }
            if (tServer < 0 && serverAddress >= 0) { tServer = now; log($"{now,7:F0} ms  terminal claimed 0x{serverAddress:X2}"); }
            if (tClient < 0 && clientAddress >= 0) { tClient = now; log($"{now,7:F0} ms  implement claimed 0x{clientAddress:X2}"); }
            if (poolBad >= 0) { log($"FAIL pool rejected at object {poolBad}"); break; }
            if (tPool < 0 && poolObjects >= 0) { tPool = now; log($"{now,7:F0} ms  pool parsed: {poolObjects} objects{(loads > 0 ? ", loaded from the store" : $" from {pool.Length} bytes")}"); }
            if (tMask < 0 && mask == 1000) { tMask = now; log($"{now,7:F0} ms  terminal shows data mask 1000"); }
            if (tMask >= 0 && !pressed)
            {
                pressed = isoshim_soft_key(5000, Pressed) == 1 && isoshim_soft_key(5000, Released) == 1;
                log($"{now,7:F0} ms  pressed soft key 5000: {(pressed ? "sent" : "not sent")}");
            }
            if (pressed && mask == 2000) { tAlarm = now; log($"{now,7:F0} ms  implement answered: alarm mask 2000 shown"); break; }
            Thread.Sleep(2);
        }
        // A little more of the bus, so the last screen is drawn and a store finishes.
        for (var end = clock.Elapsed.TotalMilliseconds + 300; clock.Elapsed.TotalMilliseconds < end;) { isoshim_step(); Thread.Sleep(2); }
        isoshim_stop();
        log($"frames: terminal sent {framesFrom0}, implement sent {framesFrom1} ({bytesFrom1} bytes)");
        log($"working sets: {workingSets}");
        bool drewData = screens.Any(s => s.Contains("\"mask\":1000,\"kind\":\"data\""));
        bool drewAlarm = screens.Any(s => s.Contains("\"mask\":2000,\"kind\":\"alarm\""));
        log($"screens drawn: data mask 1000 {(drewData ? "yes" : "no")}, alarm mask 2000 {(drewAlarm ? "yes" : "no")}; {screens.Count} distinct so far, {images.Count} pictures");
        log($"store: {saves} saves, {loads} loads, {store.Count} versions held");
        log($"every callback on the stepping thread: {(callbackOffThread ? "no" : "yes")}");
        bool stored = fromStore ? loads > 0 && bytesFrom1 < 20000 : saves > 0;
        bool pass = tAlarm >= 0 && !callbackOffThread && drewData && drewAlarm && stored;
        log(pass ? $"PASS in {tAlarm:F0} ms" :
            tAlarm < 0 ? "FAIL: no alarm mask within 60 s" :
            callbackOffThread ? "FAIL: a callback came on another thread" :
            !stored ? (fromStore ? "FAIL: the pool was not loaded from the store" : "FAIL: the pool was not stored") :
            "FAIL: a mask was not drawn");
        return pass;
    }

    // screens.js holds every screen and picture; index.html paints them with painter/vtpaint.js.
    static void WriteScreens(Action<string> log)
    {
        Directory.CreateDirectory(ScreenDir!);
        var js = new StringBuilder("window.vtScreens = [\n");
        foreach (var s in screens) js.Append(s).Append(",\n");
        js.Append("];\nwindow.vtImages = {\n");
        foreach (var (id, img) in images) js.Append($"{id}: {{w: {img.W}, h: {img.H}, rgba: \"{Convert.ToBase64String(img.Rgba)}\"}},\n");
        js.Append("};\n");
        File.WriteAllText(Path.Combine(ScreenDir!, "screens.js"), js.ToString());
        log($"screens written to {Path.Combine(ScreenDir!, "index.html")}");
    }
}
