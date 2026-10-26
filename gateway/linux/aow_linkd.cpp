// aow-linkd: the AOW link gateway for Linux (gateway/README.md). Carries every frame of one or
// more SocketCAN interfaces to a terminal (SharpIsoTerm's AowLink), and the terminal's frames to
// the bus, over TCP. The protocol is docs/TRANSPORT.md section 5 (aow_link.cpp). MIT.
//
// It parses nothing, so it can sit beside other programs on the same interface (SocketCAN shares
// it). One thread, one poll loop: the listener, the CAN sockets and the clients. Normal priority.
// Bus state, error counters and bitrate come from rtnetlink once a second. Under systemd it
// reports ready and pings the watchdog from the loop (Type=notify), so a hang is restarted too.
// With --status, once a second and when a terminal connects or goes, it sends how it is as one
// JSON datagram, for whatever shows it (on the AOW HAT, aow-hatd's ISOBUS page).
#include <arpa/inet.h>
#include <fcntl.h>
#include <linux/can.h>
#include <linux/can/netlink.h>
#include <linux/can/raw.h>
#include <linux/rtnetlink.h>
#include <net/if.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <cerrno>
#include <chrono>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <fstream>
#include <map>
#include <string>
#include <vector>

#include "aow_link.h"

namespace {

#ifndef AOW_LINKD_VERSION
#define AOW_LINKD_VERSION "0.0.0"  // set by CMake
#endif
constexpr const char* kVersion = AOW_LINKD_VERSION;
constexpr int kTickMs = 10;               // the gateway's timers are checked this often
constexpr int kBusyTickMs = 2;            // while frames wait for the bus
constexpr size_t kMaxBehind = 1 << 20;    // a client owed more than this is not reading: dropped
constexpr uint64_t kRefreshMs = 1000;     // bus state, and reopening a missing interface
constexpr uint64_t kPingMs = 1000;        // systemd's watchdog (WatchdogSec=5 in the unit)

using aow::link::BusState;
using aow::link::Frame;

volatile std::sig_atomic_t g_stop = 0;

uint64_t nowMs() {
    using namespace std::chrono;
    return static_cast<uint64_t>(duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count());
}

void log(const std::string& line) { std::printf("%s\n", line.c_str()); }

// sd_notify without libsystemd: one datagram to $NOTIFY_SOCKET, if there is one.
void notify(const char* state) {
    const char* path = std::getenv("NOTIFY_SOCKET");
    if (!path || !*path) return;
    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    std::strncpy(addr.sun_path, path, sizeof addr.sun_path - 1);
    if (addr.sun_path[0] == '@') addr.sun_path[0] = '\0';  // abstract namespace
    const int fd = socket(AF_UNIX, SOCK_DGRAM | SOCK_CLOEXEC, 0);
    if (fd < 0) return;
    const socklen_t len = static_cast<socklen_t>(offsetof(sockaddr_un, sun_path) + std::strlen(path));
    sendto(fd, state, std::strlen(state), MSG_NOSIGNAL, reinterpret_cast<sockaddr*>(&addr), len);
    close(fd);
}

// Bitrate, state and error counters of a CAN interface, from rtnetlink (what "ip -details link"
// shows). An interface with no CAN data (vcan) is error-active while it is up.
class LinkInfo {
public:
    LinkInfo() {
        fd_ = socket(AF_NETLINK, SOCK_RAW | SOCK_CLOEXEC, NETLINK_ROUTE);
        timeval tv{0, 100000};
        if (fd_ >= 0) setsockopt(fd_, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
    }
    ~LinkInfo() { if (fd_ >= 0) close(fd_); }

    bool read(int ifindex, aow::link::Channel& c) {
        if (fd_ < 0) return false;
        struct { nlmsghdr h; ifinfomsg i; } req{};
        req.h.nlmsg_len = NLMSG_LENGTH(sizeof(ifinfomsg));
        req.h.nlmsg_type = RTM_GETLINK;
        req.h.nlmsg_flags = NLM_F_REQUEST;
        req.h.nlmsg_seq = ++seq_;
        req.i.ifi_family = AF_UNSPEC;
        req.i.ifi_index = ifindex;
        if (send(fd_, &req, req.h.nlmsg_len, 0) < 0) return false;
        alignas(nlmsghdr) char buf[16384];
        for (int tries = 0; tries < 4; ++tries) {  // skip a late answer to an earlier request
            ssize_t n = recv(fd_, buf, sizeof buf, 0);
            if (n <= 0) return false;
            for (auto* h = reinterpret_cast<nlmsghdr*>(buf); NLMSG_OK(h, static_cast<unsigned>(n)); h = NLMSG_NEXT(h, n)) {
                if (h->nlmsg_seq != seq_) continue;
                if (h->nlmsg_type == NLMSG_ERROR) return false;
                if (h->nlmsg_type != RTM_NEWLINK) continue;
                parse(h, c);
                return true;
            }
        }
        return false;
    }

private:
    static void parse(nlmsghdr* h, aow::link::Channel& c) {
        auto* ifi = static_cast<ifinfomsg*>(NLMSG_DATA(h));
        const bool up = ifi->ifi_flags & IFF_UP;
        bool haveState = false;
        uint32_t state = 0;
        c.txErrors = c.rxErrors = 0;
        int len = static_cast<int>(IFLA_PAYLOAD(h));
        for (rtattr* a = IFLA_RTA(ifi); RTA_OK(a, len); a = RTA_NEXT(a, len)) {
            if (a->rta_type != IFLA_LINKINFO) continue;
            int l2 = static_cast<int>(RTA_PAYLOAD(a));
            for (auto* b = static_cast<rtattr*>(RTA_DATA(a)); RTA_OK(b, l2); b = RTA_NEXT(b, l2)) {
                if (b->rta_type != IFLA_INFO_DATA) continue;
                int l3 = static_cast<int>(RTA_PAYLOAD(b));
                for (auto* d = static_cast<rtattr*>(RTA_DATA(b)); RTA_OK(d, l3); d = RTA_NEXT(d, l3)) {
                    if (d->rta_type == IFLA_CAN_STATE && RTA_PAYLOAD(d) >= sizeof(uint32_t)) {
                        std::memcpy(&state, RTA_DATA(d), sizeof state);
                        haveState = true;
                    } else if (d->rta_type == IFLA_CAN_BERR_COUNTER && RTA_PAYLOAD(d) >= sizeof(can_berr_counter)) {
                        can_berr_counter bc;
                        std::memcpy(&bc, RTA_DATA(d), sizeof bc);
                        c.txErrors = static_cast<uint8_t>(bc.txerr > 255 ? 255 : bc.txerr);
                        c.rxErrors = static_cast<uint8_t>(bc.rxerr > 255 ? 255 : bc.rxerr);
                    } else if (d->rta_type == IFLA_CAN_BITTIMING && RTA_PAYLOAD(d) >= sizeof(can_bittiming)) {
                        can_bittiming bt;
                        std::memcpy(&bt, RTA_DATA(d), sizeof bt);
                        c.bitrate = bt.bitrate;
                    }
                }
            }
        }
        if (!up) c.state = aow::link::Down;
        else if (!haveState) c.state = aow::link::ErrorActive;
        else if (state == CAN_STATE_ERROR_ACTIVE) c.state = aow::link::ErrorActive;
        else if (state == CAN_STATE_ERROR_WARNING) c.state = aow::link::ErrorWarning;
        else if (state == CAN_STATE_ERROR_PASSIVE) c.state = aow::link::ErrorPassive;
        else if (state == CAN_STATE_BUS_OFF) c.state = aow::link::BusOff;
        else c.state = aow::link::Down;  // stopped, sleeping
    }

    int fd_ = -1;
    uint32_t seq_ = 0;
};

// One SocketCAN interface. A missing interface is looked for again every second.
class CanChannel {
public:
    CanChannel(uint8_t index, std::string iface) : index_(index), iface_(std::move(iface)) { info_.name = iface_; }
    ~CanChannel() { closeSocket(); }
    CanChannel(const CanChannel&) = delete;
    CanChannel& operator=(const CanChannel&) = delete;

