// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors
//
// The stack for a .NET host (docs/API.md): one to four virtual terminals on one bus, as several
// displays in one cab. It owns one thread, which runs the stack and raises every event; the
// host's calls are carried to it. One per process: the native stack is one.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpIsoTerm.Interop;
using static SharpIsoTerm.Interop.IsoShim;

namespace SharpIsoTerm;

/// <summary>
/// The terminals and their bus: frames in with <see cref="Receive(uint, ReadOnlySpan{byte}, bool, int)"/>,
/// frames out through <see cref="VirtualTerminalOptions.Send"/>, and one
/// <see cref="VirtualTerminal"/> for each of <see cref="VirtualTerminalOptions.Count"/> terminals,
/// each with its own NAME and address. Every implement connects to the terminal it chooses.
/// </summary>
/// <remarks>
/// Every event and every <see cref="IPoolStore"/> call comes on the stack's own thread, in order.
/// Keep handlers short; one that throws is reported through <see cref="HandlerFailed"/>. The
/// calls can come from any thread (from a handler too); each returns when the stack has done it.
/// </remarks>
public sealed unsafe class VirtualTerminalStack : IDisposable
{
    static readonly object Gate = new();
    static VirtualTerminalStack? running;

    readonly VirtualTerminalOptions options;
    readonly IPoolStore store;
    readonly ConcurrentQueue<Action> work = new();
    readonly AutoResetEvent wake = new(false);
    readonly VirtualTerminal[] terminals;
    Thread? thread;
    volatile bool stopping;
    bool closed; // under work's lock: no more calls are taken
    // On the stack's thread: keys and buttons pressed and not released, by terminal, with when each
    // is next sent as still held; and those owed an abort when a gateway link comes back (held
    // when it went, or pressed while it was down).
    readonly Dictionary<(int Terminal, bool Button, ushort Id), long> held = new();
    readonly HashSet<(int Terminal, bool Button, ushort Id)> owed = new();
    /// <summary>How often a key or button still down is sent as held (ISO 11783-6: about every 200 ms).</summary>
    public const int HeldRepeatMilliseconds = 200;
    bool linkDown;

