// See isoshim.h. Built on AgIsoStack++'s isobus and utility libraries only: this file provides the
// stack's hardware hooks (can_hardware_abstraction.hpp) itself, so HardwareIntegration and its
// threads stay out.
#include "isoshim.h"

#include <cstdio>
#include <cstdlib>
#include <deque>
#include <memory>
#include <mutex>
#include <set>
#include <vector>

#include "isobus/isobus/can_hardware_abstraction.hpp"
#include "isobus/isobus/can_network_manager.hpp"
#include "isobus/isobus/can_partnered_control_function.hpp"
#include "isobus/isobus/can_stack_logger.hpp"
#include "isobus/isobus/isobus_virtual_terminal_client.hpp"
#include "isobus/isobus/isobus_virtual_terminal_objects.hpp"
#include "isobus/isobus/isobus_virtual_terminal_server.hpp"
#include "isobus/isobus/isobus_virtual_terminal_server_managed_working_set.hpp"
#include "isobus/utility/iop_file_interface.hpp"
#include "isobus/utility/system_timing.hpp"

namespace {

isoshim_send_fn g_send = nullptr;
isoshim_event_fn g_event = nullptr;
isoshim_log_fn g_logFn = nullptr;
int g_channels = 0;

// Frames in: any thread pushes, the step thread drains.
std::mutex g_rxMutex;
std::deque<isobus::CANMessageFrame> g_rx;
// Frames out: the stack sends from the step thread only, and they leave at the end of the step.
std::vector<isobus::CANMessageFrame> g_tx;

void flushTransmit() {
	while (!g_tx.empty()) {
		std::vector<isobus::CANMessageFrame> batch;
		batch.swap(g_tx);
		for (const auto &frame : batch) {
			if (g_send) g_send(frame.channel, frame.identifier, frame.data, frame.dataLength, frame.isExtendedFrame ? 1 : 0);
			// The stack learns of its own frames on the bus this way (address claim, busload).
			isobus::on_transmit_can_message_frame_from_hardware(frame);
		}
	}
}

class Logger : public isobus::CANStackLogger {
public:
	void sink_CAN_stack_log(LoggingLevel level, const std::string &text) override {
		if (g_logFn)
			g_logFn(static_cast<int32_t>(level), text.c_str());
		else
			std::fprintf(stderr, "[stack %d] %s\n", static_cast<int>(level), text.c_str());
	}
};
Logger g_logger;

using WorkingSet = isobus::VirtualTerminalServerManagedWorkingSet;

std::int32_t addressOf(const std::shared_ptr<WorkingSet> &ws) {
	const auto cf = ws ? ws->get_control_function() : nullptr;
	return cf ? cf->get_address() : -1;
}

// A minimal terminal: nothing stored, any object. The implement under test (by NAME function code)
// is made the active working set when its pool is parsed.
class Terminal : public isobus::VirtualTerminalServer {
public:
	Terminal(std::shared_ptr<isobus::InternalControlFunction> cf, std::uint16_t wanted)
	    : VirtualTerminalServer(std::move(cf)), wanted_(wanted) {}

	// A pool can still be parsing on its own thread; the working set that owns the thread must not
	// be destroyed (by the base class, after this) until it has finished. Upstream's working set
	// does not join it itself (AgIsoStack++ #802).
	~Terminal() {
		for (const auto &ws : managedWorkingSetList) ws->join_parsing_thread();
	}

