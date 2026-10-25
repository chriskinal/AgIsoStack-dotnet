// One ISOBUS virtual terminal of a VirtualTerminalStack (docs/API.md): what one display in the cab
// shows, and the operator's input on it.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using static AgIsoStack.Interop.IsoShim;

namespace AgIsoStack;

/// <summary>
/// One terminal: its screens and editors as JSON through <see cref="Message"/> (for the page to
/// paint, unchanged), the rest as typed events, and the operator's input as calls. Made by
/// <see cref="VirtualTerminalStack"/>; its implements are the ones that chose it.
/// </summary>
/// <remarks>
/// Every event comes on the stack's thread, in order (see <see cref="VirtualTerminalStack"/>).
/// The calls can come from any thread (from a handler too); each returns when the stack has done it.
/// </remarks>
public sealed unsafe class VirtualTerminal
{
    internal VirtualTerminal(VirtualTerminalStack stack, int index)
    {
        Stack = stack;
        Index = index;
    }

    public VirtualTerminalStack Stack { get; }
    /// <summary>Its position in <see cref="VirtualTerminalStack.Terminals"/>, from 0; its function
    /// instance is <see cref="VirtualTerminalOptions.FunctionInstance"/> plus this.</summary>
    public int Index { get; }

    // ---- events (on the stack's thread) ----

    /// <summary>Every message, as JSON: <c>screen</c> and <c>edit</c> for the page, and
    /// <c>workingSets</c>, <c>alarms</c>, <c>audio</c>, <c>volume</c>, <c>identify</c> (also below
    /// as types).</summary>
    public event Action<string>? Message;
    /// <summary>A picture the screens refer to, sent before the first screen that uses it. Ids are
    /// unique across the stack's terminals.</summary>
    public event Action<VtImage>? Image;
    /// <summary>The implements connected and the one shown (null: none).</summary>
    public event Action<ulong?, IReadOnlyList<WorkingSetInfo>>? WorkingSetsChanged;
    /// <summary>Every implement showing an alarm mask, shown or not.</summary>
    public event Action<IReadOnlyList<AlarmInfo>>? AlarmsChanged;
    public event Action<AudioSignal>? Audio;
    /// <summary>Set Audio Volume from an implement: its NAME and the percentage.</summary>
    public event Action<ulong, int>? Volume;
    /// <summary>An implement asked every terminal to show its number (Identify VT): show this
    /// one's for about 3 s, so the operator can pick a terminal in the implement's settings.</summary>
    public event Action? Identify;
    /// <summary>The terminal has its address on the bus.</summary>
    public event Action<int>? AddressClaimed;
    /// <summary>An implement's pool is up: its address, the number of objects.</summary>
    public event Action<int, int>? PoolLoaded;
    /// <summary>An implement's pool was refused: its address, the object at fault.</summary>
    public event Action<int, int>? PoolRejected;
    /// <summary>An implement went away (no maintenance messages): its address, the reason code.</summary>
    public event Action<int, int>? ImplementLost;

    /// <summary>The terminal's address, or -1 before it has claimed one.</summary>
    public int Address { get; internal set; } = -1;
    public bool IsRunning => Stack.IsRunning;

    internal bool HasImageListener => Image != null;
    internal bool HasWorkingSetsListener => WorkingSetsChanged != null;
    internal bool HasAlarmsListener => AlarmsChanged != null;
    internal bool HasAudioListener => Audio != null;
    internal bool HasVolumeListener => Volume != null;
    internal void RaiseMessage(string json) => Message?.Invoke(json);
    internal void RaiseImage(VtImage image) => Image?.Invoke(image);
    internal void RaiseWorkingSetsChanged(ulong? active, IReadOnlyList<WorkingSetInfo> list) => WorkingSetsChanged?.Invoke(active, list);
    internal void RaiseAlarmsChanged(IReadOnlyList<AlarmInfo> list) => AlarmsChanged?.Invoke(list);
    internal void RaiseAudio(AudioSignal signal) => Audio?.Invoke(signal);
    internal void RaiseVolume(ulong name, int percent) => Volume?.Invoke(name, percent);
    internal void RaiseIdentify() => Identify?.Invoke();
    internal void RaiseAddressClaimed(int address) => AddressClaimed?.Invoke(address);
    internal void RaisePoolLoaded(int address, int objects) => PoolLoaded?.Invoke(address, objects);
    internal void RaisePoolRejected(int address, int objectId) => PoolRejected?.Invoke(address, objectId);
    internal void RaiseImplementLost(int address, int reason) => ImplementLost?.Invoke(address, reason);

    // ---- the operator ----

    /// <summary>A soft key (Key object) of the shown implement. True: sent.</summary>
    public Task<bool> SoftKeyAsync(ushort objectId, KeyState state) => InvokeAsync(() => Stack.Key(Index, false, objectId, state));

    /// <summary>A button on the shown mask. True: sent.</summary>
    public Task<bool> ButtonAsync(ushort objectId, KeyState state) => InvokeAsync(() => Stack.Key(Index, true, objectId, state));

    /// <summary>Shows this implement (one connected to this terminal), by its NAME. True: it is
    /// now the one shown.</summary>
    public Task<bool> ActivateAsync(ulong name) => InvokeAsync(() => isoshim_activate(Index, name) == 1);

    /// <summary>Selects an input field or a button on the screen (open: an editor is up for it;
    /// an edit message follows). True: done.</summary>
    public Task<bool> SelectAsync(ushort objectId, bool open) => InvokeAsync(() => isoshim_select(Index, objectId, open ? 1 : 0) == 1);

    /// <summary>Selects nothing.</summary>
    public Task<bool> DeselectAsync() => InvokeAsync(() => isoshim_select(Index, 0xFFFF, 0) == 1);

    /// <summary>A value for an input number (raw: before offset and scale), boolean (0 or 1) or
    /// list (the index).</summary>
    public Task<InputResult> SetNumberAsync(ushort objectId, uint value) => InvokeAsync(() => (InputResult)isoshim_set_number(Index, objectId, value));

    /// <summary>Text for an input string.</summary>
    public Task<InputResult> SetStringAsync(ushort objectId, string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return InvokeAsync(() =>
        {
            fixed (byte* p = utf8) return (InputResult)isoshim_set_string(Index, objectId, p, utf8.Length);
        });
    }

    /// <summary>Abandons the open field. True: one was open.</summary>
    public Task<bool> EscapeAsync() => InvokeAsync(() => isoshim_escape(Index) == 1);

    /// <summary>Runs code on the stack's thread (<see cref="VirtualTerminalStack.InvokeAsync"/>).</summary>
    public Task<T> InvokeAsync<T>(Func<T> call) => Stack.InvokeAsync(call);
}
