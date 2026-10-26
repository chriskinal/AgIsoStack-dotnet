// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors

#include "aow_link.h"

#include <cstring>
#include <string>
#include <vector>

#include "check.h"

using namespace aow::link;

namespace {

struct FakeBus : Bus {
    std::vector<Frame> sent;
    std::vector<uint8_t> drained;
    int full = 0;        // answer Full this many times
    bool lose = false;   // answer Lost
    Write write(const Frame& f) override {
        if (full > 0) { --full; return Write::Full; }
        if (lose) return Write::Lost;
        sent.push_back(f);
        return Write::Sent;
    }
    void drain(uint8_t channel) override { drained.push_back(channel); }
};

struct Msg { uint8_t type; std::string body; };

// Splits a client's output into messages and empties it.
std::vector<Msg> take(Gateway& g, Gateway::ClientId id) {
    std::vector<Msg> out;
    std::string* s = g.output(id);
    if (!s) return out;
    size_t at = 0;
    while (s->size() - at >= 2 && s->size() - at >= 2u + static_cast<uint8_t>((*s)[at + 1])) {
        const size_t n = static_cast<uint8_t>((*s)[at + 1]);
        out.push_back({static_cast<uint8_t>((*s)[at]), s->substr(at + 2, n)});
        at += 2 + n;
    }
    s->erase(0, at);
    return out;
}

size_t count(const std::vector<Msg>& m, uint8_t type) {
    size_t n = 0;
    for (const Msg& x : m) n += x.type == type;
    return n;
}

const Msg* first(const std::vector<Msg>& m, uint8_t type) {
    for (const Msg& x : m)
        if (x.type == type) return &x;
    return nullptr;
}

uint32_t u32(const std::string& b, size_t at) {
    uint32_t v = 0;
    for (int i = 3; i >= 0; --i) v = (v << 8) | static_cast<uint8_t>(b[at + i]);
    return v;
}

std::string hello(uint8_t version = 1, uint8_t role = 0, const std::string& name = "test") {
    std::string body("AOWL", 4);
    body += static_cast<char>(version);
    body += static_cast<char>(role);
    putStr(body, name);
    std::string out;
    putMessage(out, Hello, body);
    return out;
}

void send(Gateway& g, Gateway::ClientId id, const std::string& bytes, uint64_t now) {
    g.received(id, reinterpret_cast<const uint8_t*>(bytes.data()), bytes.size(), now);
}

// The reason in a closing client's last words, or -1 for none, or -2 if it is not closing.
int closed(std::vector<std::pair<Gateway::ClientId, std::string>>& closing, Gateway::ClientId id) {
    for (auto& [cid, last] : closing)
        if (cid == id) return last.size() >= 3 && last[0] == Close ? static_cast<uint8_t>(last[2]) : -1;
    return -2;
}

std::vector<Channel> twoChannels() {
    Channel a, b;
    a.name = "hatcan2";
    a.bitrate = 250000;
    a.state = ErrorActive;
    b.name = "vcan9";
    b.state = Down;
    return {a, b};
}

}  // namespace