	bool get_is_enough_memory(std::uint32_t) const override { return true; }
	VTVersion get_version() const override { return VTVersion::Version5; }
	std::uint8_t get_number_of_navigation_soft_keys() const override { return 0; }
	std::uint8_t get_soft_key_descriptor_x_pixel_width() const override { return 80; }
	std::uint8_t get_soft_key_descriptor_y_pixel_height() const override { return 80; }
	std::uint8_t get_number_of_possible_virtual_soft_keys_in_soft_key_mask() const override { return 64; }
	std::uint8_t get_number_of_physical_soft_keys() const override { return 6; }
	std::uint16_t get_data_mask_area_size_x_pixels() const override { return 480; }
	std::uint16_t get_data_mask_area_size_y_pixels() const override { return 480; }
	void suspend_working_set(std::shared_ptr<WorkingSet>) override {}
	SupportedWideCharsErrorCode get_supported_wide_chars(std::uint8_t, std::uint16_t, std::uint16_t, std::uint8_t &ranges,
	                                                     std::vector<std::uint8_t> &) override {
		ranges = 0;
		return SupportedWideCharsErrorCode::AnyOtherError;
	}
	std::vector<std::array<std::uint8_t, 7>> get_versions(isobus::NAME) override { return {}; }
	std::vector<std::uint8_t> get_supported_objects() const override {
		std::vector<std::uint8_t> all;
		for (int i = 0; i <= 47; ++i) all.push_back(static_cast<std::uint8_t>(i));
		return all;
	}
	std::vector<std::uint8_t> load_version(const std::vector<std::uint8_t> &, isobus::NAME) override { return {}; }
	bool save_version(const std::vector<std::uint8_t> &, const std::vector<std::uint8_t> &, isobus::NAME) override { return true; }
	bool delete_version(const std::vector<std::uint8_t> &, isobus::NAME) override { return true; }
	std::vector<std::array<std::uint8_t, 32>> get_extended_versions(isobus::NAME) override { return {}; }
	bool delete_all_versions(std::uint8_t, isobus::NAME) override { return true; }
	bool delete_object_pool(isobus::NAME) override { return true; }

	void object_pool_parse_finished(std::shared_ptr<WorkingSet> ws, bool success) override {
		const auto cf = ws->get_control_function();
		const bool wanted = wanted_ == 0xFFFF || (cf && cf->get_NAME().get_function_code() == wanted_);
		if (!success) {
			g_event(wanted ? ISOSHIM_POOL_BAD : ISOSHIM_OTHER_POOL, ws->get_object_pool_faulting_object_id(), addressOf(ws));
			return;
		}
		const auto object = std::static_pointer_cast<isobus::WorkingSet>(ws->get_working_set_object());
		if (!wanted || !object || (underTest_ && underTest_ != ws)) {
			g_event(ISOSHIM_OTHER_POOL, static_cast<int32_t>(ws->get_object_tree().size()), addressOf(ws));
			return;
		}
		underTest_ = ws;
		mask_ = 0xFFFF;
		activeWorkingSet = ws;
		activeWorkingSetMasterAddress = cf->get_address();
		activeWorkingSetDataMaskObjectID = object->get_active_mask();
		if (send_status_message()) statusMessageTimestamp_ms = isobus::SystemTiming::get_timestamp_ms();
		g_event(ISOSHIM_POOL_OK, static_cast<int32_t>(ws->get_object_tree().size()), addressOf(ws));
	}

	void handle_unexpected_working_set_shutdown(std::shared_ptr<WorkingSet> ws, UnexpectedShutdownReason reason) override {
		g_event(ISOSHIM_WS_SHUTDOWN, static_cast<int32_t>(reason), addressOf(ws));
		if (ws == underTest_) {
			underTest_.reset();
			mask_ = 0xFFFF;
		}
	}

	void step() {
		// A pool that arrived in this step's frames is parsing now, on its own thread (upstream's
		// transferred_object_pool_parse_start hook is never called, AgIsoStack++ #802, so it is
		// looked for here).
		for (const auto &ws : managedWorkingSetList) {
			const bool running = ws->get_object_pool_processing_state() == WorkingSet::ObjectPoolProcessingThreadState::Running;
			if (running && parsing_.insert(ws.get()).second) g_event(ISOSHIM_PARSE_START, 0, addressOf(ws));
			if (!running) parsing_.erase(ws.get());
		}
		update();
		if (!underTest_) return;
		const auto object = std::static_pointer_cast<isobus::WorkingSet>(underTest_->get_working_set_object());
		if (object && object->get_active_mask() != mask_) {
			mask_ = object->get_active_mask();
			g_event(ISOSHIM_MASK, mask_, 0);
		}
	}