    int fd() const { return fd_; }
    const aow::link::Channel& info() const { return info_; }

    void refresh(LinkInfo& nl) {
        if (fd_ < 0) open();
        if (fd_ < 0 || !nl.read(ifindex_, info_)) info_.state = aow::link::Down;
    }

    // Every frame waiting, to the gateway.
    void read(aow::link::Gateway& g) {
        if (fd_ < 0) return;
        for (;;) {
            can_frame cf;
            iovec iov{&cf, sizeof cf};
            alignas(cmsghdr) char ctrl[CMSG_SPACE(sizeof(timeval)) + CMSG_SPACE(sizeof(uint32_t))];
            msghdr msg{};
            msg.msg_iov = &iov;
            msg.msg_iovlen = 1;
            msg.msg_control = ctrl;
            msg.msg_controllen = sizeof ctrl;
            const ssize_t n = recvmsg(fd_, &msg, MSG_DONTWAIT);
            if (n < 0) {
                if (errno == EAGAIN || errno == EWOULDBLOCK || errno == EINTR) return;
                if (errno == ENETDOWN) return;  // the state says so at the next refresh
                log("aow-linkd: " + iface_ + ": " + std::strerror(errno) + ", reopening");
                closeSocket();
                return;
            }
            if (n < static_cast<ssize_t>(sizeof cf) || (cf.can_id & (CAN_ERR_FLAG | CAN_RTR_FLAG))) continue;
            Frame f;
            f.channel = index_;
            f.extended = cf.can_id & CAN_EFF_FLAG;
            f.id = cf.can_id & (f.extended ? CAN_EFF_MASK : CAN_SFF_MASK);
            f.len = cf.can_dlc > 8 ? 8 : cf.can_dlc;
            std::memcpy(f.data, cf.data, f.len);
            bool stamped = false;
            for (cmsghdr* c = CMSG_FIRSTHDR(&msg); c; c = CMSG_NXTHDR(&msg, c)) {
                if (c->cmsg_level != SOL_SOCKET) continue;
                if (c->cmsg_type == SO_TIMESTAMP) {
                    timeval tv;
                    std::memcpy(&tv, CMSG_DATA(c), sizeof tv);
                    f.timeUs = static_cast<uint32_t>(static_cast<uint64_t>(tv.tv_sec) * 1000000u + static_cast<uint64_t>(tv.tv_usec));
                    stamped = true;
                } else if (c->cmsg_type == SO_RXQ_OVFL) {
                    std::memcpy(&info_.overflow, CMSG_DATA(c), sizeof info_.overflow);
                }
            }
            if (!stamped) f.timeUs = static_cast<uint32_t>(nowMs() * 1000);
            g.fromBus(f);
        }
    }

