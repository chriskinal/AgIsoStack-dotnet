// See isoshim.h. Built on AgIsoStack++'s isobus and utility libraries only: this file provides the
// stack's hardware hooks (can_hardware_abstraction.hpp) itself, so HardwareIntegration and its
// threads stay out.
#include "isoshim.h"
#include "render.hpp"

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

isoshim_host g_host{};
isoshim_vt_config g_config{};
isoshim_log_fn g_logFn = nullptr;
int g_channels = 0;

// Frames in: any thread pushes, the step thread drains.
std::mutex g_rxMutex;
std::deque<isobus::CANMessageFrame> g_rx;
// Frames out: the stack sends from the step thread only, and they leave at the end of the step.
std::vector<isobus::CANMessageFrame> g_tx;

// What a load or list callback hands back through isoshim_reply.
std::vector<std::vector<std::uint8_t>> g_replies;

void flushTransmit() {
	while (!g_tx.empty()) {
		std::vector<isobus::CANMessageFrame> batch;
		batch.swap(g_tx);
		for (const auto &frame : batch) {
			g_host.send(frame.channel, frame.identifier, frame.data, frame.dataLength, frame.isExtendedFrame ? 1 : 0);
			// The stack learns of its own frames on the bus this way (address claim, busload).
			isobus::on_transmit_can_message_frame_from_hardware(frame);
		}
	}
}

void message(const std::string &json) { g_host.message(json.c_str(), static_cast<int32_t>(json.size())); }

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
using State = WorkingSet::ObjectPoolProcessingThreadState;

std::int32_t addressOf(const std::shared_ptr<WorkingSet> &ws) {
	const auto cf = ws ? ws->get_control_function() : nullptr;
	return cf ? cf->get_address() : -1;
}

std::uint64_t nameOf(const std::shared_ptr<WorkingSet> &ws) {
	const auto cf = ws ? ws->get_control_function() : nullptr;
	return cf ? cf->get_NAME().get_full_name() : 0;
}

std::shared_ptr<isobus::WorkingSet> workingSetObject(const std::shared_ptr<WorkingSet> &ws) {
	if (!ws || ws->get_object_pool_processing_state() != State::Joined) return nullptr;
	const auto object = ws->get_working_set_object();
	if (!object || object->get_object_type() != isobus::VirtualTerminalObjectType::WorkingSet) return nullptr;
	return std::static_pointer_cast<isobus::WorkingSet>(object);
}

// The terminal: the server's questions answered from the config and the host's storage, and its
// state reported to the host.
class Terminal : public isobus::VirtualTerminalServer {
public:
	explicit Terminal(std::shared_ptr<isobus::InternalControlFunction> cf) : VirtualTerminalServer(std::move(cf)) {
		repaintHandle_ = get_on_repaint_event_dispatcher().add_listener([this](std::shared_ptr<WorkingSet> ws) {
			if (ws == activeWorkingSet) dirty_ = true;
		});
		maskHandle_ = get_on_change_active_mask_event_dispatcher().add_listener(
		  [this](std::shared_ptr<WorkingSet>, std::uint16_t, std::uint16_t) { dirty_ = true; });
		keyMaskHandle_ = get_on_change_active_softkey_mask_event_dispatcher().add_listener(
		  [this](std::shared_ptr<WorkingSet>, std::uint16_t, std::uint16_t) { dirty_ = true; });
	}

	// A pool can still be parsing on its own thread; the working set that owns the thread must not
	// be destroyed (by the base class, after this) until it has finished. Upstream's working set
	// does not join it itself (AgIsoStack++ #802).
	~Terminal() {
		for (const auto &ws : managedWorkingSetList) ws->join_parsing_thread();
	}

