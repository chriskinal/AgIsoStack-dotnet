// The client end of the gateway protocol (docs/TRANSPORT.md section 5, "AOW link" version 1): one
// TCP connection to a gateway on the bus, every CAN frame both ways. It connects, says hello,
// keeps a heartbeat, drops the link after 1 s of silence and connects again on its own.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AgIsoStack;

public enum LinkRole : byte
{
    /// <summary>Sends and receives frames; one per gateway.</summary>
    Control = 0,
    /// <summary>Receives only (a logger); a gateway may refuse it.</summary>
    Monitor = 1,
}

public enum BusState : byte { ErrorActive = 0, ErrorWarning = 1, ErrorPassive = 2, BusOff = 3, Down = 4 }

/// <summary>Why a link ended: the gateway's CLOSE, or what this end saw.</summary>
public enum LinkCloseReason : byte
{
    /// <summary>The connection failed or was closed without a reason.</summary>
    Lost = 0,
    Version = 1,
    /// <summary>The gateway already has a control client.</summary>
    Busy = 2,
    /// <summary>Nothing heard for 1 s.</summary>
    Timeout = 3,
    ProtocolError = 4,
    ShuttingDown = 5,
}

public sealed record GatewayChannel(int Channel, int Bitrate, bool ListenOnly, string Name);

/// <summary>The gateway, from its HELLO.</summary>
public sealed record GatewayInfo(string Name, IReadOnlyList<GatewayChannel> Channels);

/// <summary>One bus as the gateway reports it. The drop counts are from the start of the link.</summary>
public sealed record BusStatus(int Channel, BusState State, int TxErrors, int RxErrors, long RxDropped, long TxDropped);

/// <summary>A frame from the bus: the gateway's channel, the id, the data, and the gateway's receive
/// time in microseconds on its own clock (wraps; for spacing, not for comparing with ours).</summary>
public delegate void LinkFrameHandler(int channel, uint id, ReadOnlySpan<byte> data, bool extended, uint timeUs);

/// <summary>
/// A link to a CAN gateway. Frames from the bus come through <see cref="FrameReceived"/> on the
/// link's own thread, as do the other events; <see cref="Send"/> may be called from any thread.
/// For a <see cref="VirtualTerminalStack"/>, <see cref="VirtualTerminalLink.Attach"/> wires the two.
/// </summary>
public sealed class AowLink : IDisposable
{
    public const int DefaultPort = 2323;
    public const byte Version = 1;
    // ConnectMs: a connection attempt made during a cut waits no longer than this once it is over.
    const int HeartbeatMs = 100, SilenceMs = 1000, HelloMs = 2000, RetryMs = 250, VersionRetryMs = 5000, ConnectMs = 500;
    const byte Hello = 1, Frame = 2, Heartbeat = 3, Status = 4, Close = 5;
    static readonly byte[] Magic = "AOWL"u8.ToArray();

    readonly string host, name;
    readonly int port;
    readonly LinkRole role;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly object outLock = new();
    readonly AutoResetEvent outReady = new(false);
    byte[] pending = new byte[4096];
    int pendingLength;
    Thread? thread;
    volatile Socket? socket;
    volatile bool stopping, up;
    long dropped;
    // The last HEARTBEAT heard: its time, and when (our clock), for the echo in ours.
    uint heardTime;
    long heardAtMs = -1;

    public AowLink(string host, int port = DefaultPort, string name = "", LinkRole role = LinkRole.Control)
    {
        this.host = host;
        this.port = port;
        this.name = name;
        this.role = role;
    }

