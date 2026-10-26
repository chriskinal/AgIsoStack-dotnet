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
using SharpIsoTerm.Interop;
using static SharpIsoTerm.Interop.IsoShim;

public static unsafe class IsoShimSpike
{
    static long framesFrom0, framesFrom1, bytesFrom1;
    static int stepThread = -1;
    static bool callbackOffThread;
    static volatile int serverAddress = -1, clientAddress = -1, poolObjects = -1, poolBad = -1, mask = -1, parsing = 0;

    // What the terminal reported: the last working set list, every distinct screen, the pictures.
    static string? workingSets;
    static readonly List<string> screens = new();
    // The input run: edit messages, and what the test implement heard.
    static readonly List<string> edits = new(), heard = new();
    // The alarms messages, and the audio and volume ones.
    static readonly List<string> alarms = new(), audio = new();
    static readonly Dictionary<uint, (int W, int H, byte[] Rgba)> images = new();
    // Several terminals: every message under its terminal, each terminal's and each test
    // implement's address, which terminal each picture went to, and NACKs the terminals sent.
    static readonly List<(int Terminal, string Json)> tagged = new();
    static readonly Dictionary<int, int> serverAddresses = new(), clientAddresses = new();
    static readonly List<(int Terminal, uint Id)> imageOwners = new();
    static int nacksFrom0;
    // Stored pool versions, by NAME and label, as a host would keep them on disk.
    static readonly Dictionary<(ulong Name, string Label), List<byte>> store = new();
    static int saves, loads;

    /// <summary>When set (the desktop self-test), the screens are written here with a page that paints them.</summary>
    public static string? ScreenDir;

    static void OffThread() { if (Environment.CurrentManagedThreadId != stepThread) callbackOffThread = true; }

    [UnmanagedCallersOnly]
    static void OnSend(int channel, uint id, byte* data, int len, int extended)
    {
        if (channel == 0)
        {
            Interlocked.Increment(ref framesFrom0);
            if (((id >> 8) & 0x3FF00) == 0xE800 && len > 0 && data[0] == 1) nacksFrom0++; // Acknowledgement, negative
        }
        else { Interlocked.Increment(ref framesFrom1); Interlocked.Add(ref bytesFrom1, len); }
        OffThread();
        isoshim_receive(1 - channel, id, data, len, extended);
    }

    [UnmanagedCallersOnly]
    static void OnEvent(int terminal, int kind, int a, int b)
    {
        OffThread();
        switch (kind)
        {
            case ServerAddress: serverAddress = a; serverAddresses[terminal] = a; break;
            case ClientAddress: clientAddress = a; clientAddresses[b] = a; break;
            case PoolOk: poolObjects = a; break;
            case PoolBad: poolBad = a; break;
            case Mask: mask = a; break;
            case ParseStart: parsing++; break;
        }
    }

    [UnmanagedCallersOnly]
    static void OnMessage(int terminal, byte* json, int len)
    {
        OffThread();
        string s = Encoding.UTF8.GetString(json, len);
        tagged.Add((terminal, s));
        if (s.StartsWith("{\"type\":\"workingSets\"")) workingSets = s;
        else if (s.StartsWith("{\"type\":\"screen\"") && !screens.Contains(s)) screens.Add(s);
        else if (s.StartsWith("{\"type\":\"edit\"")) edits.Add(s);
        else if (s.StartsWith("{\"type\":\"implement\"")) heard.Add(s);
        else if (s.StartsWith("{\"type\":\"alarms\"")) alarms.Add(s);
        else if (s.StartsWith("{\"type\":\"audio\"") || s.StartsWith("{\"type\":\"volume\"")) audio.Add(s);
    }

