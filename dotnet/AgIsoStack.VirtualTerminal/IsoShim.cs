// The native interface (native/isoshim.h) as P/Invoke declarations: what VirtualTerminal is built
// on, and what the self-test and the bench drive directly. Callbacks are static
// [UnmanagedCallersOnly] methods (required under iOS AOT). Everything but isoshim_receive and
// isoshim_set_log belongs to the thread that calls isoshim_step.
using System.Runtime.InteropServices;

namespace AgIsoStack.Interop;

public static unsafe class IsoShim
{
#if __IOS__
    const string Lib = "__Internal";
#else
    const string Lib = "isoshim";
#endif

    public const int MaxTerminals = 4;
    public const int ServerAddress = 1, ClientAddress = 2, PoolOk = 3, PoolBad = 4, Mask = 5, ParseStart = 7, WsShutdown = 8, Active = 9;
    public const int Released = 0, Pressed = 1, Held = 2, Aborted = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct Host
    {
        public delegate* unmanaged<int, uint, byte*, int, int, void> Send;
        public delegate* unmanaged<int, int, int, int, void> Event;
        public delegate* unmanaged<int, byte*, int, void> Message;
        public delegate* unmanaged<int, uint, int, int, byte*, void> Image;
        public delegate* unmanaged<ulong, byte*, int, byte*, int, int, int> Save;
        public delegate* unmanaged<ulong, byte*, int, int> Load;
        public delegate* unmanaged<ulong, int, void> List;
        public delegate* unmanaged<ulong, byte*, int, int> Remove;
    }

    // One terminal. Zero for a field takes the native default.
    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public uint Identity;
        public ushort Manufacturer, MaskWidth, MaskHeight;
        public byte FunctionInstance, PreferredAddress, KeyWidth, KeyHeight, PhysicalKeys, VirtualKeys, NavigationKeys,
                    Version, SmallFonts, LargeFonts, PacketsPerCts, Reserved0, Reserved1, Reserved2;
    }

    [DllImport(Lib)] public static extern int isoshim_version();
    [DllImport(Lib)] public static extern int isoshim_vt_start(Config* configs, int count, Host* host);
    [DllImport(Lib)] public static extern int isoshim_test_implement(int terminal, byte* pool, int poolLen);
    [DllImport(Lib)] public static extern int isoshim_test_masks(int terminal, ushort* ids, int max);
    [DllImport(Lib)] public static extern int isoshim_test_show(int implement, ushort mask);
    [DllImport(Lib)] public static extern int isoshim_test_send(int implement, byte* data, int len, int global);

    /// <summary>Test implement 0 sends these 8 bytes to its terminal.</summary>
    public static int TestSend(params byte[] data) => TestSend(0, false, data);

    public static int TestSend(int implement, bool global, params byte[] data)
    {
        fixed (byte* p = data) return isoshim_test_send(implement, p, data.Length, global ? 1 : 0);
    }
    [DllImport(Lib)] public static extern void isoshim_set_log(delegate* unmanaged<int, byte*, void> fn, int level);
    [DllImport(Lib)] public static extern void isoshim_receive(int channel, uint id, byte* data, int len, int extended);
    [DllImport(Lib)] public static extern void isoshim_step();
    [DllImport(Lib)] public static extern void isoshim_stop();
    [DllImport(Lib)] public static extern void isoshim_reply(byte* data, int len);
    [DllImport(Lib)] public static extern int isoshim_soft_key(int terminal, ushort objectId, int state);
    [DllImport(Lib)] public static extern int isoshim_button(int terminal, ushort objectId, int state);
    [DllImport(Lib)] public static extern int isoshim_activate(int terminal, ulong name);
    [DllImport(Lib)] public static extern int isoshim_select(int terminal, ushort objectId, int open);
    [DllImport(Lib)] public static extern int isoshim_set_number(int terminal, ushort objectId, uint value);
    [DllImport(Lib)] public static extern int isoshim_set_string(int terminal, ushort objectId, byte* utf8, int len);
    [DllImport(Lib)] public static extern int isoshim_escape(int terminal);
    [DllImport(Lib)] public static extern int isoshim_link_up();

    public static int SetString(int terminal, ushort objectId, string text)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        fixed (byte* p = utf8) return isoshim_set_string(terminal, objectId, p, utf8.Length);
    }
}
