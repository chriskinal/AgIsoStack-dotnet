// A CAN adapter on this device that speaks slcan (the Lawicel ASCII protocol: CANable's stock
// firmware and many serial adapters), reached through a serial port. It opens the adapter,
// starts the bus at the bitrate, carries every frame both ways, and opens it again on its own
// after an unplug.
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace AgIsoStack;

/// <summary>
/// A link to the bus through an slcan adapter. Frames from the bus come through
/// <see cref="FrameReceived"/> on the link's own thread, as do the other events; <see cref="Send"/>
/// may be called from any thread. For a <see cref="VirtualTerminalStack"/>,
/// <see cref="VirtualTerminalLink.Attach(VirtualTerminalStack, SlcanLink)"/> wires the two.
/// </summary>
/// <remarks>
/// slcan has no heartbeat and a quiet bus sends nothing, so the link is down only when the port
/// is: unplugged, or not there yet. Error frames and bus state are not reported (the protocol's
/// status flags are not implemented alike by every firmware).
/// </remarks>
public sealed class SlcanLink : IDisposable
{
    const int RetryMs = 1000, SettleMs = 50;

    readonly Func<ISerialPort> open;
    readonly int bitrate;
    readonly string portName;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly object outLock = new();
    readonly AutoResetEvent outReady = new(false);
    readonly byte[] bitrateCommand;
    byte[] pending = new byte[4096];
    int pendingLength;
    Thread? thread, writer;
    volatile ISerialPort? port;
    volatile bool stopping, up;
    long dropped;

    /// <summary>An adapter on the serial port of this name (<see cref="SerialPorts.List"/>).</summary>
    public SlcanLink(string portName, int bitrate = 250000) : this(() => SerialPorts.Get(portName), bitrate, portName) { }

    /// <summary>An adapter on any byte port: <paramref name="open"/> makes a new, unopened one
    /// each time the link (re)opens.</summary>
    public SlcanLink(Func<ISerialPort> open, int bitrate = 250000, string portName = "")
    {
        this.open = open ?? throw new ArgumentNullException(nameof(open));
        this.portName = portName;
        this.bitrate = bitrate;
        char code = bitrate switch
        {
            10000 => '0', 20000 => '1', 50000 => '2', 100000 => '3', 125000 => '4',
            250000 => '5', 500000 => '6', 800000 => '7', 1000000 => '8',
            _ => throw new ArgumentException("slcan bitrates: 10, 20, 50, 100, 125, 250, 500, 800 kbit/s or 1 Mbit/s", nameof(bitrate)),
        };
        bitrateCommand = Encoding.ASCII.GetBytes($"S{code}\r");
    }

    /// <summary>A frame from the bus (on the link's thread: hand it on and return). Channel 0;
    /// the time is this link's receive time in microseconds (wraps).</summary>
    public event LinkFrameHandler? FrameReceived;
    /// <summary>The adapter is open and on the bus: the port's name, and the adapter's version
    /// if it gave one.</summary>
    public event Action<string>? Connected;
    /// <summary>The adapter is gone, and why. It is opened again on its own.</summary>
    public event Action<LinkCloseReason, string>? Disconnected;
    /// <summary>A handler threw; the link carries on.</summary>
    public event Action<Exception>? HandlerFailed;

    public bool IsConnected => up;
    public int Bitrate => bitrate;
    /// <summary>Frames given to <see cref="Send"/> while the link was down, dropped.</summary>
    public long FramesDropped => Interlocked.Read(ref dropped);
    /// <summary>Why the last attempt to open the adapter failed ("" after a success).</summary>
    public string LastError { get; private set; } = "";

    public void Start()
    {
        if (thread != null) return;
        stopping = false;
        thread = new Thread(Run) { IsBackground = true, Name = "SlcanLink" };
        thread.Start();
    }

    /// <summary>Takes the adapter off the bus, closes the port and ends the link's thread.</summary>
    public void Stop()
    {
        var t = thread;
        if (t == null) return;
        stopping = true;
        outReady.Set();
        if (Thread.CurrentThread != t) t.Join();
        thread = null;
    }

    public void Dispose() => Stop();

