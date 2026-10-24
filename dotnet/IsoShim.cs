// The native interface (native/isoshim.h) as P/Invoke declarations, shared by the self-test and the
// bench. Callbacks are static [UnmanagedCallersOnly] methods (required under iOS AOT).
using System.Runtime.InteropServices;

public static unsafe class IsoShim
{
#if __IOS__
    const string Lib = "__Internal";
#else
    const string Lib = "isoshim";
#endif

    public const int ServerAddress = 1, ClientAddress = 2, PoolOk = 3, PoolBad = 4, Mask = 5, ParseStart = 7, WsShutdown = 8, Active = 9;
    public const int Released = 0, Pressed = 1, Held = 2, Aborted = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct Host
    {
        public delegate* unmanaged<int, uint, byte*, int, int, void> Send;
        public delegate* unmanaged<int, int, int, void> Event;
        public delegate* unmanaged<byte*, int, void> Message;
        public delegate* unmanaged<uint, int, int, byte*, void> Image;
        public delegate* unmanaged<ulong, byte*, int, byte*, int, int, int> Save;
        public delegate* unmanaged<ulong, byte*, int, int> Load;
        public delegate* unmanaged<ulong, int, void> List;
        public delegate* unmanaged<ulong, byte*, int, int> Remove;
    }

    // Zero for a field takes the native default.
    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public uint Identity;
        public ushort Manufacturer, MaskWidth, MaskHeight;
        public byte FunctionInstance, PreferredAddress, KeyWidth, KeyHeight, PhysicalKeys, VirtualKeys, NavigationKeys,
                    Version, SmallFonts, LargeFonts, PacketsPerCts, Reserved0, Reserved1, Reserved2;
    }

    [DllImport(Lib)] public static extern int isoshim_version();
    [DllImport(Lib)] public static extern int isoshim_vt_start(Config* config, Host* host);
    [DllImport(Lib)] public static extern int isoshim_test_implement(byte* pool, int poolLen);
    [DllImport(Lib)] public static extern int isoshim_test_masks(ushort* ids, int max);
    [DllImport(Lib)] public static extern int isoshim_test_show(ushort mask);
    [DllImport(Lib)] public static extern void isoshim_set_log(delegate* unmanaged<int, byte*, void> fn, int level);
    [DllImport(Lib)] public static extern void isoshim_receive(int channel, uint id, byte* data, int len, int extended);
    [DllImport(Lib)] public static extern void isoshim_step();
    [DllImport(Lib)] public static extern void isoshim_stop();
    [DllImport(Lib)] public static extern void isoshim_reply(byte* data, int len);
    [DllImport(Lib)] public static extern int isoshim_soft_key(ushort objectId, int state);
    [DllImport(Lib)] public static extern int isoshim_button(ushort objectId, int state);
    [DllImport(Lib)] public static extern int isoshim_activate(ulong name);
    [DllImport(Lib)] public static extern int isoshim_select(ushort objectId, int open);
    [DllImport(Lib)] public static extern int isoshim_set_number(ushort objectId, uint value);
    [DllImport(Lib)] public static extern int isoshim_set_string(ushort objectId, byte* utf8, int len);
    [DllImport(Lib)] public static extern int isoshim_escape();

    public static int SetString(ushort objectId, string text)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
        fixed (byte* p = utf8) return isoshim_set_string(objectId, p, utf8.Length);
    }
}
