// What a host configures, and the terminal's messages as types. The screen and edit messages stay
// JSON (VirtualTerminal.Message): the page paints them, the host only relays them.
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AgIsoStack;

/// <summary>Sends one CAN frame to the bus (the gateway). Called on the terminal's thread; queue
/// it and return.</summary>
public delegate void FrameSender(int channel, uint id, ReadOnlySpan<byte> data, bool extended);

/// <summary>The terminal's setup. A zero (or null) takes the default shown.</summary>
public sealed class VirtualTerminalOptions
{
    /// <summary>Sends the terminal's frames. Required.</summary>
    public FrameSender? Send { get; init; }
    /// <summary>Stored pools; none: every implement uploads its pool each time.</summary>
    public IPoolStore? Store { get; init; }
    /// <summary>The terminal's NAME: identity number (3), manufacturer code (1407), function instance (0).</summary>
    public uint Identity { get; init; }
    public ushort Manufacturer { get; init; }
    public byte FunctionInstance { get; init; }
    /// <summary>The address it claims first (0x26).</summary>
    public byte PreferredAddress { get; init; }
    /// <summary>The data mask in pixels (480 x 480).</summary>
    public ushort MaskWidth { get; init; }
    public ushort MaskHeight { get; init; }
    /// <summary>A soft key in pixels (80 x 80); physical keys (6), keys a mask may hold (64), navigation keys (0).</summary>
    public byte KeyWidth { get; init; }
    public byte KeyHeight { get; init; }
    public byte PhysicalKeys { get; init; }
    public byte VirtualKeys { get; init; }
    public byte NavigationKeys { get; init; }
    /// <summary>VT version, 3 to 6 (5).</summary>
    public byte Version { get; init; }
    /// <summary>Supported font bitfields (0xFF, 0xFF).</summary>
    public byte SmallFonts { get; init; }
    public byte LargeFonts { get; init; }
    /// <summary>Frames per clear-to-send and per DPO, received and sent (255).</summary>
    public byte PacketsPerCts { get; init; }
    /// <summary>How often the stack runs, at most this many milliseconds apart (5). Frames that
    /// arrive wake it at once.</summary>
    public int StepMilliseconds { get; init; }
    /// <summary>The stack's log (level 0 debug .. 4 critical, text), from this level up. Called
    /// from the terminal's thread and from a pool's parsing thread.</summary>
    public Action<int, string>? Log { get; init; }
    public int LogLevel { get; init; } = 2;
}

/// <summary>A key or button: pressed, still held (about every 200 ms), released, or aborted (the
/// press ended without a release). A press is always followed by a release or an abort.</summary>
public enum KeyState { Released = 0, Pressed = 1, Held = 2, Aborted = 3 }

/// <summary>What entering a value did.</summary>
public enum InputResult
{
    /// <summary>Not an enabled input field on the current screen.</summary>
    NotAnInput = 0,
    /// <summary>Entered, and sent to the implement.</summary>
    Entered = 1,
    /// <summary>Not a valid value for that field: it is unchanged and stays open.</summary>
    Invalid = -1,
}

/// <summary>An implement's pool, as the terminal has it.</summary>
public enum PoolState { Waiting, Transferring, Parsing, Ready, Failed }

/// <summary>One connected implement (working set).</summary>
public sealed record WorkingSetInfo(ulong Name, int Address, PoolState State, int Progress, int? Objects, bool? Selectable);

/// <summary>An implement showing an alarm mask. Priority 0 high, 1 medium, 2 low; acoustic signal
/// 0 highest, 1 medium, 2 lowest, 3 none.</summary>
public sealed record AlarmInfo(ulong Name, int Address, ushort Mask, int Priority, int Signal);

/// <summary>Control Audio Signal from an implement: sound the signal this many times (0: stop
/// the one sounding), at this frequency, on and off for these many milliseconds.</summary>
public sealed record AudioSignal(ulong Name, int Activations, int FrequencyHz, int OnMilliseconds, int OffMilliseconds);

/// <summary>A picture the screens refer to by id, as RGBA (4 bytes a pixel, rows top first).</summary>
public sealed record VtImage(uint Id, int Width, int Height, byte[] Rgba);

static class Messages
{
    public static ulong Name(JsonElement e) => ulong.Parse(e.GetString()!, System.Globalization.NumberStyles.HexNumber);

    public static (ulong? Active, List<WorkingSetInfo> List) WorkingSets(JsonElement root)
    {
        var active = root.GetProperty("active");
        var list = new List<WorkingSetInfo>();
        foreach (var w in root.GetProperty("list").EnumerateArray())
        {
            var state = w.GetProperty("state").GetString() switch
            {
                "transferring" => PoolState.Transferring,
                "parsing" => PoolState.Parsing,
                "ready" => PoolState.Ready,
                "failed" => PoolState.Failed,
                _ => PoolState.Waiting,
            };
            list.Add(new WorkingSetInfo(Name(w.GetProperty("name")), w.GetProperty("address").GetInt32(), state,
                w.GetProperty("progress").GetInt32(),
                w.TryGetProperty("objects", out var o) ? o.GetInt32() : null,
                w.TryGetProperty("selectable", out var s) ? s.GetBoolean() : null));
        }
        return (active.ValueKind == JsonValueKind.Null ? null : Name(active), list);
    }

    public static List<AlarmInfo> Alarms(JsonElement root)
    {
        var list = new List<AlarmInfo>();
        foreach (var a in root.GetProperty("list").EnumerateArray())
            list.Add(new AlarmInfo(Name(a.GetProperty("ws")), a.GetProperty("address").GetInt32(), (ushort)a.GetProperty("mask").GetInt32(),
                a.GetProperty("priority").GetInt32(), a.GetProperty("signal").GetInt32()));
        return list;
    }

    public static AudioSignal Audio(JsonElement root) => new(Name(root.GetProperty("ws")), root.GetProperty("activations").GetInt32(),
        root.GetProperty("frequency").GetInt32(), root.GetProperty("on").GetInt32(), root.GetProperty("off").GetInt32());
}