    aow::link::Bus::Write write(const Frame& f) {
        if (fd_ < 0) return aow::link::Bus::Write::Lost;
        can_frame cf{};
        cf.can_id = f.extended ? (f.id & CAN_EFF_MASK) | CAN_EFF_FLAG : f.id & CAN_SFF_MASK;
        cf.can_dlc = f.len;
        std::memcpy(cf.data, f.data, f.len);
        if (send(fd_, &cf, sizeof cf, MSG_DONTWAIT) == static_cast<ssize_t>(sizeof cf)) return aow::link::Bus::Write::Sent;
        if (errno == ENOBUFS || errno == EAGAIN || errno == EWOULDBLOCK) return aow::link::Bus::Write::Full;
        return aow::link::Bus::Write::Lost;  // down, bus-off: this frame is lost
    }

    void drain() {
        if (fd_ < 0) return;
        can_frame cf;
        while (recv(fd_, &cf, sizeof cf, MSG_DONTWAIT) > 0) {}
    }

private:
    void open() {
        ifindex_ = static_cast<int>(if_nametoindex(iface_.c_str()));
        if (ifindex_ == 0) {
            if (!missing_) log("aow-linkd: no interface " + iface_ + " yet");
            missing_ = true;
            return;
        }
        fd_ = socket(PF_CAN, SOCK_RAW | SOCK_NONBLOCK | SOCK_CLOEXEC, CAN_RAW);
        if (fd_ < 0) return;
        const int one = 1, rcvbuf = 4 << 20;
        setsockopt(fd_, SOL_SOCKET, SO_RCVBUF, &rcvbuf, sizeof rcvbuf);
        setsockopt(fd_, SOL_SOCKET, SO_RXQ_OVFL, &one, sizeof one);
        setsockopt(fd_, SOL_SOCKET, SO_TIMESTAMP, &one, sizeof one);
        sockaddr_can addr{};
        addr.can_family = AF_CAN;
        addr.can_ifindex = ifindex_;
        if (bind(fd_, reinterpret_cast<sockaddr*>(&addr), sizeof addr) < 0) {
            log("aow-linkd: " + iface_ + ": bind: " + std::strerror(errno));
            closeSocket();
            return;
        }
        info_.overflow = 0;
        log("aow-linkd: " + iface_ + " open as channel " + std::to_string(index_));
        missing_ = false;
    }
    void closeSocket() {
        if (fd_ >= 0) close(fd_);
        fd_ = -1;
    }

