// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors
//
// The AOW link gateway on an ESP32 (gateway/esp32/README.md): every frame of the ESP32's CAN
// controller (TWAI, through a 3.3 V transceiver) to a terminal over TCP, and the terminal's frames
// to the bus. The protocol is docs/TRANSPORT.md section 5, in ../linux/aow_link.cpp as aow-linkd
// has it; this file is the sockets, the bus, the Wi-Fi and the settings around it.
//
// The serial port (115200) takes one command a line, one line back; lines starting with '#' are
// the gateway's log, for a reader to skip:
//
//   ?                          i2c-bridge 1 (the I2C bridge below answers to its old name)
//   r <addr> <reg>             a register of an I2C device on SDA/SCL, two hex digits, or e
//   s                          the I2C addresses that answer
//   status                     one line: Wi-Fi, address, terminal, bus state and counters
//   name <text>                the name terminals show
//   ap <ssid> <password>       run its own access point (the default; password 8 to 63 characters)
//   sta <ssid> <password>      join a network instead (a machine network, never an uplink)
//   restart
//
// The I2C bridge is for a joystick module on the same board (isobus-implement-sim's aux_input
// --joystick-serial); it is no part of the gateway, which knows nothing of ISOBUS. It answers on
// TCP port 2324 as well (--joystick-tcp), one line in, one line out, for ?, r, s and status only:
// the settings are changed on the serial port alone.
#include <Arduino.h>
#include <ESPmDNS.h>
#include <Preferences.h>
#include <WiFi.h>
#include <Wire.h>

#include <driver/twai.h>
#include <esp_timer.h>
#include <lwip/sockets.h>

#include <map>
#include <string>

#include "aow_link.h"

namespace {

// Pins. The relay board's relays are on 12, 13, 14, 25, 26, 27, 32 and 33.
constexpr gpio_num_t kCanTx = GPIO_NUM_17;  // to the transceiver's D (CTX on a VP230 breakout)
constexpr gpio_num_t kCanRx = GPIO_NUM_16;  // from its R (CRX)
constexpr int kSda = 21, kScl = 22;
constexpr uint32_t kBitrate = 250000;  // ISO 11783

constexpr uint32_t kRefreshMs = 100;      // the bus state read this often
constexpr size_t kMaxBehind = 32 * 1024;  // a client this far behind is not reading: about 1 s of a full bus
constexpr int kMaxClients = 6;            // a terminal, four monitors, and one to be told busy
constexpr uint16_t kBridgePort = 2324;    // the I2C bridge
constexpr int kMaxBridgeClients = 2;
constexpr uint32_t kJoinRetryMs = 10000;  // a station not joined this long starts again

Preferences prefs;
std::string gatewayName;
bool stationMode = false;
uint64_t joinStartedMs = 0;
String joinSsid, joinPass;
std::string line;

uint64_t nowMs() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }

void note(const std::string& text) {
    Serial.print("# ");
    Serial.print(text.c_str());
    Serial.print("\n");
}

// The ESP32's CAN controller as the gateway's one channel.
class TwaiBus : public aow::link::Bus {
public:
    bool start() {
        twai_general_config_t g = TWAI_GENERAL_CONFIG_DEFAULT(kCanTx, kCanRx, TWAI_MODE_NORMAL);
        g.rx_queue_len = 128;
        g.tx_queue_len = 16;
        const twai_timing_config_t t = TWAI_TIMING_CONFIG_250KBITS();
        const twai_filter_config_t f = TWAI_FILTER_CONFIG_ACCEPT_ALL();
        if (twai_driver_install(&g, &t, &f) != ESP_OK || twai_start() != ESP_OK) return false;
        running_ = true;
        return true;
    }

    Write write(const aow::link::Frame& f) override {
        twai_message_t m = {};
        m.extd = f.extended;
        m.identifier = f.id;
        m.data_length_code = f.len;
        memcpy(m.data, f.data, f.len);
        // Not running (bus-off, recovering) counts as full: the gateway drops it after 100 ms
        return twai_transmit(&m, 0) == ESP_OK ? Write::Sent : Write::Full;
    }

    void drain(uint8_t) override { twai_clear_receive_queue(); }

    // Every frame waiting, to the gateway, stamped as read (the driver keeps no receive time)
    void read(aow::link::Gateway& g) {
        twai_message_t m;
        while (twai_receive(&m, 0) == ESP_OK) {
            if (m.rtr) continue;
            aow::link::Frame f;
            f.extended = m.extd;
            f.id = m.identifier;
            f.timeUs = static_cast<uint32_t>(esp_timer_get_time());
            f.len = m.data_length_code > 8 ? 8 : m.data_length_code;
            memcpy(f.data, m.data, f.len);
            g.fromBus(f);
        }
    }

