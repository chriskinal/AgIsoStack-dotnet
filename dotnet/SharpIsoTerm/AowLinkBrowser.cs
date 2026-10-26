// Finding AOW link gateways on the local network (docs/TRANSPORT.md, "Finding a gateway"): they
// announce themselves by DNS-SD as _aowlink._tcp. On the desktop and Android this asks with mDNS
// queries from an ordinary port, which responders answer by unicast (RFC 6762 section 6.7), so it
// needs neither port 5353 nor a multicast group. iOS lets an app send multicast only with an
// entitlement Apple grants on request, so there it uses the system's Bonjour browser (the app's
// Info.plist names _aowlink._tcp in NSBonjourServices and has NSLocalNetworkUsageDescription).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("IsoShimConsole")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("IsoShimAndroid")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("IsoShimIos")]

namespace SharpIsoTerm;

/// <summary>A gateway found on the network: its name (the DNS-SD instance, for the operator),
/// where to connect (an IPv4 address and port), its host name and its TXT record.</summary>
public sealed record AowLinkGateway(string Name, string Host, int Port, string HostName, IReadOnlyDictionary<string, string> Txt);

/// <summary>
/// Looks for AOW link gateways (<c>_aowlink._tcp</c>) until stopped: at once, then less and less
/// often (1, 2, 4 ... up to 30 s apart), and at once again on <see cref="Refresh"/>. A gateway
/// that stops answering is dropped after two rounds without it. <see cref="Changed"/> comes on
/// the browser's own thread (iOS: the main thread) with the whole list, whenever it changes.
/// </summary>
public sealed class AowLinkBrowser : IDisposable
{
    public const string ServiceType = "_aowlink._tcp";

    readonly object gate = new();
    List<AowLinkGateway> gateways = new();

    /// <summary>The gateways found, by name.</summary>
    public IReadOnlyList<AowLinkGateway> Gateways { get { lock (gate) return gateways; } }

    /// <summary>The list changed (a gateway found, gone, or moved).</summary>
    public event Action<IReadOnlyList<AowLinkGateway>>? Changed;
    /// <summary>A handler threw; the browser carries on.</summary>
    public event Action<Exception>? HandlerFailed;

    void Publish(List<AowLinkGateway> list)
    {
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        lock (gate)
        {
            if (list.Count == gateways.Count && list.Zip(gateways).All(p => Same(p.First, p.Second))) return;
            gateways = list;
        }
        try { Changed?.Invoke(list); }
        catch (Exception e) { HandlerFailed?.Invoke(e); }
    }

    static bool Same(AowLinkGateway a, AowLinkGateway b) =>
        a.Name == b.Name && a.Host == b.Host && a.Port == b.Port && a.HostName == b.HostName
        && a.Txt.Count == b.Txt.Count && a.Txt.All(kv => b.Txt.TryGetValue(kv.Key, out var v) && v == kv.Value);

#if IOS || __IOS__
    // NSNetService is deprecated since iOS 15 but works; Network.framework's NWBrowser gives no
    // address without connecting to the gateway. To revisit (docs/PLAN.md).
#pragma warning disable CA1422
    Foundation.NSNetServiceBrowser? browser;
    readonly Dictionary<string, Foundation.NSNetService> services = new();
    readonly Dictionary<string, AowLinkGateway> resolved = new();

    public void Start() => Foundation.NSRunLoop.Main.BeginInvokeOnMainThread(() =>
    {
        if (browser != null) return;
        browser = new Foundation.NSNetServiceBrowser();
        browser.FoundService += (_, e) =>
        {
            var s = e.Service;
            services[s.Name] = s;
            s.AddressResolved += (_, _) => OnResolved(s);
            s.Resolve(5.0);
        };
        browser.ServiceRemoved += (_, e) =>
        {
            services.Remove(e.Service.Name);
            if (resolved.Remove(e.Service.Name)) Publish(resolved.Values.ToList());
        };
        browser.SearchForServices(ServiceType + ".", "local.");
    });