	bool get_is_enough_memory(std::uint32_t) const override { return true; }
	VTVersion get_version() const override {
		switch (g_config.version) {
			case 3: return VTVersion::Version3;
			case 4: return VTVersion::Version4;
			case 6: return VTVersion::Version6;
			default: return VTVersion::Version5;
		}
	}
	std::uint8_t get_number_of_navigation_soft_keys() const override { return g_config.navigation_keys; }
	std::uint8_t get_soft_key_descriptor_x_pixel_width() const override { return g_config.key_width; }
	std::uint8_t get_soft_key_descriptor_y_pixel_height() const override { return g_config.key_height; }
	std::uint8_t get_number_of_possible_virtual_soft_keys_in_soft_key_mask() const override { return g_config.virtual_keys; }
	std::uint8_t get_number_of_physical_soft_keys() const override { return g_config.physical_keys; }
	std::uint16_t get_data_mask_area_size_x_pixels() const override { return g_config.mask_width; }
	std::uint16_t get_data_mask_area_size_y_pixels() const override { return g_config.mask_height; }
	std::uint8_t get_supported_small_fonts_bitfield() const override { return g_config.small_fonts; }
	std::uint8_t get_supported_large_fonts_bitfield() const override { return g_config.large_fonts; }
	void suspend_working_set(std::shared_ptr<WorkingSet>) override {}
	SupportedWideCharsErrorCode get_supported_wide_chars(std::uint8_t, std::uint16_t, std::uint16_t, std::uint8_t &ranges,
	                                                     std::vector<std::uint8_t> &) override {
		ranges = 0;
		return SupportedWideCharsErrorCode::AnyOtherError;
	}
	std::vector<std::uint8_t> get_supported_objects() const override {
		std::vector<std::uint8_t> all;
		for (int i = 0; i <= 48; ++i) all.push_back(static_cast<std::uint8_t>(i));
		return all;
	}

	std::vector<std::array<std::uint8_t, 7>> get_versions(isobus::NAME name) override { return labels<7>(name); }
	std::vector<std::array<std::uint8_t, 32>> get_extended_versions(isobus::NAME name) override {
		return get_version() == VTVersion::Version6 || get_version() == VTVersion::Version5 ? labels<32>(name)
		                                                                                     : std::vector<std::array<std::uint8_t, 32>>{};
	}
	std::vector<std::uint8_t> load_version(const std::vector<std::uint8_t> &label, isobus::NAME name) override {
		if (!g_host.load) return {};
		g_replies.clear();
		const bool found = g_host.load(name.get_full_name(), label.data(), static_cast<int32_t>(label.size())) != 0;
		std::vector<std::uint8_t> pool;
		if (found)
			for (const auto &part : g_replies) pool.insert(pool.end(), part.begin(), part.end());
		g_replies.clear();
		return pool;
	}
	// Upstream calls this once per part of the pool as it arrived, in order, for one Store Version;
	// the first part is the working set's first raw data buffer.
	bool save_version(const std::vector<std::uint8_t> &pool, const std::vector<std::uint8_t> &label, isobus::NAME name) override {
		if (!g_host.save) return false;
		bool first = true;
		for (const auto &ws : managedWorkingSetList)
			if (nameOf(ws) == name.get_full_name() && ws->get_number_iop_files() > 0) first = &ws->get_iop_raw_data(0) == &pool;
		return g_host.save(name.get_full_name(), label.data(), static_cast<int32_t>(label.size()), pool.data(),
		                   static_cast<int32_t>(pool.size()), first ? 0 : 1) != 0;
	}
	bool delete_version(const std::vector<std::uint8_t> &label, isobus::NAME name) override {
		return g_host.remove && g_host.remove(name.get_full_name(), label.data(), static_cast<int32_t>(label.size())) != 0;
	}
	bool delete_all_versions(std::uint8_t labelLen, isobus::NAME name) override {
		return g_host.remove && g_host.remove(name.get_full_name(), nullptr, labelLen) != 0;
	}
	bool delete_object_pool(isobus::NAME) override {
		listChanged_ = true;
		return true;
	}

	void object_pool_parse_finished(std::shared_ptr<WorkingSet> ws, bool success) override {
		listChanged_ = true;
		if (success)
			g_host.event(ISOSHIM_POOL_OK, static_cast<int32_t>(ws->get_object_tree().size()), addressOf(ws));
		else
			g_host.event(ISOSHIM_POOL_BAD, ws->get_object_pool_faulting_object_id(), addressOf(ws));
	}

	void handle_unexpected_working_set_shutdown(std::shared_ptr<WorkingSet> ws, UnexpectedShutdownReason reason) override {
		listChanged_ = true;
		renderer_.forget(ws.get());
		g_host.event(ISOSHIM_WS_SHUTDOWN, static_cast<int32_t>(reason), addressOf(ws));
	}