    /// <summary>A frame from the bus (on the link's thread: hand it on and return).</summary>
    public event LinkFrameHandler? FrameReceived;
    /// <summary>The link is up: the two HELLOs are exchanged.</summary>
    public event Action<GatewayInfo>? Connected;
    /// <summary>The link is down, and why. It is tried again on its own.</summary>
    public event Action<LinkCloseReason, string>? Disconnected;
    /// <summary>A bus's state, on connecting, on any change and every second.</summary>
    public event Action<BusStatus>? StatusReceived;
    /// <summary>A round trip in milliseconds, from each heartbeat.</summary>
    public event Action<double>? RoundTrip;
    /// <summary>A handler threw; the link carries on.</summary>
    public event Action<Exception>? HandlerFailed;

    public bool IsConnected => up;
    /// <summary>The gateway of the current link, or of the last one.</summary>
    public GatewayInfo? Gateway { get; private set; }
    /// <summary>Frames given to <see cref="Send"/> while the link was down, dropped.</summary>
    public long FramesDropped => Interlocked.Read(ref dropped);

    public void Start()
    {
        if (thread != null) return;
        stopping = false;
        thread = new Thread(Run) { IsBackground = true, Name = "AowLink" };
        thread.Start();
    }

    /// <summary>Closes the link and ends its thread.</summary>
    public void Stop()
    {
        var t = thread;
        if (t == null) return;
        stopping = true;
        Abort(socket);
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
            if (!up || role != LinkRole.Control)
            {
                dropped++;
                return;
            }
            Span<byte> m = Reserve(12 + data.Length);
            m[0] = Frame;
            m[1] = (byte)(10 + data.Length);
            m[2] = (byte)channel;
            m[3] = (byte)(extended ? 1 : 0);
            BitConverter.TryWriteBytes(m.Slice(4, 4), id);
            m.Slice(8, 4).Clear();
            data.CopyTo(m.Slice(12));
        }
        outReady.Set();
    }

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

    static void Abort(Socket? s)
    {
        if (s == null) return;
        // Linger 0: the kernel drops what is unsent instead of delivering it late.
        try { s.LingerState = new LingerOption(true, 0); } catch { }
        try { s.Close(); } catch { }
    }

    void Run()
    {
        while (!stopping)
        {
            var (reason, detail) = Connection();
            if (stopping) break;
            int wait = reason == LinkCloseReason.Version ? VersionRetryMs : RetryMs;
            for (var end = clock.ElapsedMilliseconds + wait; !stopping && clock.ElapsedMilliseconds < end;) Thread.Sleep(10);
        }
    }

    // One connection, from connecting to closed. Returns why it ended.
    (LinkCloseReason, string) Connection()
    {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        socket = s;
        (LinkCloseReason Reason, string Detail) result;
        try { result = Session(s); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
        {
            result = (LinkCloseReason.Timeout, up ? "nothing heard for 1 s" : "no HELLO from the gateway");
        }
        catch (OperationCanceledException) { result = (LinkCloseReason.Lost, "connect timed out"); }
        catch (Exception e) { result = (LinkCloseReason.Lost, e.Message); }
        bool wasUp;
        lock (outLock)
        {
            wasUp = up;
            up = false;
            pendingLength = 0;
        }
        Abort(s);
        outReady.Set();
        writer?.Join();
        writer = null;
        socket = null;
        if (stopping) result = (LinkCloseReason.ShuttingDown, "stopped");
        if (wasUp) Raise(() => Disconnected?.Invoke(result.Reason, result.Detail));
        return result;
    }

    Thread? writer;

    (LinkCloseReason, string) Session(Socket s)
    {
        using (var cts = new CancellationTokenSource(ConnectMs))
            s.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
        s.Send(HelloMessage());
        s.ReceiveTimeout = HelloMs;
        var rx = new Receiver(s);
        byte type = rx.Next(out var body);
        if (type == Close) return Closed(body);
        if (type != Hello) return (LinkCloseReason.ProtocolError, $"expected HELLO, got {type}");
        var gateway = ParseHello(body, out var why);
        if (gateway == null) return (why.StartsWith("version") ? LinkCloseReason.Version : LinkCloseReason.ProtocolError, why);
        Gateway = gateway;
        heardAtMs = -1;
        lock (outLock)
        {
            pendingLength = 0;
            up = true;
        }
        writer = new Thread(() => Write(s)) { IsBackground = true, Name = "AowLink-tx" };
        writer.Start();
        Raise(() => Connected?.Invoke(gateway));
        s.ReceiveTimeout = SilenceMs;
        while (!stopping)
        {
            type = rx.Next(out body);
            switch (type)
            {
                case Frame:
                    if (body.Length < 10) return (LinkCloseReason.ProtocolError, "short FRAME");
                    var handler = FrameReceived;
                    if (handler != null)
                    {
                        try
                        {
                            handler(body[0], BitConverter.ToUInt32(body.Slice(2, 4)), body.Slice(10), (body[1] & 1) != 0,
                                BitConverter.ToUInt32(body.Slice(6, 4)));
                        }
                        catch (Exception e) { Raise(() => HandlerFailed?.Invoke(e)); }
                    }
                    break;
                case Heartbeat:
                    if (body.Length < 10) return (LinkCloseReason.ProtocolError, "short HEARTBEAT");
                    long now = clock.ElapsedMilliseconds;
                    heardTime = BitConverter.ToUInt32(body.Slice(0, 4));
                    heardAtMs = now;
                    uint echo = BitConverter.ToUInt32(body.Slice(4, 4));
                    ushort held = BitConverter.ToUInt16(body.Slice(8, 2));
                    if (echo != 0)
                    {
                        double rtt = (uint)((uint)now - echo) - (double)held;
                        Raise(() => RoundTrip?.Invoke(rtt));
                    }
                    break;
                case Status:
                    if (body.Length < 12) return (LinkCloseReason.ProtocolError, "short STATUS");
                    var status = new BusStatus(body[0], (BusState)body[1], body[2], body[3],
                        BitConverter.ToUInt32(body.Slice(4, 4)), BitConverter.ToUInt32(body.Slice(8, 4)));
                    Raise(() => StatusReceived?.Invoke(status));
                    break;
                case Close:
                    return Closed(body);
                case Hello:
                    return (LinkCloseReason.ProtocolError, "a second HELLO");
                // Anything else is a newer message: skipped.
            }
        }
        return (LinkCloseReason.ShuttingDown, "stopped");
    }

    static (LinkCloseReason, string) Closed(ReadOnlySpan<byte> body)
    {
        var reason = body.Length > 0 ? (LinkCloseReason)body[0] : LinkCloseReason.Lost;
        string text = body.Length > 1 ? Str(body.Slice(1), out _) : "";
        return (reason, text);
    }

    // Sends what is waiting in one write, and a heartbeat every 100 ms, until the link is down.
    void Write(Socket s)
    {
        long next = 0;
        var buffer = new byte[4096];
        while (up && !stopping)
        {
            long now = clock.ElapsedMilliseconds;
            if (now < next) outReady.WaitOne((int)(next - now));
            now = clock.ElapsedMilliseconds;
            int n;
            lock (outLock)
            {
                if (!up) return;
                if (now >= next)
                {
                    next = now + HeartbeatMs;
                    Span<byte> m = Reserve(12);
                    m[0] = Heartbeat;
                    m[1] = 10;
                    // Our time is never 0, so an echo of 0 means "none heard yet".
                    BitConverter.TryWriteBytes(m.Slice(2, 4), (uint)Math.Max(1, now));
                    long at = heardAtMs;
                    BitConverter.TryWriteBytes(m.Slice(6, 4), at < 0 ? 0u : heardTime);
                    BitConverter.TryWriteBytes(m.Slice(10, 2), (ushort)(at < 0 ? 0 : Math.Min(ushort.MaxValue, now - at)));
                }
                n = pendingLength;
                if (n > buffer.Length) buffer = new byte[Math.Max(n, buffer.Length * 2)];
                pending.AsSpan(0, n).CopyTo(buffer);
                pendingLength = 0;
            }
            try
            {
                for (int sent = 0; sent < n;) sent += s.Send(buffer, sent, n - sent, SocketFlags.None);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                Abort(s); // the reader sees it and ends the link
                return;
            }
        }
    }

    byte[] HelloMessage()
    {
        byte[] n = Encoding.UTF8.GetBytes(name);
        if (n.Length > 200) Array.Resize(ref n, 200);
        var m = new byte[2 + 4 + 1 + 1 + 1 + n.Length];
        m[0] = Hello;
        m[1] = (byte)(m.Length - 2);
        Magic.CopyTo(m, 2);
        m[6] = Version;
        m[7] = (byte)role;
        m[8] = (byte)n.Length;
        n.CopyTo(m, 9);
        return m;
    }

    static GatewayInfo? ParseHello(ReadOnlySpan<byte> b, out string why)
    {
        why = "";
        if (b.Length < 8 || !b.Slice(0, 4).SequenceEqual(Magic)) { why = "not a AOW link gateway"; return null; }
        if (b[4] != Version) { why = $"version {b[4]}, not {Version}"; return null; }
        try
        {
            int at = 6;
            string gatewayName = Str(b.Slice(at), out int used);
            at += used;
            int count = b[at++];
            var channels = new List<GatewayChannel>();
            for (int i = 0; i < count; i++)
            {
                int bitrate = BitConverter.ToInt32(b.Slice(at, 4));
                bool listenOnly = (b[at + 4] & 1) != 0;
                at += 5;
                string channelName = Str(b.Slice(at), out used);
                at += used;
                channels.Add(new GatewayChannel(i, bitrate, listenOnly, channelName));
            }
            return new GatewayInfo(gatewayName, channels);
        }
        catch (ArgumentOutOfRangeException) { why = "short HELLO"; return null; }
        catch (IndexOutOfRangeException) { why = "short HELLO"; return null; }
    }

    static string Str(ReadOnlySpan<byte> b, out int used)
    {
        int n = b[0];
        used = 1 + n;
        return Encoding.UTF8.GetString(b.Slice(1, n));
    }

    // Reads whole messages off the socket.
    sealed class Receiver
    {
        readonly Socket s;
        readonly byte[] buf = new byte[65536];
        int start, end;

        public Receiver(Socket s) => this.s = s;

        /// <summary>The next message's type; its body is valid until the next call.</summary>
        public byte Next(out ReadOnlySpan<byte> body)
        {
            while (true)
            {
                if (end - start >= 2 && end - start >= 2 + buf[start + 1])
                {
                    byte type = buf[start];
                    int len = buf[start + 1];
                    body = new ReadOnlySpan<byte>(buf, start + 2, len);
                    start += 2 + len;
                    return type;
                }
                if (start > 0)
                {
                    Buffer.BlockCopy(buf, start, buf, 0, end - start);
                    end -= start;
                    start = 0;
                }
                int n = s.Receive(buf, end, buf.Length - end, SocketFlags.None);
                if (n == 0) throw new SocketException((int)SocketError.ConnectionReset);
                end += n;
            }
        }
    }
}

/// <summary>A terminal on a gateway link.</summary>
public static class VirtualTerminalLink
{
    /// <summary>Wires the terminals to a link: frames from the bus to the stack, and the link's
    /// state to <see cref="VirtualTerminalStack.LinkDown"/> and <see cref="VirtualTerminalStack.LinkUp"/>
    /// (so a key held when the link went is released when it comes back). The terminals' frames go
    /// out through the options: <c>Send = link.Send</c>.</summary>
    public static void Attach(this VirtualTerminalStack stack, AowLink link)
    {
        link.FrameReceived += (channel, id, data, extended, _) => stack.Receive(id, data, extended, channel);
        link.Connected += _ => stack.LinkUp();
        link.Disconnected += (_, _) => stack.LinkDown();
    }
}