int main() {
    // Encoders: a full extended frame is 20 bytes, as the specification says.
    {
        Frame f;
        f.channel = 1;
        f.extended = true;
        f.id = 0x18EAFFFE;
        f.timeUs = 0x01020304;
        f.len = 8;
        for (int i = 0; i < 8; ++i) f.data[i] = static_cast<uint8_t>(i);
        std::string s;
        putFrame(s, f);
        CHECK(s.size() == 20 && s[0] == FrameMsg && s[1] == 18 && s[2] == 1 && s[3] == 1);
        CHECK(u32(s, 4) == 0x18EAFFFE && u32(s, 8) == 0x01020304 && s[19] == 7);
        std::string h;
        putHeartbeat(h, 5, 6, 7);
        CHECK(h == std::string("\x03\x0A\x05\0\0\0\x06\0\0\0\x07\0", 12));
    }

    FakeBus bus;
    std::vector<std::string> logs;
    Gateway g("AOW HAT test", twoChannels(), bus, [&](const std::string& l) { logs.push_back(l); });
    uint64_t t = 1000;

    // HELLO in two pieces: the gateway's HELLO, then STATUS for each channel; the bus drained.
    g.accepted(1, "10.0.0.2", t);
    const std::string h = hello(1, Control, "AgOpenWeb test");
    send(g, 1, h.substr(0, 5), t);
    CHECK(take(g, 1).empty() && !g.hasControl());
    send(g, 1, h.substr(5), t);
    std::vector<Msg> m = take(g, 1);
    CHECK(!m.empty() && m[0].type == Hello);
    if (!m.empty()) {
        const std::string& b = m[0].body;
        CHECK(b.substr(0, 4) == "AOWL" && b[4] == 1 && b[5] == 0);
        const size_t n = static_cast<uint8_t>(b[6]);
        CHECK(b.substr(7, n) == "AOW HAT test");
        size_t at = 7 + n;
        CHECK(b[at] == 2);
        CHECK(u32(b, at + 1) == 250000 && b[at + 5] == 0 && b.substr(at + 7, static_cast<uint8_t>(b[at + 6])) == "hatcan2");
    }
    CHECK(count(m, Status) == 2);
    CHECK(g.hasControl() && bus.drained.size() == 2);
    CHECK(g.control().connected && g.control().name == "AgOpenWeb test" && g.control().peer == "10.0.0.2");
    CHECK(g.connections() == 1 && g.monitors() == 0);

    // Heartbeats every 100 ms; the echo and the time held after the client's own.
    g.tick(t);
    m = take(g, 1);
    CHECK(count(m, Heartbeat) == 1);
    std::string beat;
    putHeartbeat(beat, 777, 0, 0);
    send(g, 1, beat, t + 20);
    g.tick(t + 50);
    CHECK(count(take(g, 1), Heartbeat) == 0);
    g.tick(t + 100);
    m = take(g, 1);
    const Msg* hb = first(m, Heartbeat);
    CHECK(hb && u32(hb->body, 0) == t + 100 && u32(hb->body, 4) == 777 && hb->body[8] == 80);

    // A second control client is busy; a monitor gets in.
    g.accepted(2, "10.0.0.3", t + 100);
    send(g, 2, hello(1, Control), t + 100);
    auto closing = g.takeClosing();
    CHECK(closed(closing, 2) == Busy);
    // ... saying who has it: the holder's name from its HELLO.
    CHECK(closing.size() == 1 && closing[0].second.find("in use by AgOpenWeb test") != std::string::npos);
    g.accepted(3, "10.0.0.4", t + 100);
    send(g, 3, hello(1, Monitor, "logger"), t + 100);
    m = take(g, 3);
    CHECK(!m.empty() && m[0].type == Hello && count(m, Status) == 2);
    CHECK(g.takeClosing().empty());
    CHECK(g.monitors() == 1 && g.connections() == 1);

    // From the bus to both; from the control client to the bus, and to the monitor.
    Frame f;
    f.channel = 0;
    f.extended = true;
    f.id = 0x0CE6FF26;
    f.len = 3;
    f.data[0] = 9;
    g.fromBus(f);
    CHECK(count(take(g, 1), FrameMsg) == 1 && count(take(g, 3), FrameMsg) == 1);
    std::string fm;
    f.id = 0x18ECFF26;
    putFrame(fm, f);
    send(g, 1, fm, t + 110);
    g.tick(t + 110);
    CHECK(bus.sent.size() == 1 && bus.sent[0].id == 0x18ECFF26 && bus.sent[0].extended && bus.sent[0].len == 3);
    m = take(g, 3);
    CHECK(count(m, FrameMsg) == 1);

    // A frame for a channel that does not exist, or from a monitor: protocol errors.
    send(g, 3, fm, t + 120);
    closing = g.takeClosing();
    CHECK(closed(closing, 3) == ProtocolError);

    // The bus full: frames wait, in order; one past 100 ms is dropped and counted in STATUS.
    bus.sent.clear();
    bus.full = 1000;
    f.id = 0x100;
    f.extended = false;
    fm.clear();
    putFrame(fm, f);
    send(g, 1, fm, t + 200);
    f.id = 0x101;
    fm.clear();
    putFrame(fm, f);
    send(g, 1, fm, t + 250);
    g.tick(t + 260);
    CHECK(bus.sent.empty() && g.txPending());
    bus.full = 0;
    g.tick(t + 301);  // 0x100 is past its 100 ms; 0x101 goes
    CHECK(bus.sent.size() == 1 && bus.sent[0].id == 0x101 && !g.txPending());
    send(g, 1, beat, t + 301);
    take(g, 1);
    g.tick(t + 1001);  // periodic STATUS
    m = take(g, 1);
    const Msg* st = first(m, Status);
    CHECK(st && st->body.size() == 12 && u32(st->body, 8) == 1);

    // A change of state is reported at once; rxDropped counts from the connection's start.
    Channel c = g.channels()[0];
    c.state = BusOff;
    c.txErrors = 255;
    c.overflow = 7;
    g.setChannel(0, c);
    g.tick(t + 1010);
    m = take(g, 1);
    st = first(m, Status);
    CHECK(st && st->body[0] == 0 && st->body[1] == BusOff && static_cast<uint8_t>(st->body[2]) == 255 && u32(st->body, 4) == 7);
    CHECK(g.control().rxDropped == 7 && g.control().txDropped == 1 && g.control().counters.toBus == 3);
    CHECK(g.channels()[0].name == "hatcan2");
    g.tick(t + 1020);
    CHECK(count(take(g, 1), Status) == 0);

    // An unknown message type is skipped.
    std::string future;
    putMessage(future, static_cast<Type>(0x40), "future");
    send(g, 1, future + beat, t + 1100);
    CHECK(g.takeClosing().empty() && g.hasControl());

    // Silence: dropped after 1 s, with no CLOSE sent, the queue cleared, the slot free.
    bus.full = 1000;
    send(g, 1, fm, t + 1200);
    g.tick(t + 1200);
    CHECK(g.txPending());
    g.tick(t + 2200);
    CHECK(g.takeClosing().empty());
    g.tick(t + 2201);
    closing = g.takeClosing();
    CHECK(closed(closing, 1) == -1 && !g.hasControl() && !g.txPending());
    CHECK(!g.control().connected && g.control().name.empty());
    bus.full = 0;

    // A new control client gets in at once.
    t = 5000;
    g.accepted(4, "10.0.0.2", t);
    send(g, 4, hello(), t);
    CHECK(g.hasControl());

    // Wrong version; garbage first; no HELLO in 2 s; a second HELLO.
    g.accepted(5, "x", t);
    send(g, 5, hello(9), t);
    g.accepted(6, "x", t);
    send(g, 6, std::string("\x07\x02hi", 4), t);
    g.accepted(7, "x", t);
    send(g, 4, hello(), t);
    closing = g.takeClosing();
    CHECK(closed(closing, 5) == VersionReason && closed(closing, 6) == ProtocolError && closed(closing, 4) == ProtocolError);
    g.tick(t + 2000);
    CHECK(closed(closing = g.takeClosing(), 7) == -2);
    g.tick(t + 2001);
    closing = g.takeClosing();
    CHECK(closed(closing, 7) == ProtocolError);
    CHECK(g.clients() == 0 && !g.hasControl());

    // CLOSE from the client; then shutdown sends CLOSE 5 to whoever is left.
    g.accepted(8, "x", t);
    send(g, 8, hello(), t);
    std::string bye;
    putClose(bye, ShuttingDown, "");
    send(g, 8, bye, t);
    closing = g.takeClosing();
    CHECK(closed(closing, 8) == -1 && !g.hasControl());
    g.accepted(9, "x", t);
    send(g, 9, hello(), t);
    g.accepted(10, "x", t);
    g.shutdown();
    closing = g.takeClosing();
    CHECK(closed(closing, 9) == ShuttingDown && closed(closing, 10) == ShuttingDown && g.clients() == 0);

    // Lost while a frame was failing: Lost counts as dropped.
    t = 10000;
    g.accepted(11, "x", t);
    send(g, 11, hello(), t);
    bus.lose = true;
    send(g, 11, fm, t);
    g.tick(t);
    CHECK(!g.txPending());
    take(g, 11);
    g.tick(t + 1000);
    m = take(g, 11);
    st = first(m, Status);
    CHECK(st && u32(st->body, 8) == 1);
    g.lost(11, "closed by the client");
    CHECK(closed(closing = g.takeClosing(), 11) == -1 && !logs.empty());

    return finish("aow_link");
}