    // The state as STATUS has it, and a bus-off brought back
    aow::link::Channel info() {
        aow::link::Channel c;
        c.name = "CAN";
        c.bitrate = kBitrate;
        twai_status_info_t s;
        if (!running_ || twai_get_status_info(&s) != ESP_OK) {
            c.state = aow::link::Down;
            return c;
        }
        c.txErrors = static_cast<uint8_t>(s.tx_error_counter > 255 ? 255 : s.tx_error_counter);
        c.rxErrors = static_cast<uint8_t>(s.rx_error_counter > 255 ? 255 : s.rx_error_counter);
        c.overflow = s.rx_missed_count;
        const uint32_t worst = s.tx_error_counter > s.rx_error_counter ? s.tx_error_counter : s.rx_error_counter;
        switch (s.state) {
        case TWAI_STATE_RUNNING:
            c.state = worst >= 128 ? aow::link::ErrorPassive : worst >= 96 ? aow::link::ErrorWarning : aow::link::ErrorActive;
            break;
        case TWAI_STATE_BUS_OFF:
            c.state = aow::link::BusOff;
            twai_initiate_recovery();
            break;
        case TWAI_STATE_STOPPED:  // recovered: back on the bus
            c.state = aow::link::BusOff;
            twai_start();
            break;
        default:  // recovering
            c.state = aow::link::BusOff;
            break;
        }
        return c;
    }

private:
    bool running_ = false;
};

TwaiBus bus;
aow::link::Gateway* gateway = nullptr;
int listener = -1;
int bridgeListener = -1;
std::map<int, std::string> bridgeClients;  // socket -> its unfinished line
std::map<int, int> fds;  // client id -> socket
int nextId = 1;
uint64_t nextRefresh = 0;

// The last words, then a close that keeps nothing unsent
void closeHard(int fd, const std::string& last) {
    if (!last.empty()) send(fd, last.data(), last.size(), MSG_DONTWAIT);
    linger l = {1, 0};
    setsockopt(fd, SOL_SOCKET, SO_LINGER, &l, sizeof l);
    close(fd);
}

void closeClients() {
    for (auto& [id, last] : gateway->takeClosing()) {
        auto it = fds.find(id);
        if (it == fds.end()) continue;
        closeHard(it->second, last);
        fds.erase(it);
    }
}

int listenOn(uint16_t port) {
    const int fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (fd < 0) return -1;
    const int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
    sockaddr_in a = {};
    a.sin_family = AF_INET;
    a.sin_port = htons(port);
    a.sin_addr.s_addr = htonl(INADDR_ANY);  // its own access point, or the machine network it joined
    if (bind(fd, reinterpret_cast<sockaddr*>(&a), sizeof a) < 0 || ::listen(fd, 2) < 0) {
        close(fd);
        return -1;
    }
    fcntl(fd, F_SETFL, O_NONBLOCK);
    return fd;
}

// The network, from the settings: its own access point unless told to join one
void startWifi() {
    uint8_t mac[6];
    WiFi.macAddress(mac);
    char tail[8];
    snprintf(tail, sizeof tail, "%02X%02X", mac[4], mac[5]);
    gatewayName = prefs.getString("name", (std::string("ISOBUS gateway ") + tail).c_str()).c_str();
    const String mode = prefs.getString("mode", "ap");
    const String ssid = prefs.getString("ssid", (std::string("AOW gateway ") + tail).c_str());
    const String pass = prefs.getString("pass", (std::string("aowlink-") + tail).c_str());
    WiFi.persistent(false);
    WiFi.setSleep(false);  // power save adds tens of milliseconds to every frame
    stationMode = mode == "sta";
    if (stationMode) {
        WiFi.mode(WIFI_STA);
        WiFi.setAutoReconnect(true);
        joinSsid = ssid;
        joinPass = pass;
        WiFi.begin(joinSsid.c_str(), joinPass.c_str());
        joinStartedMs = nowMs();
        note("joining " + std::string(ssid.c_str()));
    } else {
        WiFi.mode(WIFI_AP);
        WiFi.softAP(ssid.c_str(), pass.c_str(), 6, 0, 4);
        note("access point " + std::string(ssid.c_str()) + " at " + WiFi.softAPIP().toString().c_str());
    }
    char host[32];
    snprintf(host, sizeof host, "aow-gateway-%s", tail);
    for (char* p = host; *p; ++p) *p = static_cast<char>(tolower(*p));
    if (MDNS.begin(host)) {
        MDNS.setInstanceName(gatewayName.c_str());
        MDNS.addService("aowlink", "tcp", aow::link::kDefaultPort);
        MDNS.addServiceTxt("aowlink", "tcp", "v", "1");
        MDNS.addServiceTxt("aowlink", "tcp", "i2c", String(kBridgePort));
    }
}

