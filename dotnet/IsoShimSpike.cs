// Spike: drives the native isoshim (AgIsoStack++) from C#. C# is the CAN bus: every frame the
// stack sends arrives in OnSend, on the thread that calls isoshim_step, and is handed to the other
// channel.
// Pass: the implement uploads its pool to the terminal, C# presses the alarm soft key through the
// terminal, and the implement answers by switching to the alarm mask.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

public static unsafe class IsoShimSpike
{
#if __IOS__
    const string Lib = "__Internal";
#else
    const string Lib = "isoshim";
#endif
    [DllImport(Lib)] static extern int isoshim_version();
    [DllImport(Lib)] static extern int isoshim_start(delegate* unmanaged<int, uint, byte*, int, int, void> send,
                                                      delegate* unmanaged<int, int, int, void> ev, byte* pool, int poolLen);
    [DllImport(Lib)] static extern void isoshim_receive(int channel, uint id, byte* data, int len, int extended);
    [DllImport(Lib)] static extern void isoshim_step();
    [DllImport(Lib)] static extern int isoshim_press_soft_key(ushort objectId);
    [DllImport(Lib)] static extern void isoshim_stop();
    [DllImport(Lib)] static extern void isoshim_set_packets_per_cts(byte packets);

    static long framesFrom0, framesFrom1, bytesFrom1;
    static int stepThread = -1;
    static bool callbackOffThread;
    static volatile int serverAddress = -1, clientAddress = -1, poolObjects = -1, poolBad = -1, mask = -1, parsing = 0;

    [UnmanagedCallersOnly]
    static void OnSend(int channel, uint id, byte* data, int len, int extended)
    {
        if (channel == 0) Interlocked.Increment(ref framesFrom0);
        else { Interlocked.Increment(ref framesFrom1); Interlocked.Add(ref bytesFrom1, len); }
        if (Environment.CurrentManagedThreadId != stepThread) callbackOffThread = true;
        isoshim_receive(1 - channel, id, data, len, extended);
    }

    [UnmanagedCallersOnly]
    static void OnEvent(int kind, int a, int b)
    {
        if (Environment.CurrentManagedThreadId != stepThread) callbackOffThread = true;
        switch (kind)
        {
            case 1: serverAddress = a; break;
            case 2: clientAddress = a; break;
            case 3: poolObjects = a; break;
            case 4: poolBad = a; break;
            case 5: mask = a; break;
            case 7: parsing++; break;
        }
    }

    static byte[] LoadPool()
    {
        using Stream s = typeof(IsoShimSpike).Assembly.GetManifestResourceStream("VT3TestPool.iop")
            ?? throw new InvalidOperationException("VT3TestPool.iop not embedded");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>Runs the spike four times in one process, stopping the stack in between: with the
    /// stack's 16 frames per clear-to-send, then with 255; then stopped the moment the pool starts
    /// parsing (its thread still running), and once more in full after that. Every line of the
    /// report goes to log.</summary>
    public static bool RunAll(Action<string> log)
    {
        bool ok = Run(log, 16);
        ok &= Run(log, 255);
        ok &= Run(log, 255, stopWhenParsing: true);
        ok &= Run(log, 255);
        log(ok ? "ALL PASS" : "SOME FAILED");
        return ok;
    }

    static bool Run(Action<string> log, byte packetsPerCts, bool stopWhenParsing = false)
    {
        framesFrom0 = framesFrom1 = bytesFrom1 = 0;
        stepThread = Environment.CurrentManagedThreadId;
        callbackOffThread = false;
        serverAddress = clientAddress = poolObjects = poolBad = mask = -1;
        parsing = 0;
        log($"--- {packetsPerCts} frames per clear-to-send{(stopWhenParsing ? ", stopped while the pool is parsing" : "")}");
        isoshim_set_packets_per_cts(packetsPerCts);
        log($"isoshim {isoshim_version()} on {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, " +
            $".NET {Environment.Version}, dynamic code {(RuntimeFeature.IsDynamicCodeSupported ? "yes" : "no (AOT)")}");
        byte[] pool = LoadPool();
        var clock = Stopwatch.StartNew();
        int rc;
        fixed (byte* p = pool)
            rc = isoshim_start(&OnSend, &OnEvent, p, pool.Length);
        if (rc != 0) { log($"FAIL isoshim_start returned {rc}"); return false; }

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
            if (tPool < 0 && poolObjects >= 0) { tPool = now; log($"{now,7:F0} ms  pool parsed: {poolObjects} objects from {pool.Length} bytes"); }
            if (tMask < 0 && mask == 1000) { tMask = now; log($"{now,7:F0} ms  terminal shows data mask 1000"); }
            if (tMask >= 0 && !pressed) { pressed = isoshim_press_soft_key(5000) == 1; log($"{now,7:F0} ms  pressed soft key 5000: {(pressed ? "sent" : "not sent")}"); }
            if (pressed && mask == 2000) { tAlarm = now; log($"{now,7:F0} ms  implement answered: alarm mask 2000 shown"); break; }
            Thread.Sleep(2);
        }
        isoshim_stop();
        log($"frames: terminal sent {framesFrom0}, implement sent {framesFrom1} ({bytesFrom1} bytes)");
        log($"every callback on the stepping thread: {(callbackOffThread ? "no" : "yes")}");
        bool pass = tAlarm >= 0 && !callbackOffThread;
        log(pass ? $"PASS in {tAlarm:F0} ms" : tAlarm >= 0 ? "FAIL: a callback came on another thread" : "FAIL: no alarm mask within 60 s");
        return pass;
    }
}