	void step() {
		// A pool that arrived in this step's frames is parsing now, on its own thread (upstream's
		// transferred_object_pool_parse_start hook is never called, AgIsoStack++ #802, so it is
		// looked for here).
		for (const auto &ws : managedWorkingSetList) {
			const bool running = ws->get_object_pool_processing_state() == State::Running;
			if (running && parsing_.insert(ws.get()).second) {
				listChanged_ = true;
				g_host.event(ISOSHIM_PARSE_START, 0, addressOf(ws));
			}
			if (!running) parsing_.erase(ws.get());
		}
		update();
		followActive();
		reportWorkingSets();
		if (dirty_) {
			dirty_ = false;
			drawScreen();
		}
	}

	bool activate(std::uint64_t name) {
		for (const auto &ws : managedWorkingSetList) {
			if (nameOf(ws) != name || !workingSetObject(ws)) continue;
			if (ws == activeWorkingSet) return true;
			if (activeWorkingSet) runMacros(activeWorkingSet, workingSetObject(activeWorkingSet), isobus::EventID::OnDeactivate);
			activeWorkingSet = ws;
			activeWorkingSetMasterAddress = static_cast<std::uint8_t>(addressOf(ws));
			activeWorkingSetDataMaskObjectID = workingSetObject(ws)->get_active_mask();
			runMacros(ws, workingSetObject(ws), isobus::EventID::OnActivate);
			followActive();
			return true;
		}
		return false;
	}

	// Soft keys and buttons: the activation message to the implement and the key's macros.
	bool key(std::uint16_t id, std::int32_t state, bool button) {
		const auto ws = activeWorkingSet;
		const auto object = workingSetObject(ws) ? find(ws, id) : nullptr;
		if (!object) return false;
		std::uint8_t code = 0;
		if (button) {
			if (object->get_object_type() != isobus::VirtualTerminalObjectType::Button) return false;
			code = std::static_pointer_cast<isobus::Button>(object)->get_key_code();
		} else {
			if (object->get_object_type() != isobus::VirtualTerminalObjectType::Key) return false;
			code = std::static_pointer_cast<isobus::Key>(object)->get_key_code();
		}
		const auto activation = static_cast<KeyActivationCode>(state);
		const auto client = ws->get_control_function();
		const bool sent = button ? send_button_activation_message(activation, id, activeWorkingSetDataMaskObjectID, code, client)
		                         : send_soft_key_activation_message(activation, id, activeWorkingSetDataMaskObjectID, code, client);
		if (state == ISOSHIM_PRESSED) runMacros(ws, object, isobus::EventID::OnKeyPress);
		if (state == ISOSHIM_RELEASED) runMacros(ws, object, isobus::EventID::OnKeyRelease);
		return sent;
	}

private:
	template<std::size_t N>
	std::vector<std::array<std::uint8_t, N>> labels(isobus::NAME name) {
		std::vector<std::array<std::uint8_t, N>> out;
		if (!g_host.list) return out;
		g_replies.clear();
		g_host.list(name.get_full_name(), static_cast<int32_t>(N));
		for (const auto &reply : g_replies) {
			if (reply.size() != N) continue;
			std::array<std::uint8_t, N> label;
			std::copy(reply.begin(), reply.end(), label.begin());
			out.push_back(label);
		}
		g_replies.clear();
		return out;
	}

	// get_object_by_id would add an empty entry for a missing id (upstream's operator[]).
	static std::shared_ptr<isobus::VTObject> find(const std::shared_ptr<WorkingSet> &ws, std::uint16_t id) {
		const auto &tree = ws->get_object_tree();
		const auto it = tree.find(id);
		return it == tree.end() ? nullptr : it->second;
	}

	void runMacros(const std::shared_ptr<WorkingSet> &ws, const std::shared_ptr<isobus::VTObject> &object, isobus::EventID event) {
		if (ws && object) process_macro(object, event, object->get_object_type(), ws);
	}

