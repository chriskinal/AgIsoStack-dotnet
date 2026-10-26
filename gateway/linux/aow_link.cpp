// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors

#include "aow_link.h"

#include <algorithm>
#include <cstring>

namespace aow::link {

namespace {

void le16(std::string& o, uint16_t v) {
    o += static_cast<char>(v & 0xFF);
    o += static_cast<char>(v >> 8);
}

void le32(std::string& o, uint32_t v) {
    for (int i = 0; i < 4; ++i) o += static_cast<char>((v >> (8 * i)) & 0xFF);
}

uint32_t rd32(const std::string& b, size_t at) {
    uint32_t v = 0;
    for (int i = 3; i >= 0; --i) v = (v << 8) | static_cast<uint8_t>(b[at + i]);
    return v;
}

// The heartbeat's clock: milliseconds, never 0 (0 means "nothing heard" in an echo).
uint32_t clock32(uint64_t nowMs) {
    const uint32_t t = static_cast<uint32_t>(nowMs);
    return t ? t : 1;
}

}  // namespace

void putMessage(std::string& out, Type type, const std::string& body) {
    const size_t n = std::min<size_t>(body.size(), 255);
    out += static_cast<char>(type);
    out += static_cast<char>(n);
    out.append(body, 0, n);
}

void putStr(std::string& out, const std::string& text) {
    const size_t n = std::min<size_t>(text.size(), 200);
    out += static_cast<char>(n);
    out.append(text, 0, n);
}

void putFrame(std::string& out, const Frame& f) {
    const uint8_t len = std::min<uint8_t>(f.len, 8);
    out += static_cast<char>(FrameMsg);
    out += static_cast<char>(10 + len);
    out += static_cast<char>(f.channel);
    out += static_cast<char>(f.extended ? 1 : 0);
    le32(out, f.id);
    le32(out, f.timeUs);
    out.append(reinterpret_cast<const char*>(f.data), len);
}

void putHeartbeat(std::string& out, uint32_t timeMs, uint32_t echo, uint16_t held) {
    std::string b;
    le32(b, timeMs);
    le32(b, echo);
    le16(b, held);
    putMessage(out, Heartbeat, b);
}

void putStatus(std::string& out, uint8_t channel, const Channel& c, uint32_t rxDropped, uint32_t txDropped) {
    std::string b;
    b += static_cast<char>(channel);
    b += static_cast<char>(c.state);
    b += static_cast<char>(c.txErrors);
    b += static_cast<char>(c.rxErrors);
    le32(b, rxDropped);
    le32(b, txDropped);
    putMessage(out, Status, b);
}

void putClose(std::string& out, Reason reason, const std::string& text) {
    std::string b(1, static_cast<char>(reason));
    putStr(b, text);
    putMessage(out, Close, b);
}

Gateway::Gateway(std::string name, std::vector<Channel> channels, Bus& bus, Log log)
    : name_(std::move(name)), channels_(std::move(channels)), reported_(channels_.size(), Down), bus_(bus),
      log_(std::move(log)), tx_(channels_.size()) {
    for (size_t i = 0; i < channels_.size(); ++i) reported_[i] = channels_[i].state;
}

void Gateway::say(const std::string& line) const {
    if (log_) log_(line);
}

void Gateway::accepted(ClientId id, const std::string& peer, uint64_t nowMs) {
    now_ = nowMs;
    Client& c = clients_[id];
    c = Client{};
    c.peer = peer;
    c.opened = c.heard = nowMs;
}

std::string* Gateway::output(ClientId id) {
    auto it = clients_.find(id);
    return it == clients_.end() ? nullptr : &it->second.out;
}

std::vector<std::pair<Gateway::ClientId, std::string>> Gateway::takeClosing() {
    std::vector<std::pair<ClientId, std::string>> out;
    out.swap(closing_);
    return out;
}

bool Gateway::txPending() const {
    for (const auto& q : tx_)
        if (!q.empty()) return true;
    return false;
}

void Gateway::drop(ClientId id, const std::string& why, Reason reason, const std::string& text) {
    auto it = clients_.find(id);
    if (it == clients_.end()) return;
    Client& c = it->second;
    std::string last;  // what was not yet sent is discarded: only the CLOSE goes
    if (reason != NoReason) putClose(last, reason, text);
    closing_.emplace_back(id, std::move(last));
    const char* role = c.role == Control ? "control" : c.role == Monitor ? "monitor" : "no hello";
    say("client gone: " + c.peer + " " + role + (c.name.empty() ? "" : " \"" + c.name + "\"") + ", " + why +
        ": to client " + std::to_string(c.counters.toClient) + ", to bus " + std::to_string(c.counters.toBus) +
        ", heartbeats " + std::to_string(c.counters.heartbeats) + ", tx dropped " + std::to_string(c.txDropped) +
        ", tx retries " + std::to_string(c.counters.txRetries));
    if (id == control_) {
        control_ = -1;
        for (auto& q : tx_) q.clear();  // nothing meant for this link goes out after it
    }
    clients_.erase(it);
}

void Gateway::lost(ClientId id, const std::string& why) { drop(id, why); }

void Gateway::shutdown() {
    std::vector<ClientId> ids;
    for (const auto& [id, c] : clients_) ids.push_back(id);
    for (ClientId id : ids) drop(id, "shutting down", ShuttingDown, "the gateway is stopping");
}

void Gateway::received(ClientId id, const uint8_t* data, size_t len, uint64_t nowMs) {
    now_ = nowMs;
    auto it = clients_.find(id);
    if (it == clients_.end()) return;
    Client& c = it->second;
    c.heard = nowMs;
    c.in.append(reinterpret_cast<const char*>(data), len);
    size_t at = 0;
    while (c.in.size() - at >= 2) {
        const size_t n = static_cast<uint8_t>(c.in[at + 1]);
        if (c.in.size() - at < 2 + n) break;
        const uint8_t type = static_cast<uint8_t>(c.in[at]);
        const std::string body = c.in.substr(at + 2, n);
        at += 2 + n;
        if (!handle(id, c, type, body, nowMs)) return;  // dropped: c is gone
    }
    c.in.erase(0, at);
}

// One message from a client. False if the client was dropped.
bool Gateway::handle(ClientId id, Client& c, uint8_t type, const std::string& body, uint64_t nowMs) {
    if (c.role < 0) {
        if (type != Hello || body.size() < 7 || std::memcmp(body.data(), kMagic, 4) != 0) {
            drop(id, "no HELLO", ProtocolError, "expected HELLO");
            return false;
        }
        const uint8_t version = static_cast<uint8_t>(body[4]), role = static_cast<uint8_t>(body[5]);
        const size_t nameLen = std::min<size_t>(static_cast<uint8_t>(body[6]), body.size() - 7);
        c.name.assign(body, 7, nameLen);
        if (version != kVersion) {
            drop(id, "version " + std::to_string(version), VersionReason,
                 "this gateway speaks version " + std::to_string(kVersion));
            return false;
        }
        if (role != Control && role != Monitor) {
            drop(id, "role " + std::to_string(role), ProtocolError, "unknown role");
            return false;
        }
        if (role == Control && control_ >= 0) {
            // Said by name, so the second terminal can tell the operator who has the bus.
            const Client& holder = clients_.at(control_);
            drop(id, "busy", Busy, "in use by " + (holder.name.empty() ? holder.peer : holder.name));
            return false;
        }
        if (role == Monitor) {
            size_t monitors = 0;
            for (const auto& [other, m] : clients_) monitors += m.role == Monitor;
            if (monitors >= kMaxMonitors) {
                drop(id, "busy", Busy, "too many monitors");
                return false;
            }
        }
        c.role = role;
        if (role == Control) {
            control_ = id;
            ++connections_;
            for (size_t i = 0; i < channels_.size(); ++i) {
                bus_.drain(static_cast<uint8_t>(i));  // nothing older than the link
                tx_[i].clear();
            }
        }
        for (const Channel& ch : channels_) c.overflowBase.push_back(ch.overflow);
        std::string hello(kMagic, 4);
        hello += static_cast<char>(kVersion);
        hello += static_cast<char>(0);
        putStr(hello, name_);
        hello += static_cast<char>(channels_.size());
        for (const Channel& ch : channels_) {
            le32(hello, ch.bitrate);
            hello += static_cast<char>(0);
            putStr(hello, ch.name);
        }
        putMessage(c.out, Hello, hello);
        for (size_t i = 0; i < channels_.size(); ++i) sendStatus(c, static_cast<uint8_t>(i));
        c.nextHeartbeat = nowMs;
        say("client " + c.peer + " " + (role == Control ? "control" : "monitor") + " \"" + c.name + "\"");
        return true;
    }
    switch (type) {
    case FrameMsg: {
        if (c.role != Control) {
            drop(id, "FRAME from a monitor", ProtocolError, "a monitor sends no frames");
            return false;
        }
        if (body.size() < 10 || static_cast<uint8_t>(body[0]) >= channels_.size()) {
            drop(id, "bad FRAME", ProtocolError, "bad FRAME");
            return false;
        }
        Frame f;
        f.channel = static_cast<uint8_t>(body[0]);
        f.extended = body[1] & 1;
        f.id = rd32(body, 2) & (f.extended ? 0x1FFFFFFFu : 0x7FFu);
        f.len = static_cast<uint8_t>(std::min<size_t>(body.size() - 10, 8));
        std::memcpy(f.data, body.data() + 10, f.len);
        tx_[f.channel].push_back({f, nowMs + kTxLimitMs});
        ++c.counters.toBus;
        for (auto& [other, m] : clients_)  // monitors see both ways
            if (m.role == Monitor) {
                putMessage(m.out, FrameMsg, body);
                ++m.counters.toClient;
            }
        return true;
    }
    case Heartbeat:
        if (body.size() >= 10) {
            c.heardTime = rd32(body, 0);
            c.heardAt = nowMs;
            c.heardBeat = true;
            ++c.counters.heartbeats;
        }
        return true;
    case Close:
        drop(id, "CLOSE " + std::to_string(body.empty() ? 0 : static_cast<uint8_t>(body[0])));
        return false;
    case Hello:
        drop(id, "second HELLO", ProtocolError, "a second HELLO");
        return false;
    default:
        return true;  // a newer message: skipped
    }
}

void Gateway::sendStatus(Client& c, uint8_t i) {
    const Channel& ch = channels_[i];
    const uint32_t base = i < c.overflowBase.size() ? c.overflowBase[i] : ch.overflow;
    putStatus(c.out, i, ch, ch.overflow - base, c.role == Control ? c.txDropped : 0);
}

Gateway::ControlInfo Gateway::control() const {
    ControlInfo info;
    auto it = clients_.find(control_);
    if (control_ < 0 || it == clients_.end()) return info;
    const Client& c = it->second;
    info.connected = true;
    info.name = c.name;
    info.peer = c.peer;
    info.counters = c.counters;
    info.txDropped = c.txDropped;
    for (size_t i = 0; i < channels_.size() && i < c.overflowBase.size(); ++i)
        info.rxDropped += channels_[i].overflow - c.overflowBase[i];
    return info;
}

size_t Gateway::monitors() const {
    size_t n = 0;
    for (const auto& [id, c] : clients_) n += c.role == Monitor;
    return n;
}

void Gateway::fromBus(const Frame& f) {
    std::string msg;
    putFrame(msg, f);
    for (auto& [id, c] : clients_)
        if (c.role >= 0) {
            c.out += msg;
            ++c.counters.toClient;
        }
}

void Gateway::setChannel(uint8_t index, const Channel& c) {
    if (index >= channels_.size()) return;
    Channel& ch = channels_[index];
    const std::string name = ch.name;
    ch = c;
    if (ch.name.empty()) ch.name = name;
}

void Gateway::flushBus(uint64_t nowMs) {
    Client* control = nullptr;
    if (control_ >= 0) control = &clients_[control_];
    for (auto& q : tx_) {
        size_t done = 0;
        for (; done < q.size(); ++done) {
            if (nowMs > q[done].deadline) {
                if (control) ++control->txDropped;
                continue;
            }
            const Bus::Write w = bus_.write(q[done].frame);
            if (w == Bus::Write::Full) {
                if (control) ++control->counters.txRetries;
                break;  // the controller's queue is full: the rest wait, in order
            }
            if (w == Bus::Write::Lost && control) ++control->txDropped;
        }
        q.erase(q.begin(), q.begin() + static_cast<std::ptrdiff_t>(done));
    }
}

void Gateway::tick(uint64_t nowMs) {
    now_ = nowMs;
    flushBus(nowMs);

    const bool periodic = nowMs >= nextStatus_;
    if (periodic) nextStatus_ = nowMs + kStatusMs;
    std::vector<ClientId> silent, noHello;
    for (auto& [id, c] : clients_) {
        if (c.role < 0) {
            if (nowMs - c.opened > kHelloMs) noHello.push_back(id);
            continue;
        }
        if (nowMs - c.heard > kSilenceMs) {
            silent.push_back(id);
            continue;
        }
        if (nowMs >= c.nextHeartbeat) {
            c.nextHeartbeat = nowMs + kHeartbeatMs;
            const uint64_t held = c.heardBeat ? std::min<uint64_t>(nowMs - c.heardAt, 0xFFFF) : 0;
            putHeartbeat(c.out, clock32(nowMs), c.heardBeat ? c.heardTime : 0, static_cast<uint16_t>(held));
        }
        for (size_t i = 0; i < channels_.size(); ++i)
            if (periodic || channels_[i].state != reported_[i]) sendStatus(c, static_cast<uint8_t>(i));
    }
    for (size_t i = 0; i < channels_.size(); ++i) reported_[i] = channels_[i].state;
    for (ClientId id : noHello) drop(id, "no HELLO in 2 s", ProtocolError, "no HELLO");
    for (ClientId id : silent) drop(id, "silent for 1 s");
}

}  // namespace aow::link
