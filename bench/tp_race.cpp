// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors
//
// Stress test: does a TP/ETP transmit started from an application thread send its request-to-send
// twice, while the stack's own thread runs the network manager's update?
//
// Two internal control functions on vcan0 (two stack channels on the same vcan), A (0x80) sending
// destination-specific messages to B (0x81). The main thread sends, one message at a time, waiting
// for each to complete; the hardware interface's thread runs the update, as in any threaded use of
// the stack. A raw socket counts request-to-sends (TP.CM byte 0 = 16, ETP.CM byte 0 = 20) from A.
//
//   tp_race N SIZE      SIZE <= 1785: TP; above: ETP
//
// Built against a threaded stack build (SocketCAN), on Linux with vcan0 up, e.g. in AgOpenWeb-HAT's
// dev container against its build: g++ -std=c++17 -O2 -I<src>/isobus/include
// -I<src>/hardware_integration/include -I<src>/utility/include tp_race.cpp libHardwareIntegration.a
// libIsobus.a libUtility.a -lpthread. 2026-10-25, upstream e932041: about 1 in 200 TP sends doubled
// its RTS (docs/PLAN.md, upstream gaps).
#include <linux/can.h>
#include <linux/can/raw.h>
#include <net/if.h>
#include <sys/socket.h>
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>

#include "isobus/hardware_integration/can_hardware_interface.hpp"
#include "isobus/hardware_integration/socket_can_interface.hpp"
#include "isobus/isobus/can_network_manager.hpp"
#include "isobus/isobus/can_partnered_control_function.hpp"

using namespace isobus;

static std::atomic<int> completed{0}, failed{0}, received{0};
static std::atomic<int> rts{0}, rtsDoubled{0};
static std::atomic<bool> stop{false};

static void onTxComplete(std::uint32_t, std::uint32_t, std::shared_ptr<InternalControlFunction>, std::shared_ptr<ControlFunction>, bool ok, void*) {
    (ok ? completed : failed)++;
}

static void onRx(const CANMessage& m, void*) {
    if (m.get_identifier().get_parameter_group_number() == 0xEF00) received++;
}

static void sniff() {
    int s = socket(PF_CAN, SOCK_RAW, CAN_RAW);
    sockaddr_can a{};
    a.can_family = AF_CAN;
    a.can_ifindex = if_nametoindex("vcan0");
    bind(s, reinterpret_cast<sockaddr*>(&a), sizeof a);
    timeval tv{0, 100000};
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
    can_frame last{};
    while (!stop) {
        can_frame f;
        if (read(s, &f, sizeof f) != sizeof f) continue;
        const std::uint32_t pgn = (f.can_id >> 8) & 0x3FF00;
        const bool isRts = (f.can_id & 0xFF) == 0x80 && ((pgn == 0xEC00 && f.data[0] == 16) || (pgn == 0xC800 && f.data[0] == 20));
        if (isRts) {
            rts++;
            if (last.can_id == f.can_id && !std::memcmp(last.data, f.data, 8)) rtsDoubled++;
        }
        last = f;
    }
    close(s);
}

int main(int argc, char** argv) {
    const int n = argc > 1 ? std::atoi(argv[1]) : 1000;
    const std::size_t size = argc > 2 ? std::strtoul(argv[2], nullptr, 10) : 100;

    CANHardwareInterface::set_number_of_can_channels(2);
    CANHardwareInterface::assign_can_channel_frame_handler(0, std::make_shared<SocketCANInterface>("vcan0"));
    CANHardwareInterface::assign_can_channel_frame_handler(1, std::make_shared<SocketCANInterface>("vcan0"));
    if (!CANHardwareInterface::start()) { std::printf("cannot start\n"); return 1; }
    std::thread sniffer(sniff);

    NAME na(0), nb(0);
    na.set_arbitrary_address_capable(true); na.set_identity_number(1); na.set_function_code(130); na.set_industry_group(2);
    nb.set_arbitrary_address_capable(true); nb.set_identity_number(2); nb.set_function_code(29); nb.set_industry_group(2);
    auto a = CANNetworkManager::CANNetwork.create_internal_control_function(na, 0, 0x80);
    auto b = CANNetworkManager::CANNetwork.create_internal_control_function(nb, 1, 0x81);
    auto pb = CANNetworkManager::CANNetwork.create_partnered_control_function(0, {NAMEFilter(NAME::NAMEParameters::FunctionCode, 29)});
    CANNetworkManager::CANNetwork.add_any_control_function_parameter_group_number_callback(0xEF00, onRx, nullptr);
    for (int i = 0; i < 100 && !(a->get_address_valid() && b->get_address_valid() && pb->get_address_valid()); ++i)
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    if (!pb->get_address_valid()) { std::printf("no partner\n"); return 1; }

    std::vector<std::uint8_t> data(size, 0x55);
    int sent = 0;
    const auto t0 = std::chrono::steady_clock::now();
    for (int i = 0; i < n; ++i) {
        const int before = completed + failed;
        data[0] = static_cast<std::uint8_t>(i);
        if (!CANNetworkManager::CANNetwork.send_can_message(0xEF00, data.data(), static_cast<std::uint32_t>(data.size()), a, pb,
                                                            CANIdentifier::CANPriority::PriorityDefault6, onTxComplete)) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
            continue;
        }
        ++sent;
        for (int w = 0; w < 4000 && completed + failed == before; ++w) std::this_thread::sleep_for(std::chrono::microseconds(500));
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(300));
    stop = true;
    sniffer.join();
    CANHardwareInterface::stop();
    const double s = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    std::printf("%zu bytes (%s): %d sent, %d completed, %d failed, %d received; RTS %d, back-to-back duplicates %d (%.1f s)\n",
                size, size > 1785 ? "ETP" : "TP", sent, completed.load(), failed.load(), received.load(), rts.load(), rtsDoubled.load(), s);
    return 0;
}