	// The status message reports the active implement's masks: keep them in step with its pool.
	void followActive() {
		const auto object = workingSetObject(activeWorkingSet);
		std::uint16_t mask = isobus::NULL_OBJECT_ID, keys = isobus::NULL_OBJECT_ID;
		if (object) {
			mask = object->get_active_mask();
			const auto maskObject = find(activeWorkingSet, mask);
			if (maskObject && maskObject->get_object_type() == isobus::VirtualTerminalObjectType::DataMask)
				keys = std::static_pointer_cast<isobus::DataMask>(maskObject)->get_soft_key_mask();
			else if (maskObject && maskObject->get_object_type() == isobus::VirtualTerminalObjectType::AlarmMask)
				keys = std::static_pointer_cast<isobus::AlarmMask>(maskObject)->get_soft_key_mask();
		}
		const std::int32_t address = object ? addressOf(activeWorkingSet) : -1;
		const bool changed = address != shownAddress_ || mask != shownMask_ || keys != shownKeys_;
		if (address != shownAddress_) {
			listChanged_ = true;
			g_host.event(ISOSHIM_ACTIVE, address, 0);
		}
		if (address != shownAddress_ || mask != shownMask_) g_host.event(ISOSHIM_MASK, mask, address);
		shownAddress_ = address;
		shownMask_ = mask;
		shownKeys_ = keys;
		if (!changed) return;
		dirty_ = true;
		if (object) {
			activeWorkingSetDataMaskObjectID = mask;
			activeWorkingSetSoftkeyMaskObjectID = keys;
		}
		if (send_status_message()) statusMessageTimestamp_ms = isobus::SystemTiming::get_timestamp_ms();
	}

	void reportWorkingSets() {
		// A transfer's progress counts as a change in whole percent.
		for (const auto &ws : managedWorkingSetList) {
			const int percent = static_cast<int>(ws->iop_load_percentage());
			auto &last = progress_[ws.get()];
			if (percent != last) {
				last = percent;
				listChanged_ = true;
			}
		}
		if (listChanged_ || managedWorkingSetList.size() != reportedCount_) {
			listChanged_ = false;
			reportedCount_ = managedWorkingSetList.size();
			isoshim::Json json;
			json.begin_object().key("type").value("workingSets");
			json.key("active");
			if (workingSetObject(activeWorkingSet))
				json.value(isoshim::hexName(nameOf(activeWorkingSet)));
			else
				json.null();
			json.key("list").begin_array();
			for (const auto &ws : managedWorkingSetList) {
				json.begin_object().key("name").value(isoshim::hexName(nameOf(ws))).key("address").value(addressOf(ws));
				const auto state = ws->get_object_pool_processing_state();
				const char *text = "waiting";
				if (state == State::Running || state == State::Success)
					text = "parsing";
				else if (state == State::Fail)
					text = "failed";
				else if (state == State::Joined)
					text = workingSetObject(ws) ? "ready" : "failed";
				else if (ws->is_object_pool_transfer_in_progress())
					text = "transferring";
				json.key("state").value(text).key("progress").value(static_cast<int>(ws->iop_load_percentage()));
				if (state == State::Joined) json.key("objects").value(static_cast<int>(ws->get_object_tree().size()));
				if (const auto object = workingSetObject(ws)) json.key("selectable").value(object->get_selectable());
				json.end_object();
			}
			json.end_array().end_object();
			message(json.str());
		}
	}

	void drawScreen() {
		std::string screen;
		if (workingSetObject(activeWorkingSet)) {
			screen = renderer_.screen(*activeWorkingSet, nameOf(activeWorkingSet), g_config.mask_width, g_config.mask_height,
			                          g_config.key_width, g_config.key_height, g_host.image);
		} else {
			screen = "{\"type\":\"screen\",\"ws\":null}";
		}
		if (screen == lastScreen_) return;
		lastScreen_ = screen;
		message(screen);
	}

	std::set<const WorkingSet *> parsing_;
	std::map<const WorkingSet *, int> progress_;
	std::size_t reportedCount_ = 0;
	bool listChanged_ = true;
	bool dirty_ = true;
	std::int32_t shownAddress_ = -2;
	std::uint16_t shownMask_ = isobus::NULL_OBJECT_ID, shownKeys_ = isobus::NULL_OBJECT_ID;
	std::string lastScreen_;
	isoshim::Renderer renderer_;
	isobus::EventCallbackHandle repaintHandle_, maskHandle_, keyMaskHandle_;
};

std::shared_ptr<isobus::InternalControlFunction> g_serverCf, g_clientCf;
std::unique_ptr<Terminal> g_terminal;
std::shared_ptr<isobus::VirtualTerminalClient> g_client;
std::shared_ptr<isobus::PartneredControlFunction> g_partner;
std::vector<std::uint8_t> g_pool;
bool g_serverReported = false, g_clientReported = false;

