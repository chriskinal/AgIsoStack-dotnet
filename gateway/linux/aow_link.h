// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors
//
// The gateway end of the AOW link protocol, version 1: CAN frames between a gateway's channels
// and a terminal (SharpIsoTerm's AowLink), over TCP. The specification is docs/TRANSPORT.md
// section 5; bench/aowlink_check.py tests a running gateway against it. MIT.
//
// Transport-free: the program around it (aow_linkd.cpp, or another platform's) owns the sockets,
// hands in bytes, frames and the time, writes out what each client is owed, and closes the
// clients this says to close. Knows nothing of ISOBUS, and nothing of Linux.
#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <map>
#include <string>
#include <utility>
#include <vector>

namespace aow::link {

constexpr uint8_t kVersion = 1;
constexpr char kMagic[4] = {'A', 'O', 'W', 'L'};
constexpr uint16_t kDefaultPort = 2323;

enum Type : uint8_t { Hello = 1, FrameMsg = 2, Heartbeat = 3, Status = 4, Close = 5 };
enum Reason : uint8_t { NoReason = 0, VersionReason = 1, Busy = 2, Timeout = 3, ProtocolError = 4, ShuttingDown = 5 };
enum BusState : uint8_t { ErrorActive = 0, ErrorWarning = 1, ErrorPassive = 2, BusOff = 3, Down = 4 };
enum Role : uint8_t { Control = 0, Monitor = 1 };

constexpr uint32_t kHeartbeatMs = 100;  // each side sends one this often
constexpr uint32_t kSilenceMs = 1000;   // a side heard nothing for this long: the link is dead
constexpr uint32_t kHelloMs = 2000;     // a client with no HELLO this long after connecting is closed
constexpr uint32_t kStatusMs = 1000;    // STATUS for every channel this often, and on a change of state
constexpr uint32_t kTxLimitMs = 100;    // a client's frame the bus has not taken in this long is dropped
constexpr size_t kMaxMonitors = 4;

struct Frame {
    uint8_t channel = 0;
    bool extended = false;
    uint32_t id = 0;
    uint32_t timeUs = 0;  // bus receive time on the gateway's clock; 0 from a client
    uint8_t len = 0;
    uint8_t data[8] = {};
};

// One CAN channel as the gateway reports it: in HELLO (name, bitrate) and in STATUS.
struct Channel {
    std::string name;
    uint32_t bitrate = 0;
    BusState state = Down;
    uint8_t txErrors = 0, rxErrors = 0;
    uint32_t overflow = 0;  // frames the kernel dropped before they were read, since the socket opened
};

// The bus side, supplied by the program.
class Bus {
public:
    enum class Write { Sent, Full, Lost };  // Full: try again later; Lost: this frame never will go
    virtual ~Bus() = default;
    virtual Write write(const Frame& f) = 0;
    virtual void drain(uint8_t channel) = 0;  // discard every frame waiting to be read
};

// Appends one message. Bodies longer than 255 bytes are cut short (no message here comes close).
void putMessage(std::string& out, Type type, const std::string& body);
void putFrame(std::string& out, const Frame& f);
void putHeartbeat(std::string& out, uint32_t timeMs, uint32_t echo, uint16_t held);
void putStatus(std::string& out, uint8_t channel, const Channel& c, uint32_t rxDropped, uint32_t txDropped);
void putClose(std::string& out, Reason reason, const std::string& text);
void putStr(std::string& out, const std::string& text);  // length u8, then up to 200 bytes

class Gateway {
public:
    using ClientId = int;
    using Log = std::function<void(const std::string&)>;

    Gateway(std::string name, std::vector<Channel> channels, Bus& bus, Log log = nullptr);

    // A TCP client connected / sent bytes / went away (closed by the peer or a socket error).
    void accepted(ClientId id, const std::string& peer, uint64_t nowMs);
    void received(ClientId id, const uint8_t* data, size_t len, uint64_t nowMs);
    void lost(ClientId id, const std::string& why);
    // A frame read from the bus: to every client that has said HELLO.
    void fromBus(const Frame& f);
    // The channel's state, counters and bitrate as last read. A change of state is reported at
    // the next tick.
    void setChannel(uint8_t index, const Channel& c);
    // Timers: heartbeats, STATUS, the silence and hello limits, frames waiting for the bus.
    // Call at least every 10 ms, and after anything above.
    void tick(uint64_t nowMs);
    // Every client is sent CLOSE (shutting down) and is to be closed.
    void shutdown();

    // What a client is owed. The program sends what it can and erases what was sent.
    std::string* output(ClientId id);
    // Clients to close now, each with its last words (its CLOSE, or nothing): one try at sending
    // them, then close with nothing left unsent kept (SO_LINGER 0). They are forgotten here.
    std::vector<std::pair<ClientId, std::string>> takeClosing();

    bool hasControl() const { return control_ >= 0; }
    size_t clients() const { return clients_.size(); }
    const std::vector<Channel>& channels() const { return channels_; }
    bool txPending() const;  // frames waiting for the bus: tick again soon

    struct Counters { uint64_t toClient = 0, toBus = 0, heartbeats = 0, txDropped = 0, txRetries = 0; };

    // The control client, for the gateway's status report: empty name and peer with none.
    struct ControlInfo {
        bool connected = false;
        std::string name, peer;
        Counters counters;
        uint32_t txDropped = 0, rxDropped = 0;  // as its STATUS reports them, all channels summed
    };
    ControlInfo control() const;
    size_t monitors() const;
    uint32_t connections() const { return connections_; }  // control clients taken since the start

private:
    struct Client {
        std::string peer;
        std::string name;
        int role = -1;  // -1 until its HELLO
        uint64_t opened = 0, heard = 0, nextHeartbeat = 0;
        uint32_t heardTime = 0;
        uint64_t heardAt = 0;
        bool heardBeat = false;
        std::string in, out;
        std::vector<uint32_t> overflowBase;
        uint32_t txDropped = 0;
        Counters counters;
    };
    struct Pending { Frame frame; uint64_t deadline; };

    void drop(ClientId id, const std::string& why, Reason reason = NoReason, const std::string& text = "");
    bool handle(ClientId id, Client& c, uint8_t type, const std::string& body, uint64_t nowMs);
    void sendStatus(Client& c, uint8_t channel);
    void flushBus(uint64_t nowMs);
    void say(const std::string& line) const;

    std::string name_;
    std::vector<Channel> channels_;
    std::vector<BusState> reported_;
    Bus& bus_;
    Log log_;
    std::map<ClientId, Client> clients_;
    std::vector<std::pair<ClientId, std::string>> closing_;
    ClientId control_ = -1;
    std::vector<std::vector<Pending>> tx_;
    uint64_t now_ = 0, nextStatus_ = 0;
    uint32_t connections_ = 0;
};

}  // namespace aow::link