    [UnmanagedCallersOnly]
    static void OnImage(int terminal, uint id, int w, int h, byte* rgba)
    {
        OffThread();
        images[id] = (w, h, new ReadOnlySpan<byte>(rgba, w * h * 4).ToArray());
        imageOwners.Add((terminal, id));
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

    static byte[] LoadPool(string name = "VT3TestPool.iop")
    {
        using Stream s = typeof(IsoShimSpike).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} not embedded");
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
        ok &= RunInput(log);
        ok &= RunAlarms(log);
        ok &= RunTerminals(log);
        ok &= VirtualTerminalTest.Run(log);
        ok &= AowLinkTest.Run(log);
        ok &= SlcanLinkTest.Run(log);
#if !IOS && !__IOS__
        ok &= AowLinkBrowserTest.Run(log);
#endif
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
        int rc = isoshim_vt_start(&config, 1, &host);
        if (rc == 0)
            fixed (byte* p = pool)
                rc = isoshim_test_implement(0, p, pool.Length);
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
                pressed = isoshim_soft_key(0, 5000, Pressed) == 1 && isoshim_soft_key(0, 5000, Released) == 1;
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

    /// <summary>The operator's input on testpools/inputs.iop (inputs.py): selecting, numbers,
    /// strings, a boolean, a list and escape; each checked by what the implement hears and what
    /// the terminal draws.</summary>
    static bool RunInput(Action<string> log)
    {
        log("--- input fields");
        stepThread = Environment.CurrentManagedThreadId;
        callbackOffThread = false;
        mask = poolBad = poolObjects = -1;
        screens.Clear();
        edits.Clear();
        heard.Clear();
        store.Clear();
        byte[] pool = LoadPool("inputs.iop");
        var config = new Config();
        var host = new Host
        {
            Send = &OnSend, Event = &OnEvent, Message = &OnMessage, Image = &OnImage,
            Save = &OnSave, Load = &OnLoad, List = &OnList, Remove = &OnRemove,
        };
        int rc = isoshim_vt_start(&config, 1, &host);
        if (rc == 0)
            fixed (byte* p = pool)
                rc = isoshim_test_implement(0, p, pool.Length);
        if (rc != 0) { log($"FAIL start returned {rc}"); isoshim_stop(); return false; }
        var clock = Stopwatch.StartNew();
        bool StepUntil(Func<bool> done, double ms = 2000)
        {
            for (var end = clock.Elapsed.TotalMilliseconds + ms; clock.Elapsed.TotalMilliseconds < end;)
            {
                if (done()) return true;
                isoshim_step();
                Thread.Sleep(2);
            }
            return done();
        }
        string Screen() => screens.LastOrDefault(x => x.Contains("\"mask\":1000,")) ?? "";
        bool ok = StepUntil(() => Screen().Length > 0, 15000);
        log($"{clock.Elapsed.TotalMilliseconds,7:F0} ms  {(ok ? "input mask drawn" : "FAIL: the input mask was not drawn")}");
        int failures = ok ? 0 : 1;
        void Check(string what, bool pass)
        {
            if (!pass) failures++;
            log($"  {(pass ? "ok  " : "FAIL")} {what}");
        }
        bool Heard(string fragment) => StepUntil(() => heard.Any(h => h.Contains(fragment)));
        bool Drawn(string fragment) => StepUntil(() => Screen().Contains(fragment));
        string LastEdit() => edits.LastOrDefault() ?? "";

        if (ok)
        {
            heard.Clear();
            Check("select Rate (9000) open: 1", isoshim_select(0, 9000, 1) == 1);
            Check("the implement hears it selected, open", Heard("\"select\",\"o\":9000,\"selected\":true,\"open\":true"));
            Check("edit message: a number, 0 to 100, scale 0.5", LastEdit().Contains("\"o\":9000,\"kind\":\"number\",\"value\":50,\"min\":0,\"max\":100,\"offset\":0,\"scale\":0.5"));
            Check("the screen marks it selected and open", Drawn("\"o\":9000,\"x\":150,\"y\":40,\"w\":120,\"h\":20,\"kind\":\"input\",\"on\":true,\"sel\":true,\"open\":true"));
            Check("Rate 101, above its maximum: -1", isoshim_set_number(0, 9000, 101) == -1);
            Check("Rate 80: 1", isoshim_set_number(0, 9000, 80) == 1);
            Check("the implement hears 80", Heard("\"number\",\"o\":9000,\"value\":80"));
            Check("the screen shows 40.0", Drawn("\"s\":\"40.0\""));
            Check("the editor closes (o null)", LastEdit().Contains("\"o\":null"));
            Check("Locked (9002), disabled: 0", isoshim_set_number(0, 9002, 1) == 0);
            Check("Count (9001, a variable) 12, not selected first: 1", isoshim_set_number(0, 9001, 12) == 1);
            Check("the implement hears 12", Heard("\"number\",\"o\":9001,\"value\":12"));
            Check("the screen shows 12", Drawn("\"s\":\"12\""));
            Check("Name (8000) lower case, not in its valid characters: -1", SetString(0, 8000, "hello") == -1);
            Check("Name longer than its 10 characters: -1", SetString(0, 8000, "MUCH TOO LONG") == -1);
            Check("Name WORLD: 1", SetString(0, 8000, "WORLD") == 1);
            Check("the implement hears WORLD, padded", Heard("\"string\",\"o\":8000,\"value\":\"WORLD     \""));
            Check("the screen shows WORLD", Drawn("\"s\":\"WORLD     \""));
            Check("Note (8001, a variable) Grüße: 1", SetString(0, 8001, "Grüße") == 1);
            Check("the implement hears Grüße in Latin-1", Heard("\"string\",\"o\":8001,\"value\":\"Grüße   \""));
            Check("Note with a character outside Latin-1: -1", SetString(0, 8001, "€") == -1);
            Check("Enable (7000) 2: -1", isoshim_set_number(0, 7000, 2) == -1);
            Check("Enable 1: 1", isoshim_set_number(0, 7000, 1) == 1);
            Check("the implement hears 1", Heard("\"number\",\"o\":7000,\"value\":1"));
            Check("select Crop (10000) open: 1", isoshim_select(0, 10000, 1) == 1);
            Check("edit message: a list of three, each drawn", LastEdit().Contains("\"kind\":\"list\",\"value\":0,\"w\":200,\"h\":20,\"options\":[[")
                && LastEdit().Contains("\"s\":\"Canola\""));
            Check("Crop 3, past the end: -1", isoshim_set_number(0, 10000, 3) == -1);
            Check("Crop 2: 1", isoshim_set_number(0, 10000, 2) == 1);
            Check("the implement hears 2", Heard("\"number\",\"o\":10000,\"value\":2"));
            Check("the screen shows Canola", Drawn("\"s\":\"Canola\""));
            Check("select Rate open, then escape: 1", isoshim_select(0, 9000, 1) == 1 && isoshim_escape(0) == 1);
            Check("the implement hears ESC", Heard("\"esc\",\"o\":9000"));
            Check("the editor closes", LastEdit().Contains("\"o\":null"));
            Check("escape with nothing open: 0", isoshim_escape(0) == 0);
            Check("Rate is still 40.0 and still selected, not open", Drawn("\"s\":\"40.0\"") &&
                Drawn("\"o\":9000,\"x\":150,\"y\":40,\"w\":120,\"h\":20,\"kind\":\"input\",\"on\":true,\"sel\":true}"));
            Check("select nothing: 1, the implement hears Rate deselected", isoshim_select(0, 0xFFFF, 0) == 1 && Heard("\"select\",\"o\":9000,\"selected\":false"));
            heard.Clear();
            Check("the implement's ESC with nothing open: answered, error 1", TestSend(0x92, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF) == 1
                && Heard("\"escResponse\",\"o\":65535,\"error\":1"));
            Check("select Name open, then the implement's ESC: answered with Name", isoshim_select(0, 8000, 1) == 1
                && TestSend(0x92, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF) == 1 && Heard("\"escResponse\",\"o\":8000,\"error\":0"));
            Check("the editor closes, and no VT ESC goes back", LastEdit().Contains("\"o\":null") && !heard.Any(h => h.Contains("\"event\":\"esc\"")));
            Check("select Rate open, then the implement's Delete Object Pool: the editor closes", isoshim_select(0, 9000, 1) == 1
                && Heard("\"select\",\"o\":9000,\"selected\":true,\"open\":true") && TestSend(0xB2, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF) == 1
                && StepUntil(() => LastEdit().Contains("\"o\":null")));
            Check("nothing shown, the implement still listed, its pool gone", StepUntil(() => mask == 0xFFFF
                && workingSets.Contains("\"active\":null") && !workingSets.Contains("\"ready\"")));
            Check("no VT ESC goes to the implement for its deleted pool", !StepUntil(() => heard.Any(h => h.Contains("\"event\":\"esc\"")), 300));
        }
        isoshim_stop();
        log($"every callback on the stepping thread: {(callbackOffThread ? "no" : "yes")}");
        bool pass = failures == 0 && !callbackOffThread;
        log(pass ? "PASS: input fields" : $"FAIL: input fields, {failures} checks failed");
        return pass;
    }

    /// <summary>Alarms and audio on testpools/drawing.iop: its alarm mask (2000, high priority,
    /// signal 1) comes and goes in the alarms message; Control Audio Signal and Set Audio Volume
    /// from the implement reach the host.</summary>
    static bool RunAlarms(Action<string> log)
    {
        log("--- alarms and audio");
        stepThread = Environment.CurrentManagedThreadId;
        callbackOffThread = false;
        mask = poolBad = poolObjects = -1;
        screens.Clear();
        alarms.Clear();
        audio.Clear();
        store.Clear();
        byte[] pool = LoadPool("drawing.iop");
        var config = new Config();
        var host = new Host
        {
            Send = &OnSend, Event = &OnEvent, Message = &OnMessage, Image = &OnImage,
            Save = &OnSave, Load = &OnLoad, List = &OnList, Remove = &OnRemove,
        };
        int rc = isoshim_vt_start(&config, 1, &host);
        if (rc == 0)
            fixed (byte* p = pool)
                rc = isoshim_test_implement(0, p, pool.Length);
        if (rc != 0) { log($"FAIL start returned {rc}"); isoshim_stop(); return false; }
        var clock = Stopwatch.StartNew();
        bool StepUntil(Func<bool> done, double ms = 2000)
        {
            for (var end = clock.Elapsed.TotalMilliseconds + ms; clock.Elapsed.TotalMilliseconds < end;)
            {
                if (done()) return true;
                isoshim_step();
                Thread.Sleep(2);
            }
            return done();
        }
        int failures = 0;
        void Check(string what, bool pass)
        {
            if (!pass) failures++;
            log($"  {(pass ? "ok  " : "FAIL")} {what}");
        }
        const string none = "{\"type\":\"alarms\",\"list\":[]}";
        bool ready = StepUntil(() => mask == 1000, 15000);
        Check("the pool is up, data mask 1000", ready);
        if (ready)
        {
            Check("alarms: none", StepUntil(() => alarms.LastOrDefault() == none));
            Check("the implement shows alarm mask 2000: 1", isoshim_test_show(0, 2000) == 1);
            Check("alarms: 2000, high priority", StepUntil(() => alarms.LastOrDefault()?.Contains("\"mask\":2000,\"priority\":0,") == true));
            Check("its acoustic signal 1, as in the pool", alarms.LastOrDefault()?.Contains("\"signal\":1}") == true);
            Check("the screen is the alarm mask", StepUntil(() => screens.Any(x => x.Contains("\"mask\":2000,\"kind\":\"alarm\""))));
            Check("back to data mask 1000: alarms none", isoshim_test_show(0, 1000) == 1 && StepUntil(() => alarms.LastOrDefault() == none));
            Check("Control Audio Signal 3 x 1000 Hz, 200 ms on, 100 ms off",
                TestSend(0xA3, 3, 0xE8, 0x03, 0xC8, 0x00, 0x64, 0x00) == 1 &&
                StepUntil(() => audio.Any(a => a.Contains("\"type\":\"audio\"") && a.EndsWith("\"activations\":3,\"frequency\":1000,\"on\":200,\"off\":100}"))));
            Check("Set Audio Volume 50 %", TestSend(0xA4, 50, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF) == 1 &&
                StepUntil(() => audio.Any(a => a.EndsWith("\"percent\":50}"))));
        }
        isoshim_stop();
        log($"every callback on the stepping thread: {(callbackOffThread ? "no" : "yes")}");
        bool pass = failures == 0 && !callbackOffThread;
        log(pass ? "PASS: alarms and audio" : $"FAIL: alarms and audio, {failures} checks failed");
        return pass;
    }

    /// <summary>Two terminals in one stack, as two displays in one cab: the example implement
    /// (VT3TestPool) on the first, the inputs implement on the second. Each terminal has its own
    /// NAME and address and only its own implement; both screens are shown at once and both
    /// take input; Identify VT reaches both.</summary>
    static bool RunTerminals(Action<string> log)
    {
        log("--- two terminals");
        stepThread = Environment.CurrentManagedThreadId;
        callbackOffThread = false;
        tagged.Clear();
        serverAddresses.Clear();
        clientAddresses.Clear();
        imageOwners.Clear();
        store.Clear();
        nacksFrom0 = 0;
        int failures = 0;
        void Check(string what, bool pass)
        {
            if (!pass) failures++;
            log($"  {(pass ? "ok  " : "FAIL")} {what}");
        }
        var host = new Host
        {
            Send = &OnSend, Event = &OnEvent, Message = &OnMessage, Image = &OnImage,
            Save = &OnSave, Load = &OnLoad, List = &OnList, Remove = &OnRemove,
        };
        var configs = new Config[5];
        Host* hostAt = &host;
        int Start(int count)
        {
            fixed (Config* p = configs) return isoshim_vt_start(p, count, hostAt);
        }
        Check("two terminals with one NAME: refused", Start(2) == -1);
        Check("none, or five: refused", Start(0) == -1 && Start(5) == -1);
        configs[1].FunctionInstance = 1;
        int rc = Start(2);
        Check("two terminals, function instances 0 and 1: started", rc == 0);
        if (rc != 0) return false;
        Check("a second start while running: -2", Start(1) == -2);
        byte[] example = LoadPool(), inputs = LoadPool("inputs.iop");
        int a, b;
        fixed (byte* p = example) a = isoshim_test_implement(0, p, example.Length);
        fixed (byte* p = inputs) b = isoshim_test_implement(1, p, inputs.Length);
        Check("a test implement for each: 0 and 1", a == 0 && b == 1);
        Check("none for a terminal that isn't there", isoshim_test_implement(2, null, 0) == -1);

        var clock = Stopwatch.StartNew();
        bool StepUntil(Func<bool> done, double ms = 2000)
        {
            for (var end = clock.Elapsed.TotalMilliseconds + ms; clock.Elapsed.TotalMilliseconds < end;)
            {
                if (done()) return true;
                isoshim_step();
                Thread.Sleep(2);
            }
            return done();
        }
        string Last(int terminal, string type) =>
            tagged.LastOrDefault(m => m.Terminal == terminal && m.Json.StartsWith($"{{\"type\":\"{type}\"")).Json ?? "";
        bool Heard(int terminal, string fragment) =>
            StepUntil(() => tagged.Any(m => m.Terminal == terminal && m.Json.StartsWith("{\"type\":\"implement\"") && m.Json.Contains(fragment)));
        // The example's data mask has its alarm soft key; the inputs mask has the Rate field.
        bool ExampleShown(int t) => Last(t, "screen").Contains("\"mask\":1000,") && Last(t, "screen").Contains("\"o\":5000,");
        bool InputsShown(int t) => Last(t, "screen").Contains("\"mask\":1000,") && Last(t, "screen").Contains("\"o\":9000,");

        bool both = StepUntil(() => ExampleShown(0) && InputsShown(1), 20000);
        log($"{clock.Elapsed.TotalMilliseconds,7:F0} ms  {(both ? "both screens drawn" : "FAIL: not both screens drawn")}");
        Check("both screens shown at once: the example on the first, the inputs on the second", both);
        serverAddresses.TryGetValue(0, out int vt0);
        serverAddresses.TryGetValue(1, out int vt1);
        Check($"addresses 0x{vt0:X2} and 0x{vt1:X2} (0x26 and 0x27)", vt0 == 0x26 && vt1 == 0x27);
        if (both)
        {
            clientAddresses.TryGetValue(0, out int ia);
            clientAddresses.TryGetValue(1, out int ib);
            string sets0 = Last(0, "workingSets"), sets1 = Last(1, "workingSets");
            Check($"the first has only its implement (0x{ia:X2})", sets0.Contains($"\"address\":{ia},") && !sets0.Contains($"\"address\":{ib},"));
            Check($"the second has only its implement (0x{ib:X2})", sets1.Contains($"\"address\":{ib},") && !sets1.Contains($"\"address\":{ia},"));
            Check("the first shows the example, the second the inputs (neither switched)", ExampleShown(0) && InputsShown(1));

            Check("Rate 80 on the second: 1", isoshim_set_number(1, 9000, 80) == 1);
            Check("the second's implement hears 80", Heard(1, "\"number\",\"o\":9000,\"value\":80"));
            Check("the second shows 40.0", StepUntil(() => InputsShown(1) && Last(1, "screen").Contains("\"s\":\"40.0\"")));
            Check("soft key 5000 on the first: sent", isoshim_soft_key(0, 5000, Pressed) == 1 && isoshim_soft_key(0, 5000, Released) == 1);
            Check("the first shows its alarm mask", StepUntil(() => Last(0, "screen").Contains("\"mask\":2000,\"kind\":\"alarm\"")));
            Check("the second still shows the inputs", InputsShown(1));
            Check("Rate on the first, where it isn't drawn: 0", isoshim_set_number(0, 9000, 80) == 0);
            Check("a terminal that isn't there: 0", isoshim_set_number(2, 9000, 80) == 0 && isoshim_soft_key(-1, 5000, Pressed) == 0);
            Check("the first's alarm only in the first's alarms", Last(0, "alarms").Contains("\"mask\":2000,") && !Last(1, "alarms").Contains("\"mask\":2000,"));

            int identify0 = tagged.Count(m => m.Terminal == 0 && m.Json == "{\"type\":\"identify\"}");
            int identify1 = tagged.Count(m => m.Terminal == 1 && m.Json == "{\"type\":\"identify\"}");
            Check("Identify VT to everyone: both terminals show their number",
                TestSend(1, true, 0xBB, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF) == 1 &&
                StepUntil(() => tagged.Count(m => m.Terminal == 0 && m.Json == "{\"type\":\"identify\"}") == identify0 + 1
                             && tagged.Count(m => m.Terminal == 1 && m.Json == "{\"type\":\"identify\"}") == identify1 + 1));
            Check("Identify VT to its own: only that one",
                TestSend(1, false, 0xBB, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF) == 1 &&
                StepUntil(() => tagged.Count(m => m.Terminal == 1 && m.Json == "{\"type\":\"identify\"}") == identify1 + 2) &&
                tagged.Count(m => m.Terminal == 0 && m.Json == "{\"type\":\"identify\"}") == identify0 + 1);
            var ids0 = imageOwners.Where(o => o.Terminal == 0).Select(o => o.Id).ToHashSet();
            var ids1 = imageOwners.Where(o => o.Terminal == 1).Select(o => o.Id).ToHashSet();
            Check($"picture ids not shared between terminals ({ids0.Count} and {ids1.Count})", !ids0.Overlaps(ids1));
            StepUntil(() => false, 300);
            Check($"no terminal refused the other's implement (NACKs: {nacksFrom0})", nacksFrom0 == 0);
        }
        isoshim_stop();
        log($"every callback on the stepping thread: {(callbackOffThread ? "no" : "yes")}");
        bool pass = failures == 0 && !callbackOffThread;
        log(pass ? "PASS: two terminals" : $"FAIL: two terminals, {failures} checks failed");
        return pass;
    }

    /// <summary>Uploads any pool through the test implement, has it show each of its data and alarm
    /// masks in turn, and writes every screen to ScreenDir. No pass or fail beyond each mask being drawn.</summary>
    public static bool Tour(string poolPath, Action<string> log)
    {
        byte[] pool = File.ReadAllBytes(poolPath);
        stepThread = Environment.CurrentManagedThreadId;
        mask = poolBad = poolObjects = -1;
        workingSets = null;
        screens.Clear();
        images.Clear();
        store.Clear();
        var config = new Config();
        var host = new Host
        {
            Send = &OnSend, Event = &OnEvent, Message = &OnMessage, Image = &OnImage,
            Save = &OnSave, Load = &OnLoad, List = &OnList, Remove = &OnRemove,
        };
        int rc = isoshim_vt_start(&config, 1, &host);
        if (rc == 0)
            fixed (byte* p = pool)
                rc = isoshim_test_implement(0, p, pool.Length);
        if (rc != 0) { log($"FAIL start returned {rc}"); isoshim_stop(); return false; }
        var clock = Stopwatch.StartNew();
        void StepFor(double ms, Func<bool> done)
        {
            for (var end = clock.Elapsed.TotalMilliseconds + ms; clock.Elapsed.TotalMilliseconds < end && !done();)
            { isoshim_step(); Thread.Sleep(2); }
        }
        bool NotSelectable() => poolObjects >= 0 && workingSets?.Contains("\"selectable\":false") == true;
        StepFor(15000, () => (poolObjects >= 0 && mask >= 0 && mask != 0xFFFF) || poolBad >= 0 || NotSelectable());
        if (NotSelectable())
        {
            log($"{Path.GetFileName(poolPath)}: {poolObjects} objects, the working set is not selectable: nothing to show");
            isoshim_stop();
            return true;
        }
        if (poolObjects < 0 || mask < 0 || mask == 0xFFFF) { log(poolBad >= 0 ? $"FAIL pool rejected at object {poolBad}" : $"FAIL no mask within 15 s (objects {poolObjects}, mask {mask}, working sets {workingSets})"); isoshim_stop(); return false; }
        log($"{clock.Elapsed.TotalMilliseconds,7:F0} ms  {Path.GetFileName(poolPath)}: {poolObjects} objects, showing mask {mask}");
        StepFor(300, () => false);
        var ids = new ushort[256];
        int count;
        fixed (ushort* p = ids) count = Math.Min(isoshim_test_masks(0, p, ids.Length), ids.Length);
        bool ok = true;
        foreach (ushort id in ids.Take(count))
        {
            string tag = $"\"mask\":{id},";
            if (!screens.Any(s => s.Contains(tag)))
            {
                isoshim_test_show(0, id);
                StepFor(3000, () => screens.Any(s => s.Contains(tag)));
            }
            bool drawn = screens.Any(s => s.Contains(tag));
            ok &= drawn;
            log($"  mask {id}: {(drawn ? "drawn" : "NOT drawn")}");
        }
        isoshim_stop();
        WriteScreens(log);
        return ok;
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
        // screens.jsonl, for comparing runs (bench/pools.sh): each mask's last screen in mask order,
        // then each picture's size and a hash of its pixels.
        var last = new SortedDictionary<int, string>();
        foreach (var screen in screens)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(screen);
            if (doc.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == System.Text.Json.JsonValueKind.String)
                last[doc.RootElement.GetProperty("mask").GetInt32()] = screen;
        }
        var lines = new StringBuilder();
        foreach (var screen in last.Values) lines.Append(screen).Append('\n');
        foreach (var (id, img) in images.OrderBy(i => i.Key))
            lines.Append($"{{\"type\":\"image\",\"id\":{id},\"w\":{img.W},\"h\":{img.H},\"sha256\":\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(img.Rgba))}\"}}\n");
        File.WriteAllText(Path.Combine(ScreenDir!, "screens.jsonl"), lines.ToString());
        log($"screens written to {Path.Combine(ScreenDir!, "index.html")}");
    }
}