isobus::NAME makeName(isobus::NAME::Function function, std::uint32_t identity, std::uint16_t manufacturer, std::uint8_t instance) {
	isobus::NAME name(0);
	name.set_arbitrary_address_capable(true);
	name.set_industry_group(2);
	name.set_function_code(static_cast<std::uint8_t>(function));
	name.set_function_instance(instance);
	name.set_identity_number(identity);
	name.set_manufacturer_code(manufacturer);
	return name;
}

template<typename T>
void orDefault(T &field, T value) {
	if (field == 0) field = value;
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

int32_t isoshim_version(void) { return 3; }

int32_t isoshim_vt_start(const isoshim_vt_config *config, const isoshim_host *host) {
	if (!config || !host || !host->send || !host->event || !host->message) return -1;
	if (g_terminal) return -2;
	g_host = *host;
	g_config = *config;
	orDefault<std::uint32_t>(g_config.identity, 3);
	orDefault<std::uint16_t>(g_config.manufacturer, 1407);
	orDefault<std::uint16_t>(g_config.mask_width, 480);
	orDefault<std::uint16_t>(g_config.mask_height, 480);
	orDefault<std::uint8_t>(g_config.preferred_address, 0x26);
	orDefault<std::uint8_t>(g_config.key_width, 80);
	orDefault<std::uint8_t>(g_config.key_height, 80);
	orDefault<std::uint8_t>(g_config.physical_keys, 6);
	orDefault<std::uint8_t>(g_config.virtual_keys, 64);
	orDefault<std::uint8_t>(g_config.version, 5);
	orDefault<std::uint8_t>(g_config.small_fonts, 0xFF);
	orDefault<std::uint8_t>(g_config.large_fonts, 0xFF);
	orDefault<std::uint8_t>(g_config.packets_per_cts, 255);

	auto &network = isobus::CANNetworkManager::CANNetwork.get_configuration();
	network.set_number_of_packets_per_cts_message(g_config.packets_per_cts);
	network.set_number_of_packets_per_dpo_message(g_config.packets_per_cts);
	if (std::getenv("ISOSHIM_LOG")) {
		isobus::CANStackLogger::set_can_stack_logger_sink(&g_logger);
		isobus::CANStackLogger::set_log_level(isobus::CANStackLogger::LoggingLevel::Info);
	}
	g_channels = 1;
	g_serverCf = isobus::CANNetworkManager::CANNetwork.create_internal_control_function(
	  makeName(isobus::NAME::Function::VirtualTerminal, g_config.identity, g_config.manufacturer, g_config.function_instance), 0,
	  g_config.preferred_address);
	g_terminal.reset(new Terminal(g_serverCf));
	g_terminal->initialize();
	return 0;
}

int32_t isoshim_test_implement(const uint8_t *pool, int32_t poolLen) {
	if (!g_terminal || g_client || !pool || poolLen <= 0) return -1;
	g_pool.assign(pool, pool + poolLen);
	g_channels = 2;
	g_clientCf = isobus::CANNetworkManager::CANNetwork.create_internal_control_function(
	  makeName(isobus::NAME::Function::SteeringControl, 2, 1407, 0), 1);
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
		g_host.event(ISOSHIM_SERVER_ADDRESS, g_serverCf->get_address(), 0);
	}
	if (!g_clientReported && g_clientCf && g_clientCf->get_address_valid()) {
		g_clientReported = true;
		g_host.event(ISOSHIM_CLIENT_ADDRESS, g_clientCf->get_address(), 0);
	}
}

void isoshim_reply(const uint8_t *data, int32_t len) {
	if (data && len > 0) g_replies.emplace_back(data, data + len);
}

int32_t isoshim_soft_key(uint16_t objectId, int32_t state) {
	if (!g_terminal || state < 0 || state > 3) return 0;
	const bool sent = g_terminal->key(objectId, state, false);
	flushTransmit();
	return sent ? 1 : 0;
}

int32_t isoshim_button(uint16_t objectId, int32_t state) {
	if (!g_terminal || state < 0 || state > 3) return 0;
	const bool sent = g_terminal->key(objectId, state, true);
	flushTransmit();
	return sent ? 1 : 0;
}

int32_t isoshim_activate(uint64_t name) {
	if (!g_terminal) return 0;
	const bool done = g_terminal->activate(name);
	flushTransmit();
	return done ? 1 : 0;
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
	g_host = {};
}

} // extern "C"