std::string status() {
    const aow::link::Channel c = gateway->channels()[0];
    const auto control = gateway->control();
    static const char* states[] = {"error active", "error warning", "error passive", "bus-off", "down"};
    char text[256];
    snprintf(text, sizeof text, "%s %s, %s, terminal %s, monitors %u, bus %s tx %u rx %u, missed %u, to client %llu, to bus %llu",
             stationMode ? "station" : "access point",
             stationMode ? (WiFi.isConnected() ? WiFi.localIP().toString().c_str() : "not connected") : WiFi.softAPIP().toString().c_str(),
             gatewayName.c_str(), control.connected ? (control.name.empty() ? control.peer.c_str() : control.name.c_str()) : "none",
             static_cast<unsigned>(gateway->monitors()), states[c.state > 4 ? 4 : c.state], c.txErrors, c.rxErrors,
             static_cast<unsigned>(c.overflow), static_cast<unsigned long long>(control.counters.toClient),
             static_cast<unsigned long long>(control.counters.toBus));
    return text;
}

// One command's answer, without its line end. The settings only from the serial port (local).
std::string answer(const std::string& text, bool local) {
    unsigned address = 0, reg = 0;
    char a[80] = {}, b[80] = {};
    char hex[8];
    if (text == "?") return "i2c-bridge 1";
    if (sscanf(text.c_str(), "r %x %x", &address, &reg) == 2 && address < 0x80 && reg < 0x100) {
        // An SMBus read byte data: the register written, then a byte after a repeated start
        Wire.beginTransmission(static_cast<uint8_t>(address));
        Wire.write(static_cast<uint8_t>(reg));
        if (Wire.endTransmission(false) != 0 || Wire.requestFrom(static_cast<uint8_t>(address), static_cast<uint8_t>(1)) != 1)
            return "e";
        snprintf(hex, sizeof hex, "%02x", Wire.read());
        return hex;
    }
    if (text == "s") {
        std::string found;
        for (uint8_t at = 0x08; at < 0x78; ++at) {
            Wire.beginTransmission(at);
            if (Wire.endTransmission() == 0) {
                snprintf(hex, sizeof hex, "%02x ", at);
                found += hex;
            }
        }
        return found;
    }
    if (text == "status") return status();
    if (!local) return "e";
    if (text.rfind("name ", 0) == 0 && text.size() > 5) {
        prefs.putString("name", text.substr(5).c_str());
        return "ok, after a restart";
    }
    if ((sscanf(text.c_str(), "ap %79s %79s", a, b) == 2 || sscanf(text.c_str(), "sta %79s %79s", a, b) == 2) && strlen(b) >= 8) {
        prefs.putString("mode", text[0] == 'a' ? "ap" : "sta");
        prefs.putString("ssid", a);
        prefs.putString("pass", b);
        return "ok, after a restart";
    }
    if (text == "restart") {
        Serial.print("ok\n");
        Serial.flush();
        ESP.restart();
    }
    return "e";
}

void command(const std::string& text) { Serial.print((answer(text, true) + "\n").c_str()); }

// The bridge's TCP clients: their lines answered in order, all at once
void serveBridge(fd_set& rd) {
    if (bridgeListener >= 0 && FD_ISSET(bridgeListener, &rd)) {
        const int fd = accept(bridgeListener, nullptr, nullptr);
        if (fd >= 0 && static_cast<int>(bridgeClients.size()) >= kMaxBridgeClients) {
            close(fd);
        } else if (fd >= 0) {
            const int one = 1;
            setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
            fcntl(fd, F_SETFL, O_NONBLOCK);
            bridgeClients[fd] = "";
        }
    }
    for (auto it = bridgeClients.begin(); it != bridgeClients.end();) {
        const int fd = it->first;
        if (!FD_ISSET(fd, &rd)) {
            ++it;
            continue;
        }
        char buf[256];
        const int got = recv(fd, buf, sizeof buf, MSG_DONTWAIT);
        if (got == 0 || (got < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)) {
            close(fd);
            it = bridgeClients.erase(it);
            continue;
        }
        std::string out;
        for (int i = 0; i < got; ++i) {
            const char c = buf[i];
            if (c == '\n' || c == '\r') {
                if (!it->second.empty()) out += answer(it->second, false) + "\n";
                it->second.clear();
            } else if (it->second.size() < 200) {
                it->second += c;
            }
        }
        if (!out.empty()) send(fd, out.data(), out.size(), MSG_DONTWAIT);  // a few bytes: the socket takes them
        ++it;
    }
}