    uint8_t index_;
    std::string iface_;
    int fd_ = -1, ifindex_ = 0;
    bool missing_ = false;
    aow::link::Channel info_;
};

class CanBus : public aow::link::Bus {
public:
    explicit CanBus(std::deque<CanChannel>& channels) : channels_(channels) {}
    Write write(const Frame& f) override {
        return f.channel < channels_.size() ? channels_[f.channel].write(f) : Write::Lost;
    }
    void drain(uint8_t channel) override {
        if (channel < channels_.size()) channels_[channel].drain();
    }

private:
    std::deque<CanChannel>& channels_;
};

struct Options {
    std::vector<std::string> can{"can0"};
    std::string listen = "127.0.0.1";
    uint16_t port = aow::link::kDefaultPort;
    std::string name;
    std::string statusHost;  // empty: no reports
    uint16_t statusPort = 0;
};

[[noreturn]] void usage(int code) {
    std::printf(
        "aow-linkd %s: the AOW link gateway (CAN <-> TCP for a virtual terminal)\n"
        "  --can IF[,IF...]    CAN interfaces, channels numbered in this order (default can0)\n"
        "  --listen ADDR       address to listen on (default 127.0.0.1; 0.0.0.0 for every network)\n"
        "  --port N            TCP port (default %u)\n"
        "  --name TEXT         the gateway's name in its HELLO (default: host name and interfaces)\n"
        "  --config FILE       settings read before the other options (so they win), one a line:\n"
        "                      name=TEXT, network=1 (listen on every network, as --listen 0.0.0.0)\n"
        "  --status HOST:PORT  how it is, as a JSON datagram, every second and when a terminal\n"
        "                      connects or goes (IPv4)\n"
        "  --version\n",
        kVersion, aow::link::kDefaultPort);
    std::exit(code);
}

std::vector<std::string> split(const std::string& s) {
    std::vector<std::string> out;
    size_t at = 0;
    while (at <= s.size()) {
        const size_t comma = s.find(',', at);
        const std::string part = s.substr(at, comma == std::string::npos ? std::string::npos : comma - at);
        if (!part.empty()) out.push_back(part);
        if (comma == std::string::npos) break;
        at = comma + 1;
    }
    return out;
}

// name=TEXT and network=0|1, one a line; anything else is ignored. A missing file changes nothing.
void readConfig(const std::string& path, Options& o) {
    std::ifstream in(path);
    std::string line;
    while (std::getline(in, line)) {
        const size_t eq = line.find('=');
        if (eq == std::string::npos) continue;
        const std::string key = line.substr(0, eq), value = line.substr(eq + 1);
        if (key == "name") o.name = value;
        else if (key == "network") o.listen = value == "1" ? "0.0.0.0" : "127.0.0.1";
    }
}

Options parse(int argc, char** argv) {
    Options o;
    for (int i = 1; i + 1 < argc; ++i)
        if (std::string(argv[i]) == "--config") readConfig(argv[i + 1], o);
    for (int i = 1; i < argc; ++i) {
        const std::string a = argv[i];
        const bool hasValue = i + 1 < argc;
        if (a == "--can" && hasValue) o.can = split(argv[++i]);
        else if (a == "--listen" && hasValue) o.listen = argv[++i];
        else if (a == "--port" && hasValue) o.port = static_cast<uint16_t>(std::atoi(argv[++i]));
        else if (a == "--name" && hasValue) o.name = argv[++i];
        else if (a == "--config" && hasValue) ++i;  // read above
        else if (a == "--status" && hasValue) {
            const std::string to = argv[++i];
            const size_t colon = to.rfind(':');
            if (colon == std::string::npos) usage(2);
            o.statusHost = to.substr(0, colon);
            o.statusPort = static_cast<uint16_t>(std::atoi(to.c_str() + colon + 1));
            if (o.statusPort == 0) usage(2);
        }
        else if (a == "--version") { std::printf("aow-linkd %s\n", kVersion); std::exit(0); }
        else if (a == "--help" || a == "-h") usage(0);
        else { std::fprintf(stderr, "unknown option: %s\n", a.c_str()); usage(2); }
    }
    if (o.can.empty() || o.can.size() > 255 || o.port == 0) usage(2);
    return o;
}

int listenOn(const Options& o) {
    const int fd = socket(AF_INET, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    if (fd < 0) return -1;
    const int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(o.port);
    if (inet_pton(AF_INET, o.listen.c_str(), &addr.sin_addr) != 1) {
        std::fprintf(stderr, "aow-linkd: --listen %s is not an IPv4 address\n", o.listen.c_str());
        close(fd);
        return -1;
    }
    if (bind(fd, reinterpret_cast<sockaddr*>(&addr), sizeof addr) < 0 || ::listen(fd, 4) < 0) {
        std::fprintf(stderr, "aow-linkd: cannot listen on %s:%u: %s\n", o.listen.c_str(), o.port, std::strerror(errno));
        close(fd);
        return -1;
    }
    return fd;
}

// One try at sending a closing client's last words, then a close that drops whatever the kernel
// still holds for it (SO_LINGER 0): nothing older reaches it after the link is gone.
void closeHard(int fd, const std::string& last) {
    if (!last.empty()) send(fd, last.data(), last.size(), MSG_NOSIGNAL | MSG_DONTWAIT);
    const linger lg{1, 0};
    setsockopt(fd, SOL_SOCKET, SO_LINGER, &lg, sizeof lg);
    close(fd);
}

// JSON string contents: quotes, backslashes and control characters escaped.
std::string jsonText(const std::string& s) {
    std::string out;
    for (const char ch : s) {
        const unsigned char c = static_cast<unsigned char>(ch);
        if (c == '"' || c == '\\') out += '\\', out += ch;
        else if (c < 0x20) {
            char esc[8];
            std::snprintf(esc, sizeof esc, "\\u%04x", c);
            out += esc;
        } else out += ch;
    }
    return out;
}

// The --status report, one JSON datagram. Nobody listening costs nothing.
//   {"gateway":"aow-linkd","version":"…","name":"…","listen":"127.0.0.1","port":2323,
//    "connections":1,"monitors":0,
//    "client":{"name":"…","address":"…","toClient":0,"toBus":0,"txDropped":0,"rxDropped":0} or null,
//    "channels":[{"interface":"can0","bitrate":250000,"state":0,"txErrors":0,"rxErrors":0}]}
// state is the AOW link's bus state: 0 error active, 1 warning, 2 passive, 3 bus-off, 4 down.
class Reporter {
public:
    explicit Reporter(const Options& o) {
        if (o.statusHost.empty()) return;
        to_.sin_family = AF_INET;
        to_.sin_port = htons(o.statusPort);
        if (inet_pton(AF_INET, o.statusHost.c_str(), &to_.sin_addr) != 1) {
            std::fprintf(stderr, "aow-linkd: --status %s is not an IPv4 address\n", o.statusHost.c_str());
            return;
        }
        fd_ = socket(AF_INET, SOCK_DGRAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    }
    ~Reporter() { if (fd_ >= 0) close(fd_); }

    void send(const aow::link::Gateway& g, const Options& o, const std::string& name) {
        if (fd_ < 0) return;
        const aow::link::Gateway::ControlInfo c = g.control();
        std::string j = "{\"gateway\":\"aow-linkd\",\"version\":\"" + jsonText(kVersion) + "\",\"name\":\"" + jsonText(name) +
            "\",\"listen\":\"" + jsonText(o.listen) + "\",\"port\":" + std::to_string(o.port) +
            ",\"connections\":" + std::to_string(g.connections()) + ",\"monitors\":" + std::to_string(g.monitors()) + ",\"client\":";
        if (c.connected)
            j += "{\"name\":\"" + jsonText(c.name) + "\",\"address\":\"" + jsonText(c.peer) + "\",\"toClient\":" +
                std::to_string(c.counters.toClient) + ",\"toBus\":" + std::to_string(c.counters.toBus) +
                ",\"txDropped\":" + std::to_string(c.txDropped) + ",\"rxDropped\":" + std::to_string(c.rxDropped) + "}";
        else
            j += "null";
        j += ",\"channels\":[";
        bool first = true;
        for (const aow::link::Channel& ch : g.channels()) {
            j += (first ? "" : ",") + std::string("{\"interface\":\"") + jsonText(ch.name) + "\",\"bitrate\":" +
                std::to_string(ch.bitrate) + ",\"state\":" + std::to_string(ch.state) + ",\"txErrors\":" +
                std::to_string(ch.txErrors) + ",\"rxErrors\":" + std::to_string(ch.rxErrors) + "}";
            first = false;
        }
        j += "]}";
        sendto(fd_, j.data(), j.size(), MSG_DONTWAIT, reinterpret_cast<const sockaddr*>(&to_), sizeof to_);
    }

private:
    int fd_ = -1;
    sockaddr_in to_{};
};

}  // namespace

int main(int argc, char** argv) {
    setvbuf(stdout, nullptr, _IOLBF, 0);  // a line at a time to the journal
    const Options opt = parse(argc, argv);
    std::signal(SIGPIPE, SIG_IGN);
    struct sigaction sa{};
    sa.sa_handler = [](int) { g_stop = 1; };
    sigaction(SIGTERM, &sa, nullptr);
    sigaction(SIGINT, &sa, nullptr);

    std::deque<CanChannel> channels;  // not moved once made: each owns its socket
    for (size_t i = 0; i < opt.can.size(); ++i) channels.emplace_back(static_cast<uint8_t>(i), opt.can[i]);
    LinkInfo nl;
    std::vector<aow::link::Channel> infos;
    for (CanChannel& c : channels) {
        c.refresh(nl);
        infos.push_back(c.info());
    }

    std::string name = opt.name;
    if (name.empty()) {
        char host[64] = {};
        gethostname(host, sizeof host - 1);
        name = std::string(host);
        for (size_t i = 0; i < opt.can.size(); ++i) name += (i ? "," : " ") + opt.can[i];
    }
    CanBus bus(channels);
    aow::link::Gateway gateway(name, infos, bus, [](const std::string& l) { log("aow-linkd: " + l); });

    const int listener = listenOn(opt);
    if (listener < 0) return 1;
    std::string ifaces;
    for (size_t i = 0; i < opt.can.size(); ++i) ifaces += (i ? "," : "") + opt.can[i];
    log("aow-linkd " + std::string(kVersion) + ": " + ifaces + " <-> tcp " + opt.listen + ":" + std::to_string(opt.port) +
        ", AOW link version " + std::to_string(aow::link::kVersion));
    notify("READY=1");
    Reporter reporter(opt);
    bool reportedControl = false;

    std::map<int, int> fds;  // client id -> socket
    int nextId = 1;
    uint64_t nextRefresh = nowMs() + kRefreshMs, nextPing = 0;
    std::vector<pollfd> pfds;
    std::vector<int> ids;  // the client id of each pollfd after the listener and channels

    auto closeClients = [&] {
        for (auto& [id, last] : gateway.takeClosing()) {
            auto it = fds.find(id);
            if (it == fds.end()) continue;
            closeHard(it->second, last);
            fds.erase(it);
        }
    };

    while (!g_stop) {
        pfds.clear();
        ids.clear();
        pfds.push_back({listener, POLLIN, 0});
        for (CanChannel& c : channels) pfds.push_back({c.fd(), POLLIN, 0});  // -1 is ignored by poll
        for (auto& [id, fd] : fds) {
            const std::string* out = gateway.output(id);
            pfds.push_back({fd, static_cast<short>(POLLIN | (out && !out->empty() ? POLLOUT : 0)), 0});
            ids.push_back(id);
        }
        const int n = poll(pfds.data(), pfds.size(), gateway.txPending() ? kBusyTickMs : kTickMs);
        if (n < 0 && errno != EINTR) {
            log(std::string("aow-linkd: poll: ") + std::strerror(errno));
            break;
        }
        const uint64_t now = nowMs();

        if (pfds[0].revents & POLLIN) {
            sockaddr_in peer{};
            socklen_t len = sizeof peer;
            const int fd = accept4(listener, reinterpret_cast<sockaddr*>(&peer), &len, SOCK_NONBLOCK | SOCK_CLOEXEC);
            if (fd >= 0) {
                const int one = 1;
                setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
                char text[INET_ADDRSTRLEN] = "?";
                inet_ntop(AF_INET, &peer.sin_addr, text, sizeof text);
                const int id = nextId++;
                fds[id] = fd;
                gateway.accepted(id, text, now);
            }
        }
        for (size_t i = 0; i < channels.size(); ++i)
            if (pfds[1 + i].revents & POLLIN) channels[i].read(gateway);

        const size_t base = 1 + channels.size();
        for (size_t i = 0; i < ids.size(); ++i) {
            if (!(pfds[base + i].revents & (POLLIN | POLLHUP | POLLERR))) continue;
            const int id = ids[i];
            if (!gateway.output(id)) continue;  // dropped earlier in this pass
            uint8_t buf[65536];
            const ssize_t got = recv(fds[id], buf, sizeof buf, MSG_DONTWAIT);
            if (got > 0) gateway.received(id, buf, static_cast<size_t>(got), now);
            else if (got == 0) gateway.lost(id, "closed by the client");
            else if (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)
                gateway.lost(id, std::string("error ") + std::strerror(errno));
        }

        if (now >= nextRefresh) {
            nextRefresh = now + kRefreshMs;
            for (size_t i = 0; i < channels.size(); ++i) {
                channels[i].refresh(nl);
                gateway.setChannel(static_cast<uint8_t>(i), channels[i].info());
            }
            reporter.send(gateway, opt, name);
            reportedControl = gateway.hasControl();
        } else {
            // The overflow count comes with each read; the state waits for the refresh.
            for (size_t i = 0; i < channels.size(); ++i) {
                aow::link::Channel c = gateway.channels()[i];
                c.overflow = channels[i].info().overflow;
                gateway.setChannel(static_cast<uint8_t>(i), c);
            }
        }
        gateway.tick(now);

        for (auto& [id, fd] : fds) {
            std::string* out = gateway.output(id);
            if (!out || out->empty()) continue;
            const ssize_t sent = send(fd, out->data(), out->size(), MSG_NOSIGNAL | MSG_DONTWAIT);
            if (sent > 0) out->erase(0, static_cast<size_t>(sent));
            if (sent < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)
                gateway.lost(id, std::string("send failed: ") + std::strerror(errno));
            else if (out->size() > kMaxBehind)
                gateway.lost(id, "not reading (more than 1 MB behind)");
        }
        closeClients();
        if (gateway.hasControl() != reportedControl) {  // a terminal came or went: say so now
            reporter.send(gateway, opt, name);
            reportedControl = gateway.hasControl();
        }
        if (now >= nextPing) {
            nextPing = now + kPingMs;
            notify("WATCHDOG=1");
        }
    }

    gateway.shutdown();
    closeClients();
    reporter.send(gateway, opt, name);
    close(listener);
    log("aow-linkd: stopped");
    return 0;
}