	bool press(std::uint16_t id) {
		if (!underTest_) return false;
		const auto client = underTest_->get_control_function();
		return send_soft_key_activation_message(KeyActivationCode::ButtonPressedOrLatched, id, mask_, 1, client) &&
		       send_soft_key_activation_message(KeyActivationCode::ButtonUnlatchedOrReleased, id, mask_, 1, client);
	}

private:
	std::uint16_t wanted_;
	std::shared_ptr<WorkingSet> underTest_;
	std::uint16_t mask_ = 0xFFFF;
	std::set<const WorkingSet *> parsing_;
};

std::shared_ptr<isobus::InternalControlFunction> g_serverCf, g_clientCf;
std::unique_ptr<Terminal> g_terminal;
std::shared_ptr<isobus::VirtualTerminalClient> g_client;
std::shared_ptr<isobus::PartneredControlFunction> g_partner;
std::vector<std::uint8_t> g_pool;
bool g_serverReported = false, g_clientReported = false;

isobus::NAME makeName(isobus::NAME::Function function, std::uint32_t identity) {
	isobus::NAME name(0);
	name.set_arbitrary_address_capable(true);
	name.set_industry_group(2);
	name.set_function_code(static_cast<std::uint8_t>(function));
	name.set_identity_number(identity);
	name.set_manufacturer_code(1407);
	return name;
}

void startTerminal(std::uint16_t wanted) {
	g_serverCf = isobus::CANNetworkManager::CANNetwork.create_internal_control_function(
	  makeName(isobus::NAME::Function::VirtualTerminal, 3), 0, 0x26);
	g_terminal.reset(new Terminal(g_serverCf, wanted));
	g_terminal->initialize();
}

} // namespace

// The stack's hardware hooks: frames out are queued for the end of the step.
namespace isobus {
bool send_can_message_frame_to_hardware(const CANMessageFrame &frame) {
	if (frame.channel >= g_channels) return false;
	g_tx.push_back(frame);
	return true;
}
} // namespace isobus

