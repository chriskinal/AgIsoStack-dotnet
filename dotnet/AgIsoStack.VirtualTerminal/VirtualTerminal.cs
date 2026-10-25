// An ISOBUS virtual terminal for a .NET host (docs/API.md). It owns one thread, which runs the
// stack and raises every event; the host's calls are carried to it. One per process: the native
// stack is one.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgIsoStack.Interop;
using static AgIsoStack.Interop.IsoShim;

namespace AgIsoStack;

/// <summary>
/// The terminal: frames in with <see cref="Receive(uint, ReadOnlySpan{byte}, bool, int)"/>, frames
/// out through <see cref="VirtualTerminalOptions.Send"/>, the screens and editors as JSON through
/// <see cref="Message"/> (for the page to paint, unchanged), the rest as typed events, and the
/// operator's input as calls.
/// </summary>
/// <remarks>
/// Every event and every <see cref="IPoolStore"/> call comes on the terminal's own thread, in
/// order. Keep handlers short; one that throws is reported through <see cref="HandlerFailed"/>.
/// The calls can come from any thread (from a handler too); each returns when the terminal has
/// done it.
/// </remarks>
public sealed unsafe class VirtualTerminal : IDisposable
{
    static readonly object Gate = new();
    static VirtualTerminal? running;

    readonly VirtualTerminalOptions options;
    readonly IPoolStore store;
    readonly ConcurrentQueue<Action> work = new();
    readonly AutoResetEvent wake = new(false);
    Thread? thread;
    volatile bool stopping;
    bool closed; // under work's lock: no more calls are taken