    public VirtualTerminalStack(VirtualTerminalOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.Send == null) throw new ArgumentException("Send is required", nameof(options));
        int count = options.Count == 0 ? 1 : options.Count;
        if (count < 1 || count > MaxTerminals)
            throw new ArgumentException($"Count is 1 to {MaxTerminals}", nameof(options));
        store = options.Store ?? new MemoryPoolStore();
        terminals = Enumerable.Range(0, count).Select(i => new VirtualTerminal(this, i)).ToArray();
    }

    /// <summary>The terminals, by position (0 first: function instance
    /// <see cref="VirtualTerminalOptions.FunctionInstance"/>, the next one more, and so on).</summary>
    public IReadOnlyList<VirtualTerminal> Terminals => terminals;

    /// <summary>A handler or the store threw; the stack carries on.</summary>
    public event Action<Exception>? HandlerFailed;

    public bool IsRunning => thread != null;

    // ---- life ----

    /// <summary>Starts the terminals on the stack's own thread. Throws if another stack is running
    /// in this process, or if the native library refuses the options.</summary>
    public void Start()
    {
        lock (Gate)
        {
            if (running != null) throw new InvalidOperationException("A terminal stack is already running in this process.");
            running = this;
        }
        stopping = false;
        held.Clear();
        owed.Clear();
        linkDown = false;
        lock (work) closed = false;
        var started = new TaskCompletionSource<int>();
        var t = new Thread(() => Run(started)) { IsBackground = true, Name = "VirtualTerminal" };
        thread = t;
        t.Start();
        int rc = started.Task.GetAwaiter().GetResult();
        if (rc != 0)
        {
            t.Join();
            throw new InvalidOperationException($"The terminals did not start ({rc}).");
        }
    }

    /// <summary>Takes the terminals off the bus and ends the stack's thread (any pool still
    /// parsing is waited for), then returns. Calls not yet done fail. From the stack's own thread
    /// (a handler, or code that resumed there after awaiting something a handler completed) it
    /// only asks: the stack stops after the current step.</summary>
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
        var configs = new Config[terminals.Length];
        for (int i = 0; i < configs.Length; i++)
            configs[i] = new Config
            {
                Identity = options.Identity, Manufacturer = options.Manufacturer, FunctionInstance = (byte)(options.FunctionInstance + i),
                PreferredAddress = options.PreferredAddress == 0 ? (byte)0 : (byte)(options.PreferredAddress + i),
                MaskWidth = options.MaskWidth, MaskHeight = options.MaskHeight,
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
        int rc;
        fixed (Config* p = configs) rc = isoshim_vt_start(p, configs.Length, &host);
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
            RepeatHeld();
            isoshim_step();
            wake.WaitOne(interval);
        }
        // The calls still queued, and any that raced the stop, are done before the stack goes
        // (they find it running and finish).
        lock (work) closed = true;
        while (work.TryDequeue(out var call)) call();
        isoshim_stop();
        isoshim_set_log(null, 0);
        foreach (var t in terminals) t.Address = -1;
        Ended();
    }

    // The thread's last act: another stack may start.
    void Ended()
    {
        lock (work) closed = true;
        thread = null;
        lock (Gate) running = null;
    }

    // ---- frames ----

    /// <summary>A frame from the bus. Any thread; the stack takes it at once.</summary>
    public void Receive(uint id, ReadOnlySpan<byte> data, bool extended = true, int channel = 0)
    {
        if (data.Length > 8) throw new ArgumentException("A CAN frame holds at most 8 bytes.", nameof(data));
        fixed (byte* p = data) isoshim_receive(channel, id, p, data.Length, extended ? 1 : 0);
        wake.Set();
    }

    // ---- keys (VirtualTerminal calls these on the stack's thread) ----

    internal bool Key(int terminal, bool button, ushort id, KeyState state)
    {
        bool sent = (button ? isoshim_button(terminal, id, (int)state) : isoshim_soft_key(terminal, id, (int)state)) == 1;
        var key = (terminal, button, id);
        if (state is KeyState.Pressed or KeyState.Held)
        {
            if (sent) held[key] = Environment.TickCount64 + HeldRepeatMilliseconds;
            if (sent && linkDown) owed.Add(key);
        }
        else held.Remove(key);
        return sent;
    }

    // Every key and button still down, sent as held when it is due, so the host sends only the
    // press and its end. Not while the link is down (the frames would be lost; the key is owed an
    // abort). One that has left the screen (its mask changed) is no longer sent.
    void RepeatHeld()
    {
        if (held.Count == 0 || linkDown) return;
        long now = Environment.TickCount64;
        foreach (var key in held.Where(k => k.Value <= now).Select(k => k.Key).ToList())
        {
            var (terminal, button, id) = key;
            int state = (int)KeyState.Held;
            if ((button ? isoshim_button(terminal, id, state) : isoshim_soft_key(terminal, id, state)) == 1)
                held[key] = now + HeldRepeatMilliseconds;
            else
                held.Remove(key);
        }
    }

    // ---- a gateway link (VirtualTerminalLink.Attach calls these) ----

    /// <summary>The link to the bus is gone: what the terminals send is lost until it is back.
    /// Any thread.</summary>
    public void LinkDown()
    {
        if (!IsRunning) return;
        _ = InvokeAsync(() =>
        {
            linkDown = true;
            owed.UnionWith(held.Keys);
            return 0;
        });
    }

    /// <summary>The link to the bus is back: every key or button held when it went, or pressed
    /// while it was down, is sent as aborted (a press is always followed by a release). Any
    /// thread.</summary>
    public void LinkUp()
    {
        if (!IsRunning) return;
        _ = InvokeAsync(() =>
        {
            linkDown = false;
            foreach (var (terminal, button, id) in owed)
            {
                if (button) isoshim_button(terminal, id, (int)KeyState.Aborted);
                else isoshim_soft_key(terminal, id, (int)KeyState.Aborted);
                held.Remove((terminal, button, id));
            }
            owed.Clear();
            // Each terminal's address claim again, and every implement's: claims sent while the
            // link was down (at start, or during a cut) never crossed it.
            isoshim_link_up();
            return 0;
        });
    }

    /// <summary>Runs code on the stack's thread, between steps: for the raw interface
    /// (<see cref="IsoShim"/>), which belongs to that thread.</summary>
    public Task<T> InvokeAsync<T>(Func<T> call)
    {
        if (Thread.CurrentThread == thread) return Task.FromResult(call());
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (work)
        {
            if (thread == null || closed)
            {
                done.SetException(new InvalidOperationException("The terminals are not running."));
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

    // ---- the native callbacks: all on the stack's thread, except the log ----

    static void Raise(Action handler)
    {
        try { handler(); }
        catch (Exception e)
        {
            try { running?.HandlerFailed?.Invoke(e); } catch { }
        }
    }

    static VirtualTerminal? TerminalAt(int index)
    {
        var self = running;
        return self != null && index >= 0 && index < self.terminals.Length ? self.terminals[index] : null;
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
    static void OnEvent(int terminal, int kind, int a, int b)
    {
        var vt = TerminalAt(terminal);
        if (vt == null) return;
        switch (kind)
        {
            case ServerAddress:
                vt.Address = a;
                Raise(() => vt.RaiseAddressClaimed(a));
                break;
            case PoolOk: Raise(() => vt.RaisePoolLoaded(b, a)); break;
            case PoolBad: Raise(() => vt.RaisePoolRejected(b, a)); break;
            case WsShutdown: Raise(() => vt.RaiseImplementLost(b, a)); break;
        }
    }

    [UnmanagedCallersOnly]
    static void OnMessage(int terminal, byte* json, int len)
    {
        var vt = TerminalAt(terminal);
        if (vt == null) return;
        string text = Encoding.UTF8.GetString(json, len);
        Raise(() => vt.RaiseMessage(text));
        // The typed events, parsed only when someone listens.
        if (text.StartsWith("{\"type\":\"workingSets\"") && vt.HasWorkingSetsListener)
            Raise(() =>
            {
                using var doc = JsonDocument.Parse(text);
                var (active, list) = Messages.WorkingSets(doc.RootElement);
                vt.RaiseWorkingSetsChanged(active, list);
            });
        else if (text.StartsWith("{\"type\":\"alarms\"") && vt.HasAlarmsListener)
            Raise(() =>
            {
                using var doc = JsonDocument.Parse(text);
                vt.RaiseAlarmsChanged(Messages.Alarms(doc.RootElement));
            });
        else if (text.StartsWith("{\"type\":\"audio\"") && vt.HasAudioListener)
            Raise(() =>
            {
                using var doc = JsonDocument.Parse(text);
                vt.RaiseAudio(Messages.Audio(doc.RootElement));
            });
        else if (text.StartsWith("{\"type\":\"volume\"") && vt.HasVolumeListener)
            Raise(() =>
            {
                using var doc = JsonDocument.Parse(text);
                vt.RaiseVolume(Messages.Name(doc.RootElement.GetProperty("ws")), doc.RootElement.GetProperty("percent").GetInt32());
            });
        else if (text.StartsWith("{\"type\":\"identify\""))
            Raise(vt.RaiseIdentify);
    }

    [UnmanagedCallersOnly]
    static void OnImage(int terminal, uint id, int w, int h, byte* rgba)
    {
        var vt = TerminalAt(terminal);
        if (vt == null || !vt.HasImageListener) return;
        var image = new VtImage(id, w, h, new ReadOnlySpan<byte>(rgba, w * h * 4).ToArray());
        Raise(() => vt.RaiseImage(image));
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