    /// <summary>Sends a frame to the bus. Dropped while the link is down (a frame is never held
    /// for the next link). Any thread; returns at once.</summary>
    public void Send(int channel, uint id, ReadOnlySpan<byte> data, bool extended)
    {
        if (data.Length > 8) throw new ArgumentException("A CAN frame holds at most 8 bytes.", nameof(data));
        lock (outLock)
        {
            if (!up)
            {
                dropped++;
                return;
            }
            // T + 8 hex id (t + 3 for a standard frame), the length, 2 hex a byte, CR.
            int idDigits = extended ? 8 : 3;
            Span<byte> m = Reserve(1 + idDigits + 1 + data.Length * 2 + 1);
            m[0] = (byte)(extended ? 'T' : 't');
            uint shown = extended ? id & 0x1FFFFFFF : id & 0x7FF;
            for (int i = 0; i < idDigits; i++) m[idDigits - i] = Hex((int)(shown >> (4 * i)) & 0xF);
            m[1 + idDigits] = (byte)('0' + data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                m[2 + idDigits + 2 * i] = Hex(data[i] >> 4);
                m[3 + idDigits + 2 * i] = Hex(data[i] & 0xF);
            }
            m[^1] = (byte)'\r';
        }
        outReady.Set();
    }

    static byte Hex(int v) => (byte)(v < 10 ? '0' + v : 'A' + v - 10);

    // Under outLock: room for n more bytes in the outgoing buffer.
    Span<byte> Reserve(int n)
    {
        if (pendingLength + n > pending.Length) Array.Resize(ref pending, Math.Max(pending.Length * 2, pendingLength + n));
        var span = pending.AsSpan(pendingLength, n);
        pendingLength += n;
        return span;
    }

    void Raise(Action handler)
    {
        try { handler(); }
        catch (Exception e)
        {
            try { HandlerFailed?.Invoke(e); } catch { }
        }
    }

    void Run()
    {
        while (!stopping)
        {
            Session();
            for (var end = clock.ElapsedMilliseconds + RetryMs; !stopping && clock.ElapsedMilliseconds < end;) Thread.Sleep(10);
        }
    }

    // One opening of the adapter, from open to gone.
    void Session()
    {
        ISerialPort p;
        try
        {
            p = open();
            p.Open();
        }
        catch (Exception e)
        {
            LastError = e.Message; // not there (yet), or no permission yet: tried again
            return;
        }
        port = p;
        var rx = new LineReader(p);
        (LinkCloseReason Reason, string Detail) result = (LinkCloseReason.ShuttingDown, "stopped");
        bool wasUp = false;
        try
        {
            // Close whatever a last session left open (a reply of BEL is fine), ask the version,
            // set the bitrate, open. Firmwares differ in what they answer, so replies are not
            // required; what comes back before the bus opens is read and dropped, except the version.
            Command(p, "\r\r\r");
            Command(p, "C\r");
            Command(p, "V\r");
            string version = rx.Drain(SettleMs * 2);
            p.Write(bitrateCommand, 0, bitrateCommand.Length);
            Command(p, "O\r");
            rx.Drain(SettleMs);
            LastError = "";
            lock (outLock)
            {
                pendingLength = 0;
                up = true;
            }
            wasUp = true;
            writer = new Thread(() => Write(p)) { IsBackground = true, Name = "SlcanLink-tx" };
            writer.Start();
            string info = version.Length > 0 ? $"{portName} ({version})" : portName;
            Raise(() => Connected?.Invoke(info));
            var data = new byte[8];
            while (!stopping)
            {
                var line = rx.Next();
                if (line.IsEmpty) continue;
                if (!Parse(line, data, out uint id, out bool extended, out int length)) continue;
                var handler = FrameReceived;
                if (handler == null) continue;
                try { handler(0, id, data.AsSpan(0, length), extended, (uint)(clock.Elapsed.Ticks / (TimeSpan.TicksPerMillisecond / 1000))); }
                catch (Exception e) { Raise(() => HandlerFailed?.Invoke(e)); }
            }
            // Off the bus before the port closes.
            try { Command(p, "C\r"); } catch { }
        }
        catch (Exception e)
        {
            result = (LinkCloseReason.Lost, e.Message);
            LastError = e.Message;
        }
        lock (outLock)
        {
            up = false;
            pendingLength = 0;
        }
        outReady.Set();
        writer?.Join();
        writer = null;
        port = null;
        try { p.Dispose(); } catch { }
        if (stopping) result = (LinkCloseReason.ShuttingDown, "stopped");
        if (wasUp) Raise(() => Disconnected?.Invoke(result.Reason, result.Detail));
    }