    public VirtualTerminal(VirtualTerminalOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.Send == null) throw new ArgumentException("Send is required", nameof(options));
        store = options.Store ?? new MemoryPoolStore();
    }

    // ---- events (on the terminal's thread) ----

    /// <summary>Every message, as JSON: <c>screen</c> and <c>edit</c> for the page, and
    /// <c>workingSets</c>, <c>alarms</c>, <c>audio</c>, <c>volume</c> (also below as types).</summary>
    public event Action<string>? Message;
    /// <summary>A picture the screens refer to, sent before the first screen that uses it.</summary>
    public event Action<VtImage>? Image;
    /// <summary>The implements connected and the one shown (null: none).</summary>
    public event Action<ulong?, IReadOnlyList<WorkingSetInfo>>? WorkingSetsChanged;
    /// <summary>Every implement showing an alarm mask, shown or not.</summary>
    public event Action<IReadOnlyList<AlarmInfo>>? AlarmsChanged;
    public event Action<AudioSignal>? Audio;
    /// <summary>Set Audio Volume from an implement: its NAME and the percentage.</summary>
    public event Action<ulong, int>? Volume;
    /// <summary>The terminal has its address on the bus.</summary>
    public event Action<int>? AddressClaimed;
    /// <summary>An implement's pool is up: its address, the number of objects.</summary>
    public event Action<int, int>? PoolLoaded;
    /// <summary>An implement's pool was refused: its address, the object at fault.</summary>
    public event Action<int, int>? PoolRejected;
    /// <summary>An implement went away (no maintenance messages): its address, the reason code.</summary>
    public event Action<int, int>? ImplementLost;
    /// <summary>A handler or the store threw; the terminal carries on.</summary>
    public event Action<Exception>? HandlerFailed;

    /// <summary>The terminal's address, or -1 before it has claimed one.</summary>
    public int Address { get; private set; } = -1;
    public bool IsRunning => thread != null;

    // ---- life ----

    /// <summary>Starts the terminal on its own thread. Throws if another one is running in this
    /// process, or if the native library refuses the options.</summary>
    public void Start()
    {
        lock (Gate)
        {
            if (running != null) throw new InvalidOperationException("A terminal is already running in this process.");
            running = this;
        }
        stopping = false;
        lock (work) closed = false;
        var started = new TaskCompletionSource<int>();
        var t = new Thread(() => Run(started)) { IsBackground = true, Name = "VirtualTerminal" };
        thread = t;
        t.Start();
        int rc = started.Task.GetAwaiter().GetResult();
        if (rc != 0)
        {
            t.Join();
            throw new InvalidOperationException($"The terminal did not start ({rc}).");
        }
    }

    /// <summary>Takes the terminal off the bus and ends its thread (any pool still parsing is
    /// waited for), then returns. Calls not yet done fail. From the terminal's own thread (a
    /// handler, or code that resumed there after awaiting something a handler completed) it only
    /// asks: the terminal stops after the current step.</summary>
    public void Stop()
    {
        var t = thread;
        if (t == null) return;
        stopping = true;
        wake.Set();
        if (Thread.CurrentThread != t) t.Join();
    }

    public void Dispose() => Stop();

    void Run(TaskCompletionSource<int> started)
    {
        var config = new Config
        {
            Identity = options.Identity, Manufacturer = options.Manufacturer, FunctionInstance = options.FunctionInstance,
            PreferredAddress = options.PreferredAddress, MaskWidth = options.MaskWidth, MaskHeight = options.MaskHeight,
            KeyWidth = options.KeyWidth, KeyHeight = options.KeyHeight, PhysicalKeys = options.PhysicalKeys,
            VirtualKeys = options.VirtualKeys, NavigationKeys = options.NavigationKeys, Version = options.Version,
            SmallFonts = options.SmallFonts, LargeFonts = options.LargeFonts, PacketsPerCts = options.PacketsPerCts,
        };
        var host = new Host
        {
            Send = &OnSend, Event = &OnEvent, Message = &OnMessage, Image = &OnImage,
            Save = &OnSave, Load = &OnLoad, List = &OnList, Remove = &OnRemove,
        };
        if (options.Log != null) isoshim_set_log(&OnLog, options.LogLevel);
        int rc = isoshim_vt_start(&config, &host);
        if (rc != 0)
        {
            isoshim_set_log(null, 0);
            Ended();
            started.SetResult(rc);
            return;
        }
        started.SetResult(rc);
        int interval = options.StepMilliseconds > 0 ? options.StepMilliseconds : 5;
        while (!stopping)
        {
            while (work.TryDequeue(out var call)) call();
            isoshim_step();
            wake.WaitOne(interval);
        }
        // The calls still queued, and any that raced the stop, are done before the stack goes
        // (they find it running and finish).
        lock (work) closed = true;
        while (work.TryDequeue(out var call)) call();
        isoshim_stop();
        isoshim_set_log(null, 0);
        Address = -1;
        Ended();
    }

    // The thread's last act: another terminal may start.
    void Ended()
    {
        lock (work) closed = true;
        thread = null;
        lock (Gate) running = null;
    }

    // ---- frames ----

    /// <summary>A frame from the bus. Any thread; the terminal takes it at once.</summary>
    public void Receive(uint id, ReadOnlySpan<byte> data, bool extended = true, int channel = 0)
    {
        if (data.Length > 8) throw new ArgumentException("A CAN frame holds at most 8 bytes.", nameof(data));
        fixed (byte* p = data) isoshim_receive(channel, id, p, data.Length, extended ? 1 : 0);
        wake.Set();
    }

    // ---- the operator ----

    /// <summary>A soft key (Key object) of the shown implement. True: sent.</summary>
    public Task<bool> SoftKeyAsync(ushort objectId, KeyState state) => InvokeAsync(() => isoshim_soft_key(objectId, (int)state) == 1);

    /// <summary>A button on the shown mask. True: sent.</summary>
    public Task<bool> ButtonAsync(ushort objectId, KeyState state) => InvokeAsync(() => isoshim_button(objectId, (int)state) == 1);

    /// <summary>Shows this implement, by its NAME. True: it is now the one shown.</summary>
    public Task<bool> ActivateAsync(ulong name) => InvokeAsync(() => isoshim_activate(name) == 1);

    /// <summary>Selects an input field or a button on the screen (open: an editor is up for it;
    /// an edit message follows). True: done.</summary>
    public Task<bool> SelectAsync(ushort objectId, bool open) => InvokeAsync(() => isoshim_select(objectId, open ? 1 : 0) == 1);

    /// <summary>Selects nothing.</summary>
    public Task<bool> DeselectAsync() => InvokeAsync(() => isoshim_select(0xFFFF, 0) == 1);

    /// <summary>A value for an input number (raw: before offset and scale), boolean (0 or 1) or
    /// list (the index).</summary>
    public Task<InputResult> SetNumberAsync(ushort objectId, uint value) => InvokeAsync(() => (InputResult)isoshim_set_number(objectId, value));

    /// <summary>Text for an input string.</summary>
    public Task<InputResult> SetStringAsync(ushort objectId, string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return InvokeAsync(() =>
        {
            fixed (byte* p = utf8) return (InputResult)isoshim_set_string(objectId, p, utf8.Length);
        });
    }

    /// <summary>Abandons the open field. True: one was open.</summary>
    public Task<bool> EscapeAsync() => InvokeAsync(() => isoshim_escape() == 1);

    /// <summary>Runs code on the terminal's thread, between steps of the stack: for the raw
    /// interface (<see cref="IsoShim"/>), which belongs to that thread.</summary>
    public Task<T> InvokeAsync<T>(Func<T> call)
    {
        if (Thread.CurrentThread == thread) return Task.FromResult(call());
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (work)
        {
            if (thread == null || closed)
            {
                done.SetException(new InvalidOperationException("The terminal is not running."));
                return done.Task;
            }
            work.Enqueue(() =>
            {
                try { done.SetResult(call()); }
                catch (Exception e) { done.SetException(e); }
            });
        }
        wake.Set();
        return done.Task;
    }

    // ---- the native callbacks: all on the terminal's thread, except the log ----

    static void Raise(Action handler)
    {
        try { handler(); }
        catch (Exception e)
        {
            try { running?.HandlerFailed?.Invoke(e); } catch { }
        }
    }

    [UnmanagedCallersOnly]
    static void OnSend(int channel, uint id, byte* data, int len, int extended)
    {
        var self = running;
        if (self == null) return;
        try { self.options.Send!(channel, id, new ReadOnlySpan<byte>(data, len), extended != 0); }
        catch (Exception e) { Raise(() => self.HandlerFailed?.Invoke(e)); }
    }

    [UnmanagedCallersOnly]
    static void OnEvent(int kind, int a, int b)
    {
        var self = running;
        if (self == null) return;
        switch (kind)
        {
            case ServerAddress:
                self.Address = a;
                Raise(() => self.AddressClaimed?.Invoke(a));
                break;
            case PoolOk: Raise(() => self.PoolLoaded?.Invoke(b, a)); break;
            case PoolBad: Raise(() => self.PoolRejected?.Invoke(b, a)); break;
            case WsShutdown: Raise(() => self.ImplementLost?.Invoke(b, a)); break;
        }
    }

    [UnmanagedCallersOnly]
    static void OnMessage(byte* json, int len)
    {
        var self = running;
        if (self == null) return;
        string text = Encoding.UTF8.GetString(json, len);
        Raise(() => self.Message?.Invoke(text));
        // The typed events, parsed only when someone listens.
        if (text.StartsWith("{\"type\":\"workingSets\"") && self.WorkingSetsChanged != null)
            Raise(() =>
            {
                using var doc = JsonDocument.Parse(text);
                var (active, list) = Messages.WorkingSets(doc.RootElement);
                self.WorkingSetsChanged?.Invoke(active, list);
            });
        else if (text.StartsWith("{\"type\":\"alarms\"") && self.AlarmsChanged != null)
            Raise(() =>
            {
                using var doc = JsonDocument.Parse(text);
                self.AlarmsChanged?.Invoke(Messages.Alarms(doc.RootElement));
            });
        else if (text.StartsWith("{\"type\":\"audio\"") && self.Audio != null)
            Raise(() =>
            {
                using var doc = JsonDocument.Parse(text);
                self.Audio?.Invoke(Messages.Audio(doc.RootElement));
            });
        else if (text.StartsWith("{\"type\":\"volume\"") && self.Volume != null)
            Raise(() =>
            {
                using var doc = JsonDocument.Parse(text);
                self.Volume?.Invoke(Messages.Name(doc.RootElement.GetProperty("ws")), doc.RootElement.GetProperty("percent").GetInt32());
            });
    }

    [UnmanagedCallersOnly]
    static void OnImage(uint id, int w, int h, byte* rgba)
    {
        var self = running;
        if (self?.Image == null) return;
        var image = new VtImage(id, w, h, new ReadOnlySpan<byte>(rgba, w * h * 4).ToArray());
        Raise(() => self.Image?.Invoke(image));
    }

    static bool Store(Func<IPoolStore, bool> call)
    {
        var store = running?.store;
        if (store == null) return false;
        bool ok = false;
        Raise(() => ok = call(store));
        return ok;
    }

    [UnmanagedCallersOnly]
    static int OnSave(ulong name, byte* label, int labelLen, byte* data, int len, int append) =>
        Store(s => s.Save(name, new ReadOnlySpan<byte>(label, labelLen), new ReadOnlySpan<byte>(data, len), append != 0)) ? 1 : 0;

    [UnmanagedCallersOnly]
    static int OnLoad(ulong name, byte* label, int labelLen) => Store(s =>
    {
        byte[]? pool = s.Load(name, new ReadOnlySpan<byte>(label, labelLen));
        if (pool == null) return false;
        fixed (byte* p = pool) isoshim_reply(p, pool.Length);
        return true;
    }) ? 1 : 0;

    [UnmanagedCallersOnly]
    static void OnList(ulong name, int labelLen) => Store(s =>
    {
        foreach (byte[] label in s.List(name, labelLen))
            if (label.Length == labelLen)
                fixed (byte* p = label) isoshim_reply(p, label.Length);
        return true;
    });

    [UnmanagedCallersOnly]
    static int OnRemove(ulong name, byte* label, int labelLen) => Store(s =>
        label == null ? s.RemoveAll(name, labelLen) : s.Remove(name, new ReadOnlySpan<byte>(label, labelLen))) ? 1 : 0;

    [UnmanagedCallersOnly]
    static void OnLog(int level, byte* text)
    {
        var log = running?.options.Log;
        if (log == null) return;
        try { log(level, Marshal.PtrToStringUTF8((IntPtr)text) ?? ""); } catch { }
    }
}