    void OnResolved(Foundation.NSNetService s)
    {
        string? ip = null;
        foreach (var a in s.Addresses ?? Array.Empty<Foundation.NSData>())
        {
            // A BSD sockaddr: length, family (2: IPv4), port, then the address.
            var b = a.ToArray();
            if (b.Length >= 8 && b[1] == 2) { ip = $"{b[4]}.{b[5]}.{b[6]}.{b[7]}"; break; }
        }
        if (ip == null) return;
        var txt = new Dictionary<string, string>();
        var data = s.GetTxtRecordData();
        if (data != null)
            foreach (var (k, v) in Foundation.NSNetService.DictionaryFromTxtRecord(data))
                txt[k.ToString()] = Encoding.UTF8.GetString(((Foundation.NSData)v).ToArray());
        resolved[s.Name] = new AowLinkGateway(s.Name, ip, (int)s.Port, (s.HostName ?? "").TrimEnd('.'), txt);
        Publish(resolved.Values.ToList());
    }

    /// <summary>Asks again now.</summary>
    public void Refresh() => Foundation.NSRunLoop.Main.BeginInvokeOnMainThread(() =>
    {
        foreach (var s in services.Values) s.Resolve(5.0);
    });

    public void Stop() => Foundation.NSRunLoop.Main.BeginInvokeOnMainThread(() =>
    {
        browser?.Stop();
        browser = null;
        foreach (var s in services.Values) s.Stop();
        services.Clear();
        resolved.Clear();
        Publish(new List<AowLinkGateway>());
    });
#pragma warning restore CA1422
#else
    const int MaxIntervalMs = 30000, ListenMs = 800, MissedRounds = 2;
    static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);
    const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;

    Thread? thread;
    volatile bool stopping;
    readonly AutoResetEvent wake = new(false);
    // What the answers said, by instance (its full name), kept between rounds.
    sealed class Instance
    {
        public string Target = "";
        public int Port;
        public Dictionary<string, string> Txt = new();
        public int Missed;
        public bool SeenThisRound;
    }
    readonly Dictionary<string, Instance> instances = new(StringComparer.OrdinalIgnoreCase);
    // This round's addresses of each host, each marked when it is on the network of the interface
    // that heard it: a host on several networks (or a Mac with VM bridges) answers with all of them.
    readonly Dictionary<string, Dictionary<string, bool>> addresses = new(StringComparer.OrdinalIgnoreCase);
    // Each socket's interface address and mask, to prefer a host's address on the same network.
    readonly Dictionary<Socket, (uint Address, uint Mask)> local = new();

    public void Start()
    {
        if (thread != null) return;
        stopping = false;
        thread = new Thread(Run) { IsBackground = true, Name = "AowLinkBrowser" };
        thread.Start();
    }

    /// <summary>Asks again now, and then often again for a while.</summary>
    public void Refresh() => wake.Set();

    public void Stop()
    {
        var t = thread;
        if (t == null) return;
        stopping = true;
        wake.Set();
        if (Thread.CurrentThread != t) t.Join();
        thread = null;
        instances.Clear();
        addresses.Clear();
        Publish(new List<AowLinkGateway>());
    }

    void Run()
    {
        int interval = 1000;
        while (!stopping)
        {
            try { Round(); }
            catch (Exception e) { HandlerFailed?.Invoke(e); }
            if (wake.WaitOne(interval)) interval = 1000;
            else interval = Math.Min(interval * 2, MaxIntervalMs);
        }
    }

    // One round: ask for the gateways on every interface, then for whatever the answers left out
    // (an instance's SRV and TXT, a host's address), then publish.
    void Round()
    {
        foreach (var i in instances.Values) i.SeenThisRound = false;
        addresses.Clear();
        var sockets = OpenSockets(local);
        try
        {
            Ask(sockets, new[] { (ServiceType + ".local", TypePtr) });
            var missing = new List<(string, ushort)>();
            foreach (var (name, i) in instances)
            {
                if (!i.SeenThisRound) continue;
                if (i.Target.Length == 0) { missing.Add((name, TypeSrv)); missing.Add((name, TypeTxt)); }
                else if (!addresses.ContainsKey(i.Target)) missing.Add((i.Target, TypeA));
            }
            if (missing.Count > 0 && !stopping) Ask(sockets, missing);
            // A target learned from the second answer may still want its address.
            var hosts = instances.Values.Where(i => i.SeenThisRound && i.Target.Length > 0 && !addresses.ContainsKey(i.Target))
                .Select(i => (i.Target, TypeA)).Distinct().ToList();
            if (hosts.Count > 0 && !stopping) Ask(sockets, hosts);
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
            local.Clear();
        }

        foreach (var name in instances.Keys.ToList())
        {
            var i = instances[name];
            if (i.SeenThisRound) i.Missed = 0;
            else if (++i.Missed >= MissedRounds) instances.Remove(name);
        }
        Publish(Current());
    }

    // The gateways the answers so far describe completely (an address for the instance's host).
    internal List<AowLinkGateway> Current()
    {
        var list = new List<AowLinkGateway>();
        foreach (var (name, i) in instances)
            if (i.Target.Length > 0 && addresses.TryGetValue(i.Target, out var ips) && ips.Count > 0)
                list.Add(new AowLinkGateway(InstanceLabel(name), Pick(ips), i.Port, i.Target, i.Txt));
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    // The address on the same network as the interface that heard it; else any, the same each time.
    static string Pick(Dictionary<string, bool> ips)
    {
        foreach (var (ip, here) in ips.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (here) return ip;
        return ips.Keys.Min(StringComparer.Ordinal)!;
    }

    static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    // One socket per IPv4 interface that is up and can multicast, so the query goes out on each.
    static List<Socket> OpenSockets(Dictionary<Socket, (uint, uint)> local)
    {
        var list = new List<Socket>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    s.Bind(new IPEndPoint(ua.Address, 0));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ua.Address.GetAddressBytes());
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    list.Add(s);
                    local[s] = (ToUInt(ua.Address), ua.IPv4Mask is { } mask ? ToUInt(mask) : 0xFFFFFF00);
                }
                catch (SocketException) { s.Dispose(); }
            }
        }
        return list;
    }

    void Ask(List<Socket> sockets, IReadOnlyCollection<(string Name, ushort Type)> questions)
    {
        var query = Query(questions);
        foreach (var s in sockets)
        {
            try { s.SendTo(query, Group); }
            catch (SocketException) { }
        }
        var buffer = new byte[9000];
        var until = Environment.TickCount64 + ListenMs;
        while (!stopping)
        {
            long left = until - Environment.TickCount64;
            if (left <= 0 || sockets.Count == 0) break;
            var ready = sockets.ToList();
            Socket.Select(ready, null, null, (int)(left * 1000));
            foreach (var s in ready)
            {
                int n;
                try { n = s.Receive(buffer); }
                catch (SocketException) { continue; }
                try { Read(buffer.AsSpan(0, n), s); }
                catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException) { } // not a message we can read
            }
        }
    }

    // ---- DNS messages (RFC 1035), just what this needs ----

    static byte[] Query(IReadOnlyCollection<(string Name, ushort Type)> questions)
    {
        var m = new List<byte>(512) { 0, 0, 0, 0, 0, (byte)questions.Count, 0, 0, 0, 0, 0, 0 };
        foreach (var (name, type) in questions)
        {
            foreach (var label in name.Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                m.Add((byte)b.Length);
                m.AddRange(b);
            }
            m.Add(0);
            m.Add((byte)(type >> 8)); m.Add((byte)type);
            m.Add(0); m.Add(1); // class IN
        }
        return m.ToArray();
    }

    // One response's records into what is known (the self-test reads its own).
    internal void Read(ReadOnlySpan<byte> m, Socket? via)
    {
        if (m.Length < 12 || (m[2] & 0x80) == 0) return; // not a response
        int questions = m[4] << 8 | m[5];
        int records = (m[6] << 8 | m[7]) + (m[8] << 8 | m[9]) + (m[10] << 8 | m[11]);
        int p = 12;
        for (int q = 0; q < questions; q++) { ReadName(m, ref p); p += 4; }
        string service = ServiceType + ".local";
        for (int r = 0; r < records; r++)
        {
            string owner = ReadName(m, ref p);
            ushort type = (ushort)(m[p] << 8 | m[p + 1]);
            uint ttl = (uint)(m[p + 4] << 24 | m[p + 5] << 16 | m[p + 6] << 8 | m[p + 7]);
            int length = m[p + 8] << 8 | m[p + 9];
            p += 10;
            int data = p, end = p + length;
            if (end > m.Length) return;
            switch (type)
            {
                case TypePtr when owner.Equals(service, StringComparison.OrdinalIgnoreCase):
                {
                    string name = ReadName(m, ref data);
                    if (ttl == 0) { instances.Remove(name); break; } // a goodbye
                    if (!instances.TryGetValue(name, out var i)) instances[name] = i = new Instance();
                    i.SeenThisRound = true;
                    break;
                }
                case TypeSrv when instances.TryGetValue(owner, out var i):
                    i.Port = m[data + 4] << 8 | m[data + 5];
                    data += 6;
                    i.Target = ReadName(m, ref data);
                    break;
                case TypeTxt when instances.TryGetValue(owner, out var i):
                {
                    var txt = new Dictionary<string, string>();
                    while (data < end)
                    {
                        int n = m[data++];
                        var entry = Encoding.UTF8.GetString(m.Slice(data, n));
                        data += n;
                        int eq = entry.IndexOf('=');
                        if (entry.Length > 0) txt[eq < 0 ? entry : entry[..eq]] = eq < 0 ? "" : entry[(eq + 1)..];
                    }
                    i.Txt = txt;
                    break;
                }
                case TypeA when length == 4:
                {
                    uint a = (uint)(m[data] << 24 | m[data + 1] << 16 | m[data + 2] << 8 | m[data + 3]);
                    if (!addresses.TryGetValue(owner, out var ips)) addresses[owner] = ips = new();
                    var here = via != null && local.TryGetValue(via, out var l) && (a & l.Mask) == (l.Address & l.Mask);
                    string ip = $"{m[data]}.{m[data + 1]}.{m[data + 2]}.{m[data + 3]}";
                    if (here || !ips.ContainsKey(ip)) ips[ip] = here;
                    break;
                }
            }
            p = end;
        }
    }

    // A name, following compression pointers; the position moves past it as written in place.
    static string ReadName(ReadOnlySpan<byte> m, ref int p)
    {
        var labels = new List<string>();
        int at = p, jumps = 0;
        bool jumped = false;
        while (true)
        {
            int n = m[at];
            if (n == 0) { at++; break; }
            if ((n & 0xC0) == 0xC0)
            {
                if (++jumps > 20) throw new FormatException("name pointer loop");
                int target = (n & 0x3F) << 8 | m[at + 1];
                if (!jumped) { p = at + 2; jumped = true; }
                at = target;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(m.Slice(at + 1, n)));
            at += 1 + n;
        }
        if (!jumped) p = at;
        return string.Join('.', labels);
    }

    // "AOW HAT 1234._aowlink._tcp.local" -> "AOW HAT 1234" (an instance label may hold dots).
    static string InstanceLabel(string name)
    {
        string suffix = "." + ServiceType + ".local";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name[..^suffix.Length] : name;
    }
#endif

    public void Dispose() => Stop();
}