    static void Command(ISerialPort p, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        p.Write(b, 0, b.Length);
    }

    // Sends what is waiting in one write, until the link is down.
    void Write(ISerialPort p)
    {
        var buffer = new byte[4096];
        while (up && !stopping)
        {
            outReady.WaitOne(100);
            int n;
            lock (outLock)
            {
                if (!up) return;
                n = pendingLength;
                if (n == 0) continue;
                if (n > buffer.Length) buffer = new byte[Math.Max(n, buffer.Length * 2)];
                pending.AsSpan(0, n).CopyTo(buffer);
                pendingLength = 0;
            }
            try { p.Write(buffer, 0, n); }
            catch (Exception)
            {
                // The reader sees the port gone and ends the link.
                return;
            }
        }
    }

    /// <summary>One line from the adapter: <c>Tiiiiiiiildd..</c> (extended) or <c>tiiildd..</c>
    /// (standard), possibly with a 4-digit timestamp after the data. Anything else (replies,
    /// remote frames, status) is not a frame. The data goes into <paramref name="data"/> (8 bytes).</summary>
    internal static bool Parse(ReadOnlySpan<byte> line, Span<byte> data, out uint id, out bool extended, out int length)
    {
        id = 0;
        extended = false;
        length = 0;
        if (line.Length < 1 || (line[0] != 'T' && line[0] != 't')) return false;
        extended = line[0] == 'T';
        int idDigits = extended ? 8 : 3;
        if (line.Length < 2 + idDigits) return false;
        for (int i = 1; i <= idDigits; i++)
        {
            int v = HexValue(line[i]);
            if (v < 0) return false;
            id = (id << 4) | (uint)v;
        }
        int dlc = line[1 + idDigits] - '0';
        if (dlc < 0 || dlc > 8 || line.Length < 2 + idDigits + 2 * dlc) return false;
        for (int i = 0; i < dlc; i++)
        {
            int hi = HexValue(line[2 + idDigits + 2 * i]), lo = HexValue(line[3 + idDigits + 2 * i]);
            if (hi < 0 || lo < 0) return false;
            data[i] = (byte)(hi << 4 | lo);
        }
        if (extended ? id > 0x1FFFFFFF : id > 0x7FF) return false;
        length = dlc;
        return true;
    }

    static int HexValue(byte c) =>
        c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;

    // Reads lines off the port: CR (or LF) ends one; BEL (an error reply) is a line of its own.
    sealed class LineReader
    {
        readonly ISerialPort p;
        readonly byte[] buf = new byte[4096];
        readonly byte[] line = new byte[64];
        int start, end, length;

        public LineReader(ISerialPort p) => this.p = p;

        /// <summary>The next whole line (without its CR), or empty when none came in about 100
        /// ms. Valid until the next call.</summary>
        public ReadOnlySpan<byte> Next()
        {
            while (true)
            {
                while (start < end)
                {
                    byte c = buf[start++];
                    if (c == '\r' || c == '\n' || c == 7)
                    {
                        int n = length;
                        length = 0;
                        if (n > 0) return line.AsSpan(0, n);
                        continue;
                    }
                    if (length < line.Length) line[length++] = c;
                }
                start = end = 0;
                end = p.Read(buf, 0, buf.Length);
                if (end == 0) return default;
            }
        }

        /// <summary>Reads for this long, dropping the lines, and returns the version reply
        /// (<c>Vhhss</c>) if one came.</summary>
        public string Drain(int ms)
        {
            string version = "";
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                var l = Next();
                if (l.Length >= 2 && l[0] == 'V') version = Encoding.ASCII.GetString(l);
            }
            return version;
        }
    }
}