void readSerial() {
    while (Serial.available()) {
        const char c = static_cast<char>(Serial.read());
        if (c == '\n' || c == '\r') {
            if (!line.empty()) command(line);
            line.clear();
        } else if (line.size() < 200) {
            line += c;
        }
    }
}

// One pass: new clients, their bytes, the bus, the timers, what each is owed
void serve() {
    fd_set rd, wr;
    FD_ZERO(&rd);
    FD_ZERO(&wr);
    int top = listener > bridgeListener ? listener : bridgeListener;
    if (listener >= 0) FD_SET(listener, &rd);
    if (bridgeListener >= 0) FD_SET(bridgeListener, &rd);
    for (auto& [fd, pending] : bridgeClients) {
        FD_SET(fd, &rd);
        if (fd > top) top = fd;
    }
    for (auto& [id, fd] : fds) {
        FD_SET(fd, &rd);
        const std::string* out = gateway->output(id);
        if (out && !out->empty()) FD_SET(fd, &wr);
        if (fd > top) top = fd;
    }
    timeval wait = {0, 1000};  // the bus is polled between, so never long
    if (select(top + 1, &rd, &wr, nullptr, &wait) < 0) return;
    const uint64_t now = nowMs();
    serveBridge(rd);

    if (listener >= 0 && FD_ISSET(listener, &rd)) {
        sockaddr_in peer = {};
        socklen_t len = sizeof peer;
        const int fd = accept(listener, reinterpret_cast<sockaddr*>(&peer), &len);
        if (fd >= 0 && static_cast<int>(fds.size()) >= kMaxClients) {
            close(fd);
        } else if (fd >= 0) {
            const int one = 1;
            setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
            fcntl(fd, F_SETFL, O_NONBLOCK);
            char text[16] = "?";
            inet_ntoa_r(peer.sin_addr, text, sizeof text);
            const int id = nextId++;
            fds[id] = fd;
            gateway->accepted(id, text, now);
        }
    }
    for (auto& [id, fd] : fds) {
        if (!FD_ISSET(fd, &rd) || !gateway->output(id)) continue;
        uint8_t buf[1024];
        const int got = recv(fd, buf, sizeof buf, MSG_DONTWAIT);
        if (got > 0)
            gateway->received(id, buf, static_cast<size_t>(got), now);
        else if (got == 0)
            gateway->lost(id, "closed by the client");
        else if (errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)
            gateway->lost(id, std::string("error ") + strerror(errno));
    }

    bus.read(*gateway);
    if (now >= nextRefresh) {
        nextRefresh = now + kRefreshMs;
        gateway->setChannel(0, bus.info());
    }
    gateway->tick(now);

    for (auto& [id, fd] : fds) {
        std::string* out = gateway->output(id);
        if (!out || out->empty()) continue;
        const int sent = send(fd, out->data(), out->size(), MSG_DONTWAIT);
        if (sent > 0) out->erase(0, static_cast<size_t>(sent));
        if (sent < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR)
            gateway->lost(id, std::string("send failed: ") + strerror(errno));
        else if (out->size() > kMaxBehind)
            gateway->lost(id, "not reading (more than 32 kB behind)");
    }
    closeClients();
}

}  // namespace

void setup() {
    Serial.begin(115200);
    Wire.begin(kSda, kScl, 100000);
    prefs.begin("aowlink");
    startWifi();
    if (!bus.start()) note("CAN controller did not start");
    static aow::link::Gateway g(gatewayName, {bus.info()}, bus, [](const std::string& l) { note(l); });
    gateway = &g;
    listener = listenOn(aow::link::kDefaultPort);
    if (listener < 0) note("cannot listen on port 2323");
    bridgeListener = listenOn(kBridgePort);
    note("AOW link gateway \"" + gatewayName + "\", CAN " + std::to_string(kBitrate / 1000) + " kbit/s, tcp port " +
         std::to_string(aow::link::kDefaultPort));
    Serial.print("i2c-bridge 1\n");  // as the bridge did at start, for a reader waiting on a reset
}

// Auto-reconnect covers a network lost after joining, not a first join that failed, which
// happens after a cold start: try again from the start
void keepJoining() {
    if (!stationMode || WiFi.isConnected()) {
        joinStartedMs = nowMs();
        return;
    }
    if (nowMs() - joinStartedMs < kJoinRetryMs) return;
    note("not joined after 10 s, trying again");
    WiFi.disconnect();
    WiFi.begin(joinSsid.c_str(), joinPass.c_str());
    joinStartedMs = nowMs();
}

void loop() {
    keepJoining();
    readSerial();
    serve();
}