extern "C" {

int32_t isoshim_version(void) { return 2; }

int32_t isoshim_start(isoshim_send_fn send, isoshim_event_fn event, const uint8_t *pool, int32_t poolLen) {
	if (!send || !event || !pool || poolLen <= 0 || g_terminal) return -1;
	g_send = send;
	g_event = event;
	g_pool.assign(pool, pool + poolLen);
	if (std::getenv("ISOSHIM_LOG")) {
		isobus::CANStackLogger::set_can_stack_logger_sink(&g_logger);
		isobus::CANStackLogger::set_log_level(isobus::CANStackLogger::LoggingLevel::Info);
	}
	g_channels = 2;
	startTerminal(0xFFFF);

	g_clientCf = isobus::CANNetworkManager::CANNetwork.create_internal_control_function(
	  makeName(isobus::NAME::Function::SteeringControl, 2), 1);
	const isobus::NAMEFilter vt(isobus::NAME::NAMEParameters::FunctionCode,
	                            static_cast<std::uint8_t>(isobus::NAME::Function::VirtualTerminal));
	g_partner = isobus::CANNetworkManager::CANNetwork.create_partnered_control_function(1, { vt });
	g_client = std::make_shared<isobus::VirtualTerminalClient>(g_partner, g_clientCf);
	g_client->set_object_pool(0, g_pool.data(), static_cast<std::uint32_t>(g_pool.size()),
	                          isobus::IOPFileInterface::hash_object_pool_to_version(g_pool));
	// The example's behaviour: the alarm soft key (5000) shows the alarm mask (2000).
	g_client->get_vt_soft_key_event_dispatcher().add_listener([](const isobus::VirtualTerminalClient::VTKeyEvent &e) {
		if (e.keyEvent == isobus::VirtualTerminalClient::KeyActivationCode::ButtonUnlatchedOrReleased && e.objectID == 5000)
			g_client->send_change_active_mask(0, 2000);
	});
	g_client->initialize(false); // updated from isoshim_step, not its own thread
	return 0;
}

int32_t isoshim_start_terminal(isoshim_send_fn send, isoshim_event_fn event, uint16_t wantedFunction) {
	if (!send || !event || g_terminal) return -1;
	g_send = send;
	g_event = event;
	g_channels = 1;
	startTerminal(wantedFunction);
	return 0;
}

void isoshim_set_log(isoshim_log_fn fn, int32_t level) {
	g_logFn = fn;
	isobus::CANStackLogger::set_can_stack_logger_sink(fn ? &g_logger : nullptr);
	isobus::CANStackLogger::set_log_level(static_cast<isobus::CANStackLogger::LoggingLevel>(level));
}

void isoshim_receive(int32_t channel, uint32_t id, const uint8_t *data, int32_t len, int32_t extended) {
	if (channel < 0 || channel > 1 || len < 0 || len > 8) return;
	isobus::CANMessageFrame frame{};
	frame.identifier = id;
	frame.channel = static_cast<std::uint8_t>(channel);
	frame.dataLength = static_cast<std::uint8_t>(len);
	frame.isExtendedFrame = extended != 0;
	frame.timestamp_us = isobus::SystemTiming::get_timestamp_us();
	for (int i = 0; i < len; ++i) frame.data[i] = data[i];
	std::lock_guard<std::mutex> lock(g_rxMutex);
	g_rx.push_back(frame);
}

void isoshim_step(void) {
	if (!g_terminal) return;
	std::deque<isobus::CANMessageFrame> in;
	{
		std::lock_guard<std::mutex> lock(g_rxMutex);
		in.swap(g_rx);
	}
	for (const auto &frame : in)
		if (frame.channel < g_channels) isobus::receive_can_message_frame_from_hardware(frame);

	isobus::periodic_update_from_hardware(); // the network manager: transport protocols, address claim
	if (g_client) g_client->update();
	g_terminal->step();
	flushTransmit();

	if (!g_serverReported && g_serverCf->get_address_valid()) {
		g_serverReported = true;
		g_event(ISOSHIM_SERVER_ADDRESS, g_serverCf->get_address(), 0);
	}
	if (!g_clientReported && g_clientCf && g_clientCf->get_address_valid()) {
		g_clientReported = true;
		g_event(ISOSHIM_CLIENT_ADDRESS, g_clientCf->get_address(), 0);
	}
}

int32_t isoshim_press_soft_key(uint16_t objectId) {
	const bool sent = g_terminal && g_terminal->press(objectId);
	flushTransmit();
	return sent ? 1 : 0;
}

// Everything goes while the network manager is still alive: left to the end of the process, these
// objects would unregister from one that has already been destroyed.
void isoshim_stop(void) {
	if (g_client) g_client->terminate();
	g_client.reset();
	g_terminal.reset(); // joins any parsing thread
	if (g_partner) isobus::CANNetworkManager::CANNetwork.deactivate_control_function(g_partner);
	if (g_clientCf) isobus::CANNetworkManager::CANNetwork.deactivate_control_function(g_clientCf);
	if (g_serverCf) isobus::CANNetworkManager::CANNetwork.deactivate_control_function(g_serverCf);
	g_partner.reset();
	g_clientCf.reset();
	g_serverCf.reset();
	g_tx.clear();
	{
		std::lock_guard<std::mutex> lock(g_rxMutex);
		g_rx.clear();
	}
	g_serverReported = g_clientReported = false;
	g_channels = 0;
	g_send = nullptr;
	g_event = nullptr;
}

void isoshim_set_packets_per_cts(uint8_t packets) {
	auto &config = isobus::CANNetworkManager::CANNetwork.get_configuration();
	config.set_number_of_packets_per_cts_message(packets);
	config.set_number_of_packets_per_dpo_message(packets);
}

} // extern "C"
