// Self-test of finding gateways (AowLinkBrowser, desktop and Android): mDNS answers built here,
// as a responder writes them (compressed names, the SRV, TXT and address in the additional
// records), read by the browser. Its queries need a real network; the bench checks those
// (docs/RESULTS.md).
#if !IOS && !__IOS__
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgIsoStack;

public static class AowLinkBrowserTest
{
    // A DNS response: the records as (owner, type, ttl, data), names written in full or as a
    // pointer to an earlier one.
    sealed class Message
    {
        readonly List<byte> m = new() { 0, 0, 0x84, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        readonly Dictionary<string, int> at = new();
        int records;

        public byte[] Name(string name)
        {
            var b = new List<byte>();
            var labels = name.Split('.');
            for (int i = 0; i < labels.Length; i++)
            {
                string rest = string.Join('.', labels.Skip(i));
                if (at.TryGetValue(rest, out int p)) { b.Add((byte)(0xC0 | p >> 8)); b.Add((byte)p); return b.ToArray(); }
                var l = Encoding.UTF8.GetBytes(labels[i]);
                b.Add((byte)l.Length); b.AddRange(l);
            }
            b.Add(0);
            return b.ToArray();
        }

        // Writes the name where it lands, remembering each suffix's offset for later pointers.
        void WriteName(string name)
        {
            var labels = name.Split('.');
            for (int i = 0; i < labels.Length; i++)
            {
                string rest = string.Join('.', labels.Skip(i));
                if (at.TryGetValue(rest, out int p)) { m.Add((byte)(0xC0 | p >> 8)); m.Add((byte)p); return; }
                at[rest] = m.Count;
                var l = Encoding.UTF8.GetBytes(labels[i]);
                m.Add((byte)l.Length); m.AddRange(l);
            }
            m.Add(0);
        }

        public Message Record(string owner, ushort type, uint ttl, Func<Message, byte[]> data)
        {
            WriteName(owner);
            m.Add((byte)(type >> 8)); m.Add((byte)type); m.Add(0x80); m.Add(1);
            m.AddRange(new[] { (byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl });
            int lengthAt = m.Count;
            m.Add(0); m.Add(0);
            var d = data(this);
            m.AddRange(d);
            m[lengthAt] = (byte)(d.Length >> 8); m[lengthAt + 1] = (byte)d.Length;
            records++;
            return this;
        }

        public byte[] Bytes()
        {
            var b = m.ToArray();
            b[7] = (byte)records; // all as answers
            return b;
        }
    }

    static byte[] Srv(Message m, int port, string target) => new byte[] { 0, 0, 0, 0, (byte)(port >> 8), (byte)port }.Concat(m.Name(target)).ToArray();
    static byte[] Txt(params string[] entries) => entries.SelectMany(e => new[] { (byte)Encoding.UTF8.GetByteCount(e) }.Concat(Encoding.UTF8.GetBytes(e))).ToArray();

    public static bool Run(Action<string> log)
    {
        log("--- finding gateways, AowLinkBrowser");
        int failures = 0;
        void Check(string what, bool pass)
        {
            if (!pass) failures++;
            log($"  {(pass ? "ok  " : "FAIL")} {what}");
        }
        const string Service = "_aowlink._tcp.local";

        // One answer with everything: the instance (a name with a space and a dot), its host,
        // its TXT and two addresses.
        var b = new AowLinkBrowser();
        string instance = "AOW HAT 1.2." + Service;
        var full = new Message()
            .Record(Service, 12, 4500, m => m.Name(instance))
            .Record(instance, 33, 120, m => Srv(m, 2323, "aow-hat.local"))
            .Record(instance, 16, 4500, _ => Txt("v=1", "flag"))
            .Record("aow-hat.local", 1, 120, _ => new byte[] { 192, 168, 4, 217 })
            .Record("aow-hat.local", 1, 120, _ => new byte[] { 10, 0, 0, 5 })
            .Bytes();
        b.Read(full, null);
        var list = b.Current();
        Check($"one answer, compressed names: {string.Join(", ", list.Select(g => $"\"{g.Name}\" {g.Host}:{g.Port} {g.HostName}"))}",
            list.Count == 1 && list[0].Name == "AOW HAT 1.2" && list[0].Port == 2323 && list[0].HostName == "aow-hat.local"
            && (list[0].Host == "10.0.0.5" || list[0].Host == "192.168.4.217"));
        Check("its TXT record: v=1 and a key without a value", list.Count == 1 && list[0].Txt.TryGetValue("v", out var v) && v == "1" && list[0].Txt.ContainsKey("flag"));

        // Answers in pieces, as a second round's follow-up queries get them.
        var b2 = new AowLinkBrowser();
        string second = "ISOBUS gateway." + Service;
        b2.Read(new Message().Record(Service, 12, 4500, m => m.Name(second)).Bytes(), null);
        Check("an instance alone is not listed yet (no host, no address)", b2.Current().Count == 0);
        b2.Read(new Message().Record(second, 33, 120, m => Srv(m, 2400, "esp32.local")).Bytes(), null);
        Check("with its SRV but no address: still not listed", b2.Current().Count == 0);
        b2.Read(new Message().Record("esp32.local", 1, 120, _ => new byte[] { 192, 168, 4, 1 }).Bytes(), null);
        var l2 = b2.Current();
        Check("its address too: listed, 192.168.4.1:2400", l2.Count == 1 && l2[0].Host == "192.168.4.1" && l2[0].Port == 2400);

        // A goodbye (TTL 0) removes it at once.
        b2.Read(new Message().Record(Service, 12, 0, m => m.Name(second)).Bytes(), null);
        Check("a goodbye removes it", b2.Current().Count == 0);

        // Other services' records are not gateways.
        var b3 = new AowLinkBrowser();
        b3.Read(new Message().Record("_http._tcp.local", 12, 4500, m => m.Name("Printer._http._tcp.local"))
            .Record("Printer._http._tcp.local", 33, 120, m => Srv(m, 80, "printer.local"))
            .Record("printer.local", 1, 120, _ => new byte[] { 192, 168, 4, 50 }).Bytes(), null);
        Check("another service's answer: nothing listed", b3.Current().Count == 0);

        // Damaged answers: cut short anywhere, or a name pointing at itself. Each is refused
        // with an exception the browser catches, and nothing half-read is listed.
        int refused = 0, other = 0;
        for (int cut = 1; cut < full.Length; cut++)
        {
            try { new AowLinkBrowser().Read(full.AsSpan(0, cut), null); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException) { refused++; }
            catch (Exception) { other++; }
        }
        var loop = (byte[])full.Clone();
        loop[12] = 0xC0; loop[13] = 12; // the first record's owner points at itself
        bool loopRefused = false;
        try { new AowLinkBrowser().Read(loop, null); }
        catch (FormatException) { loopRefused = true; }
        Check($"answers cut short: {refused} refused, none with another exception", other == 0);
        Check("a name pointing at itself: refused", loopRefused);

        log(failures == 0 ? "PASS: finding gateways" : $"FAIL: finding gateways, {failures} checks failed");
        return failures == 0;
    }
}
#endif
