// SPDX-License-Identifier: MIT
// Copyright (c) 2026 The SharpIsoTerm contributors
//
// See isoshim.h. Built on AgIsoStack++'s isobus and utility libraries only: this file provides the
// stack's hardware hooks (can_hardware_abstraction.hpp) itself, so HardwareIntegration and its
// threads stay out.
#include "isoshim.h"
#include "render.hpp"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <functional>
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

void message(std::int32_t terminal, const std::string &json) { g_host.message(terminal, json.c_str(), static_cast<int32_t>(json.size())); }

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

// One terminal: the server's questions answered from its config and the host's storage, and its
// state reported to the host under its position (index).
class Terminal : public isobus::VirtualTerminalServer {
public:
	Terminal(std::shared_ptr<isobus::InternalControlFunction> cf, std::int32_t index, const isoshim_vt_config &config)
	    : VirtualTerminalServer(std::move(cf)), index_(index), config_(config) {
		if (g_host.image)
			image_ = [index](std::uint32_t id, std::int32_t w, std::int32_t h, const std::uint8_t *rgba) { g_host.image(index, id, w, h, rgba); };
		repaintHandle_ = get_on_repaint_event_dispatcher().add_listener([this](std::shared_ptr<WorkingSet> ws) {
			if (ws == activeWorkingSet) dirty_ = true;
		});
		maskHandle_ = get_on_change_active_mask_event_dispatcher().add_listener(
		  [this](std::shared_ptr<WorkingSet>, std::uint16_t, std::uint16_t) { dirty_ = true; });
		keyMaskHandle_ = get_on_change_active_softkey_mask_event_dispatcher().add_listener(
		  [this](std::shared_ptr<WorkingSet>, std::uint16_t, std::uint16_t) { dirty_ = true; });
		// The implement selected an input itself (Select Input Object command): upstream has set
		// the focus; the editor follows it.
		focusHandle_ = get_on_focus_object_event_dispatcher().add_listener([this](std::shared_ptr<WorkingSet> ws, std::uint16_t id, bool open) {
			if (ws != activeWorkingSet) return;
			setOpen(ws, open ? id : isobus::NULL_OBJECT_ID);
			dirty_ = true;
		});
	}

	// A pool can still be parsing on its own thread: the working set joins it when destroyed
	// (AgIsoStack++ #805, fixing #802).
	~Terminal() {
		const auto pgn = static_cast<std::uint32_t>(isobus::CANLibParameterGroupNumber::ECUtoVirtualTerminal);
		isobus::CANNetworkManager::CANNetwork.remove_any_control_function_parameter_group_number_callback(pgn, onImplementMessage, this);
		isobus::CANNetworkManager::CANNetwork.remove_any_control_function_parameter_group_number_callback(pgn, onAddressed, this);
	}

	// After the base class's own initialize. Upstream's server takes every implement message sent
	// to any of the process's own control functions, so with two terminals each would also answer
	// the other's implements (and take them on): it hears only its own, and those sent to everyone
	// (gap 11). Then a second listener for the ones upstream acknowledges and drops (audio, gap 5)
	// or does not handle (ESC, gap 8; Identify VT from an implement not connected, gap 12).
	void listen() {
		const auto pgn = static_cast<std::uint32_t>(isobus::CANLibParameterGroupNumber::ECUtoVirtualTerminal);
		isobus::CANNetworkManager::CANNetwork.remove_any_control_function_parameter_group_number_callback(pgn, process_rx_message, this);
		isobus::CANNetworkManager::CANNetwork.add_any_control_function_parameter_group_number_callback(pgn, onAddressed, this);
		isobus::CANNetworkManager::CANNetwork.add_any_control_function_parameter_group_number_callback(pgn, onImplementMessage, this);
	}

	std::shared_ptr<isobus::InternalControlFunction> controlFunction() const { return serverInternalControlFunction; }

	bool get_is_enough_memory(std::uint32_t) const override { return true; }
	VTVersion get_version() const override {
		switch (config_.version) {
			case 3: return VTVersion::Version3;
			case 4: return VTVersion::Version4;
			case 6: return VTVersion::Version6;
			default: return VTVersion::Version5;
		}
	}
	std::uint8_t get_number_of_navigation_soft_keys() const override { return config_.navigation_keys; }
	std::uint8_t get_soft_key_descriptor_x_pixel_width() const override { return config_.key_width; }
	std::uint8_t get_soft_key_descriptor_y_pixel_height() const override { return config_.key_height; }
	std::uint8_t get_number_of_possible_virtual_soft_keys_in_soft_key_mask() const override { return config_.virtual_keys; }
	std::uint8_t get_number_of_physical_soft_keys() const override { return config_.physical_keys; }
	std::uint16_t get_data_mask_area_size_x_pixels() const override { return config_.mask_width; }
	std::uint16_t get_data_mask_area_size_y_pixels() const override { return config_.mask_height; }
	std::uint8_t get_supported_small_fonts_bitfield() const override { return config_.small_fonts; }
	std::uint8_t get_supported_large_fonts_bitfield() const override { return config_.large_fonts; }
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
	// Upstream calls this once per part of the pool as it arrived, in order, for one Store Version,
	// after delete_version for its label (AgIsoStack++ #809); the first part is the working set's
	// first raw data buffer, so the host is told to start afresh there too.
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
	bool delete_object_pool(isobus::NAME) override { return true; }

	// After the server deleted the pool (Delete Object Pool) and cleared the active working set;
	// the working set stays, and its next pool comes into the same one.
	void object_pool_deleted(std::shared_ptr<WorkingSet> ws) override {
		listChanged_ = true;
		renderer_.forget(ws.get());
		if (ws == openWs_) setOpen(ws, isobus::NULL_OBJECT_ID); // the host's editor closes
	}

	void object_pool_parse_finished(std::shared_ptr<WorkingSet> ws, bool success) override {
		listChanged_ = true;
		if (success)
			g_host.event(index_, ISOSHIM_POOL_OK, static_cast<int32_t>(ws->get_object_tree().size()), addressOf(ws));
		else
			g_host.event(index_, ISOSHIM_POOL_BAD, ws->get_object_pool_faulting_object_id(), addressOf(ws));
	}

	void handle_unexpected_working_set_shutdown(std::shared_ptr<WorkingSet> ws, UnexpectedShutdownReason reason) override {
		listChanged_ = true;
		renderer_.forget(ws.get());
		if (ws == openWs_) setOpen(ws, isobus::NULL_OBJECT_ID); // the host's editor closes
		g_host.event(index_, ISOSHIM_WS_SHUTDOWN, static_cast<int32_t>(reason), addressOf(ws));
	}

	void step() {
		// A pool that arrived in this step's frames is parsing now, on its own thread (upstream's
		// transferred_object_pool_parse_start hook is never called, AgIsoStack++ #802, so it is
		// looked for here).
		for (const auto &ws : managedWorkingSetList) {
			const bool running = ws->get_object_pool_processing_state() == State::Running;
			if (running && parsing_.insert(ws.get()).second) {
				listChanged_ = true;
				g_host.event(index_, ISOSHIM_PARSE_START, 0, addressOf(ws));
			}
			if (!running) parsing_.erase(ws.get());
		}
		update();
		flushOutbox();
		followActive();
		reportWorkingSets();
		reportAlarms();
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

	// Self-test: the data and alarm masks of the active implement's pool, and its working set object.
	std::vector<std::uint16_t> masks(std::uint16_t &workingSet) const {
		std::vector<std::uint16_t> ids;
		const auto object = workingSetObject(activeWorkingSet);
		if (!object) return ids;
		workingSet = object->get_id();
		for (const auto &entry : activeWorkingSet->get_object_tree()) {
			const auto type = entry.second->get_object_type();
			if (type == isobus::VirtualTerminalObjectType::DataMask || type == isobus::VirtualTerminalObjectType::AlarmMask)
				ids.push_back(entry.first);
		}
		return ids;
	}

	// ---- Input fields (docs/API.md): select, enter a value, escape ----

	// The operator selects an input field or a button (open: an editor is up for an input), or
	// nothing (NULL_OBJECT_ID). Only what the last screen drew, enabled, can be selected.
	bool select(std::uint16_t id, bool open) {
		const auto ws = activeWorkingSet;
		if (!workingSetObject(ws)) return false;
		const bool none = id == isobus::NULL_OBJECT_ID;
		if (!none && !renderer_.touchable(id)) return false;
		const auto object = none ? nullptr : find(ws, id);
		if (!object || object->get_object_type() == isobus::VirtualTerminalObjectType::Button) open = false;
		const std::uint16_t old = ws->get_object_focus();
		if (openId_ != isobus::NULL_OBJECT_ID && (openId_ != id || !open)) escape(); // leaving an open field aborts it
		if (old != id) {
			if (old != isobus::NULL_OBJECT_ID) runMacros(ws, find(ws, old), isobus::EventID::OnInputFieldDeselection);
			ws->set_object_focus(id);
			if (!none) runMacros(ws, object, isobus::EventID::OnInputFieldSelection);
		}
		if (!none)
			post([=] { return send_select_input_object_message(id, true, open, ws->get_control_function()); });
		else if (old != isobus::NULL_OBJECT_ID)
			post([=] { return send_select_input_object_message(old, false, false, ws->get_control_function()); });
		setOpen(ws, open ? id : isobus::NULL_OBJECT_ID);
		dirty_ = true;
		return true;
	}

	// A value for an input number (raw, before offset and scale), boolean (0 or 1) or list (the
	// item's index). 1: entered; -1: not a valid value (the field stays as it was); 0: not an
	// enabled input on the screen.
	int setNumber(std::uint16_t id, std::uint32_t value) {
		using Type = isobus::VirtualTerminalObjectType;
		const auto ws = activeWorkingSet;
		if (!workingSetObject(ws) || !renderer_.touchable(id)) return 0;
		const auto object = find(ws, id);
		std::uint16_t variableId = isobus::NULL_OBJECT_ID;
		std::uint32_t own = 0;
		switch (object->get_object_type()) {
			case Type::InputNumber: {
				const auto n = std::static_pointer_cast<isobus::InputNumber>(object);
				if (value < n->get_minimum_value() || value > n->get_maximum_value()) return -1;
				variableId = n->get_variable_reference();
				own = n->get_value();
				break;
			}
			case Type::InputBoolean: {
				const auto b = std::static_pointer_cast<isobus::InputBoolean>(object);
				if (value > 1) return -1;
				variableId = b->get_variable_reference();
				own = b->get_value();
				break;
			}
			case Type::InputList: {
				const auto l = std::static_pointer_cast<isobus::InputList>(object);
				if (value >= object->get_number_children() || !find(ws, object->get_child_id(static_cast<std::uint16_t>(value)))) return -1;
				variableId = l->get_variable_reference();
				own = l->get_value();
				break;
			}
			default: return 0;
		}
		const auto found = find(ws, variableId);
		const auto variable = found && found->get_object_type() == Type::NumberVariable ? std::static_pointer_cast<isobus::NumberVariable>(found) : nullptr;
		const std::uint32_t old = variable ? variable->get_value() : own;
		if (variable)
			variable->set_value(value);
		else if (object->get_object_type() == Type::InputNumber)
			std::static_pointer_cast<isobus::InputNumber>(object)->set_value(value);
		else if (object->get_object_type() == Type::InputBoolean)
			std::static_pointer_cast<isobus::InputBoolean>(object)->set_value(static_cast<std::uint8_t>(value));
		else
			std::static_pointer_cast<isobus::InputList>(object)->set_value(static_cast<std::uint8_t>(value));
		// The changed value's object: the variable when the input has one (ISO 11783-6; Topcon and
		// espVT send the same), else the input.
		const std::uint16_t changed = variable ? variableId : id;
		post([=] { return send_change_numeric_value_message(changed, value, ws->get_control_function()); });
		entered(ws, object, value != old);
		return 1;
	}

	// Text (UTF-8) for an input string; stored in the field's encoding and padded to its length.
	// Results as setNumber: -1 when too long, or a character the field does not take.
	int setString(std::uint16_t id, const std::string &text) {
		using Type = isobus::VirtualTerminalObjectType;
		const auto ws = activeWorkingSet;
		if (!workingSetObject(ws) || !renderer_.touchable(id)) return 0;
		const auto object = find(ws, id);
		if (object->get_object_type() != Type::InputString) return 0;
		const auto field = std::static_pointer_cast<isobus::InputString>(object);
		const auto found = find(ws, field->get_variable_reference());
		const auto variable = found && found->get_object_type() == Type::StringVariable ? std::static_pointer_cast<isobus::StringVariable>(found) : nullptr;
		const std::string current = variable ? variable->get_value() : field->get_value();
		std::string value;
		if (!isoshim::poolText(text, current, value)) return -1;
		const auto attributes = find(ws, field->get_input_attributes());
		if (attributes && attributes->get_object_type() == Type::InputAttributes) {
			const auto a = std::static_pointer_cast<isobus::InputAttributes>(attributes);
			const auto listed = isoshim::codePoints(isoshim::utf8(a->get_validation_string()));
			const bool only = a->get_validation_type() == isobus::InputAttributes::ValidationType::ValidCharactersAreListed;
			for (const auto c : isoshim::codePoints(text))
				if ((std::find(listed.begin(), listed.end(), c) != listed.end()) != only) return -1;
		}
		if (variable)
			variable->set_value(value);
		else
			field->set_value(value);
		const std::uint16_t changed = variable ? field->get_variable_reference() : id; // as setNumber
		post([=] { return send_change_string_value_message(changed, value, ws->get_control_function()); });
		entered(ws, object, value != current);
		return 1;
	}

	// The operator abandons the open field (VT ESC message). False if none is open.
	bool escape() {
		if (openId_ == isobus::NULL_OBJECT_ID || !openWs_) return false;
		const auto ws = openWs_;
		const std::uint16_t id = openId_;
		post([=] {
			const std::uint8_t data[8] = { static_cast<std::uint8_t>(Function::VTESCMessage), static_cast<std::uint8_t>(id & 0xFF),
				                           static_cast<std::uint8_t>(id >> 8), 0, 0xFF, 0xFF, 0xFF, 0xFF };
			return isobus::CANNetworkManager::CANNetwork.send_can_message(
			  static_cast<std::uint32_t>(isobus::CANLibParameterGroupNumber::VirtualTerminalToECU), data, sizeof data,
			  serverInternalControlFunction, ws->get_control_function(), get_priority());
		});
		setOpen(ws, isobus::NULL_OBJECT_ID);
		runMacros(ws, find(ws, id), isobus::EventID::OnESC);
		dirty_ = true;
		return true;
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
		const std::uint16_t parent = activeWorkingSetDataMaskObjectID;
		post([=] {
			return button ? send_button_activation_message(activation, id, parent, code, client)
			              : send_soft_key_activation_message(activation, id, parent, code, client);
		});
		if (state == ISOSHIM_PRESSED) runMacros(ws, object, isobus::EventID::OnKeyPress);
		if (state == ISOSHIM_RELEASED) runMacros(ws, object, isobus::EventID::OnKeyRelease);
		return true;
	}

private:
	void message(const std::string &json) const { ::message(index_, json); }

	static void onAddressed(const isobus::CANMessage &m, void *parent) {
		auto *self = static_cast<Terminal *>(parent);
		const auto to = m.get_destination_control_function();
		if (to != nullptr && to != self->serverInternalControlFunction) return;
		if (m.get_data_length() > 0 && m.get_uint8_at(0) == static_cast<std::uint8_t>(Function::IdentifyVTMessage)) return; // ours
		process_rx_message(m, self);
	}

	// Every implement showing an alarm mask, shown or not: {"type":"alarms","list":[{ws, address,
	// mask, priority (0 high, 1 medium, 2 low), signal (0 highest .. 3 none)}]}, sent on change.
	void reportAlarms() {
		isoshim::Json json;
		json.begin_object().key("type").value("alarms").key("list").begin_array();
		for (const auto &ws : managedWorkingSetList) {
			const auto object = workingSetObject(ws);
			const auto mask = object ? find(ws, object->get_active_mask()) : nullptr;
			if (!mask || mask->get_object_type() != isobus::VirtualTerminalObjectType::AlarmMask) continue;
			const auto alarm = std::static_pointer_cast<isobus::AlarmMask>(mask);
			json.begin_object().key("ws").value(isoshim::hexName(nameOf(ws))).key("address").value(addressOf(ws));
			json.key("mask").value(static_cast<int>(mask->get_id()));
			json.key("priority").value(static_cast<int>(alarm->get_mask_priority()));
			json.key("signal").value(static_cast<int>(alarm->get_signal_priority())).end_object();
		}
		json.end_array().end_object();
		if (json.str() == lastAlarms_) return;
		lastAlarms_ = json.str();
		message(lastAlarms_);
	}

	static void onImplementMessage(const isobus::CANMessage &m, void *parent) {
		auto *self = static_cast<Terminal *>(parent);
		const auto &data = m.get_data();
		if (data.size() < 8) return;
		const auto to = m.get_destination_control_function();
		// Identify VT, from any implement, connected or not, to this terminal or to every one: the
		// host shows the terminal's number for about 3 s (so the operator can pick a terminal in
		// the implement's own settings). Upstream takes it only from a connected implement.
		if (data[0] == static_cast<std::uint8_t>(Function::IdentifyVTMessage) && (to == nullptr || to == self->serverInternalControlFunction)) {
			self->message("{\"type\":\"identify\"}");
			return;
		}
		if (to != self->serverInternalControlFunction) return;
		std::shared_ptr<WorkingSet> ws;
		for (const auto &candidate : self->managedWorkingSetList)
			if (candidate->get_control_function() == m.get_source_control_function()) ws = candidate;
		if (!ws) return;
		isoshim::Json json;
		switch (data[0]) {
			case static_cast<std::uint8_t>(Function::ControlAudioSignalCommand):
				// activations 0 stops a signal that is sounding
				json.begin_object().key("type").value("audio").key("ws").value(isoshim::hexName(nameOf(ws)));
				json.key("activations").value(static_cast<int>(data[1])).key("frequency").value(static_cast<int>(m.get_uint16_at(2)));
				json.key("on").value(static_cast<int>(m.get_uint16_at(4))).key("off").value(static_cast<int>(m.get_uint16_at(6))).end_object();
				self->message(json.str());
				break;
			case static_cast<std::uint8_t>(Function::SetAudioVolumeCommand):
				json.begin_object().key("type").value("volume").key("ws").value(isoshim::hexName(nameOf(ws)));
				json.key("percent").value(static_cast<int>(data[1])).end_object();
				self->message(json.str());
				break;
			case static_cast<std::uint8_t>(Function::ESCCommand): self->escCommand(ws); break;
			default: break;
		}
	}

	// The implement's ESC command: its open field closes (no VT ESC message: the implement asked),
	// and the answer names the field, or says none was open.
	void escCommand(const std::shared_ptr<WorkingSet> &ws) {
		std::uint16_t id = isobus::NULL_OBJECT_ID;
		std::uint8_t error = 1; // no input field is open
		if (openWs_ == ws && openId_ != isobus::NULL_OBJECT_ID) {
			id = openId_;
			error = 0;
			setOpen(ws, isobus::NULL_OBJECT_ID);
			runMacros(ws, find(ws, id), isobus::EventID::OnESC);
			dirty_ = true;
		}
		post([=] {
			const std::uint8_t data[8] = { static_cast<std::uint8_t>(Function::ESCCommand), static_cast<std::uint8_t>(id & 0xFF),
				                           static_cast<std::uint8_t>(id >> 8), error, 0xFF, 0xFF, 0xFF, 0xFF };
			return isobus::CANNetworkManager::CANNetwork.send_can_message(
			  static_cast<std::uint32_t>(isobus::CANLibParameterGroupNumber::VirtualTerminalToECU), data, sizeof data,
			  serverInternalControlFunction, ws->get_control_function(), get_priority());
		});
	}

	// The operator's messages to an implement, in order. One the stack cannot take now (a
	// multi-frame message while the last one to the same implement is still closing) waits for a
	// later step; after 2 s it is dropped (the implement has gone).
	void post(std::function<bool()> send) {
		if (outbox_.empty() && send()) return;
		outbox_.push_back({ isobus::SystemTiming::get_timestamp_ms() + 2000, std::move(send) });
	}
	void flushOutbox() {
		while (!outbox_.empty() && (outbox_.front().second() || isobus::SystemTiming::get_timestamp_ms() > outbox_.front().first))
			outbox_.pop_front();
	}

	// A value was entered: the entry macros, and the field closes but stays selected.
	void entered(const std::shared_ptr<WorkingSet> &ws, const std::shared_ptr<isobus::VTObject> &object, bool changed) {
		const std::uint16_t id = object->get_id();
		if (ws->get_object_focus() != id) {
			const std::uint16_t old = ws->get_object_focus();
			if (old != isobus::NULL_OBJECT_ID) runMacros(ws, find(ws, old), isobus::EventID::OnInputFieldDeselection);
			ws->set_object_focus(id);
			runMacros(ws, object, isobus::EventID::OnInputFieldSelection);
		}
		if (openId_ == id) post([=] { return send_select_input_object_message(id, true, false, ws->get_control_function()); });
		setOpen(ws, isobus::NULL_OBJECT_ID);
		runMacros(ws, object, isobus::EventID::OnEntryOfAValue);
		if (changed) runMacros(ws, object, isobus::EventID::OnEntryOfANewValue);
		dirty_ = true;
	}

	// Which field is open for input; the host is told with an edit message (o null: closed).
	void setOpen(const std::shared_ptr<WorkingSet> &ws, std::uint16_t id) {
		if (id == openId_ && ws == openWs_) return;
		const bool wasOpen = openId_ != isobus::NULL_OBJECT_ID;
		openId_ = id;
		openWs_ = id == isobus::NULL_OBJECT_ID ? nullptr : ws;
		if (id != isobus::NULL_OBJECT_ID || wasOpen)
			message(renderer_.edit(*ws, nameOf(ws), id, image_));
	}

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
		if (openId_ != isobus::NULL_OBJECT_ID && (address != shownAddress_ || mask != shownMask_)) escape(); // the field has gone
		if (address != shownAddress_) {
			listChanged_ = true;
			g_host.event(index_, ISOSHIM_ACTIVE, address, 0);
		}
		if (address != shownAddress_ || mask != shownMask_) g_host.event(index_, ISOSHIM_MASK, mask, address);
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
			screen = renderer_.screen(*activeWorkingSet, nameOf(activeWorkingSet), config_.mask_width, config_.mask_height,
			                          config_.key_width, config_.key_height, activeWorkingSet->get_object_focus(),
			                          openId_ != isobus::NULL_OBJECT_ID && openWs_ == activeWorkingSet, image_);
		} else {
			screen = "{\"type\":\"screen\",\"ws\":null}";
		}
		if (screen == lastScreen_) return;
		lastScreen_ = screen;
		message(screen);
	}

	const std::int32_t index_;
	const isoshim_vt_config config_;
	isoshim::ImageSink image_;
	std::set<const WorkingSet *> parsing_;
	std::map<const WorkingSet *, int> progress_;
	std::size_t reportedCount_ = 0;
	bool listChanged_ = true;
	bool dirty_ = true;
	std::int32_t shownAddress_ = -2;
	std::uint16_t shownMask_ = isobus::NULL_OBJECT_ID, shownKeys_ = isobus::NULL_OBJECT_ID;
	std::string lastScreen_;
	isoshim::Renderer renderer_;
	std::deque<std::pair<std::uint32_t, std::function<bool()>>> outbox_;
	std::string lastAlarms_;
	std::shared_ptr<WorkingSet> openWs_;
	std::uint16_t openId_ = isobus::NULL_OBJECT_ID;
	isobus::EventCallbackHandle repaintHandle_, maskHandle_, keyMaskHandle_, focusHandle_;
};

// The terminals, in the host's order, each with its control function.
struct Server {
	std::shared_ptr<isobus::InternalControlFunction> cf;
	std::unique_ptr<Terminal> terminal;
	bool reported = false;
};
std::vector<Server> g_servers;

// Self-test: upstream's VT client, each partnered to one terminal.
struct TestImplement {
	std::int32_t terminal;
	std::shared_ptr<isobus::InternalControlFunction> cf;
	std::shared_ptr<isobus::PartneredControlFunction> partner;
	std::shared_ptr<isobus::VirtualTerminalClient> client;
	std::vector<std::uint8_t> pool;
	bool reported = false;
};
std::vector<std::unique_ptr<TestImplement>> g_implements;

Terminal *terminalAt(std::int32_t index) {
	return index >= 0 && index < static_cast<std::int32_t>(g_servers.size()) ? g_servers[index].terminal.get() : nullptr;
}

TestImplement *implementAt(std::int32_t index) {
	return index >= 0 && index < static_cast<std::int32_t>(g_implements.size()) ? g_implements[index].get() : nullptr;
}

// Self-test: a terminal's answer to a test implement's ESC command, which upstream's client does
// not report.
void heardEscResponse(const isobus::CANMessage &m, void *parent) {
	const auto *implement = static_cast<const TestImplement *>(parent);
	const auto &data = m.get_data();
	if (data.size() < 4 || data[0] != 0x92 || m.get_destination_control_function() != implement->cf) return;
	isoshim::Json json;
	json.begin_object().key("type").value("implement").key("event").value("escResponse");
	json.key("o").value(static_cast<int>(m.get_uint16_at(1))).key("error").value(static_cast<int>(data[3])).end_object();
	message(implement->terminal, json.str());
}

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

int32_t isoshim_version(void) { return 6; }

int32_t isoshim_vt_start(const isoshim_vt_config *configs, int32_t count, const isoshim_host *host) {
	if (!configs || count < 1 || count > ISOSHIM_MAX_TERMINALS || !host || !host->send || !host->event || !host->message) return -1;
	if (!g_servers.empty()) return -2;
	std::vector<isoshim_vt_config> config(configs, configs + count);
	std::set<std::uint64_t> names;
	for (std::int32_t i = 0; i < count; ++i) {
		auto &c = config[i];
		orDefault<std::uint32_t>(c.identity, 3);
		orDefault<std::uint16_t>(c.manufacturer, 1407);
		orDefault<std::uint16_t>(c.mask_width, 480);
		orDefault<std::uint16_t>(c.mask_height, 480);
		orDefault<std::uint8_t>(c.preferred_address, static_cast<std::uint8_t>(0x26 + i));
		orDefault<std::uint8_t>(c.key_width, 80);
		orDefault<std::uint8_t>(c.key_height, 80);
		orDefault<std::uint8_t>(c.physical_keys, 12);
		orDefault<std::uint8_t>(c.virtual_keys, 64);
		orDefault<std::uint8_t>(c.version, 5);
		orDefault<std::uint8_t>(c.small_fonts, 0xFF);
		orDefault<std::uint8_t>(c.large_fonts, 0xFF);
		orDefault<std::uint8_t>(c.packets_per_cts, 255);
		const auto name = makeName(isobus::NAME::Function::VirtualTerminal, c.identity, c.manufacturer, c.function_instance);
		if (!names.insert(name.get_full_name()).second) return -1; // two terminals with one NAME
	}
	g_host = *host;

	auto &network = isobus::CANNetworkManager::CANNetwork.get_configuration();
	network.set_number_of_packets_per_cts_message(config[0].packets_per_cts);
	network.set_number_of_packets_per_dpo_message(config[0].packets_per_cts);
	if (std::getenv("ISOSHIM_LOG")) {
		isobus::CANStackLogger::set_can_stack_logger_sink(&g_logger);
		isobus::CANStackLogger::set_log_level(isobus::CANStackLogger::LoggingLevel::Info);
	}
	g_channels = 1;
	for (std::int32_t i = 0; i < count; ++i) {
		const auto &c = config[i];
		Server server;
		server.cf = isobus::CANNetworkManager::CANNetwork.create_internal_control_function(
		  makeName(isobus::NAME::Function::VirtualTerminal, c.identity, c.manufacturer, c.function_instance), 0, c.preferred_address);
		server.terminal.reset(new Terminal(server.cf, i, c));
		server.terminal->initialize();
		server.terminal->listen();
		g_servers.push_back(std::move(server));
	}
	return 0;
}

int32_t isoshim_test_implement(int32_t terminal, const uint8_t *pool, int32_t poolLen) {
	if (!terminalAt(terminal) || !pool || poolLen <= 0) return -1;
	const auto index = static_cast<std::int32_t>(g_implements.size());
	auto implement = std::make_unique<TestImplement>();
	auto *self = implement.get();
	self->terminal = terminal;
	self->pool.assign(pool, pool + poolLen);
	g_channels = 2;
	self->cf = isobus::CANNetworkManager::CANNetwork.create_internal_control_function(
	  makeName(isobus::NAME::Function::SteeringControl, static_cast<std::uint32_t>(2 + index), 1407, 0), 1);
	// Its terminal: a VT with that terminal's function instance.
	const auto vtName = g_servers[terminal].cf->get_NAME();
	const isobus::NAMEFilter vt(isobus::NAME::NAMEParameters::FunctionCode,
	                            static_cast<std::uint8_t>(isobus::NAME::Function::VirtualTerminal));
	const isobus::NAMEFilter instance(isobus::NAME::NAMEParameters::FunctionInstance, vtName.get_function_instance());
	self->partner = isobus::CANNetworkManager::CANNetwork.create_partnered_control_function(1, { vt, instance });
	self->client = std::make_shared<isobus::VirtualTerminalClient>(self->partner, self->cf);
	self->client->set_object_pool(0, self->pool.data(), static_cast<std::uint32_t>(self->pool.size()),
	                              isobus::IOPFileInterface::hash_object_pool_to_version(self->pool));
	// The example's behaviour: the alarm soft key (5000) shows the alarm mask (2000).
	self->client->get_vt_soft_key_event_dispatcher().add_listener([self](const isobus::VirtualTerminalClient::VTKeyEvent &e) {
		if (e.keyEvent == isobus::VirtualTerminalClient::KeyActivationCode::ButtonUnlatchedOrReleased && e.objectID == 5000)
			self->client->send_change_active_mask(0, 2000);
	});
	// What the implement hears of the operator's input, for the self-test to check, under its
	// terminal: {"type":"implement","event":"select"|"esc"|"number"|"string"|"key","o":id,...}
	const auto heard = [](const char *event, std::uint16_t id) {
		auto json = std::make_shared<isoshim::Json>();
		json->begin_object().key("type").value("implement").key("event").value(event).key("o").value(static_cast<int>(id));
		return json;
	};
	self->client->get_vt_select_input_object_event_dispatcher().add_listener([heard, terminal](const isobus::VirtualTerminalClient::VTSelectInputObjectEvent &e) {
		auto json = heard("select", e.objectID);
		json->key("selected").value(e.objectSelected).key("open").value(e.objectOpenForInput).end_object();
		message(terminal, json->str());
	});
	const auto key = [heard, terminal](const isobus::VirtualTerminalClient::VTKeyEvent &e) {
		auto json = heard("key", e.objectID);
		json->key("state").value(static_cast<int>(e.keyEvent)).end_object(); // 0 released .. 3 aborted
		message(terminal, json->str());
	};
	self->client->get_vt_soft_key_event_dispatcher().add_listener(key);
	self->client->get_vt_button_event_dispatcher().add_listener(key);
	self->client->get_vt_esc_message_event_dispatcher().add_listener([heard, terminal](const isobus::VirtualTerminalClient::VTESCMessageEvent &e) {
		auto json = heard("esc", e.objectID);
		json->end_object();
		message(terminal, json->str());
	});
	self->client->get_vt_change_numeric_value_event_dispatcher().add_listener([heard, terminal](const isobus::VirtualTerminalClient::VTChangeNumericValueEvent &e) {
		auto json = heard("number", e.objectID);
		json->key("value").value(static_cast<double>(e.value)).end_object();
		message(terminal, json->str());
	});
	self->client->get_vt_change_string_value_event_dispatcher().add_listener([heard, terminal](const isobus::VirtualTerminalClient::VTChangeStringValueEvent &e) {
		auto json = heard("string", e.objectID);
		json->key("value").value(isoshim::utf8(e.value)).end_object();
		message(terminal, json->str());
	});
	isobus::CANNetworkManager::CANNetwork.add_any_control_function_parameter_group_number_callback(
	  static_cast<std::uint32_t>(isobus::CANLibParameterGroupNumber::VirtualTerminalToECU), heardEscResponse, self);
	self->client->initialize(false); // updated from isoshim_step, not its own thread
	g_implements.push_back(std::move(implement));
	return index;
}

int32_t isoshim_test_masks(int32_t terminal, uint16_t *ids, int32_t max) {
	const auto t = terminalAt(terminal);
	if (!t || !ids || max < 0) return -1;
	std::uint16_t workingSet = 0;
	const auto masks = t->masks(workingSet);
	for (std::size_t i = 0; i < masks.size() && i < static_cast<std::size_t>(max); ++i) ids[i] = masks[i];
	return static_cast<int32_t>(masks.size());
}

int32_t isoshim_test_show(int32_t implement, uint16_t mask) {
	const auto self = implementAt(implement);
	if (!self) return 0;
	std::uint16_t workingSet = isobus::NULL_OBJECT_ID;
	terminalAt(self->terminal)->masks(workingSet);
	if (workingSet == isobus::NULL_OBJECT_ID) return 0;
	const bool sent = self->client->send_change_active_mask(workingSet, mask);
	flushTransmit();
	return sent ? 1 : 0;
}

int32_t isoshim_test_send(int32_t implement, const uint8_t *data, int32_t len, int32_t global) {
	const auto self = implementAt(implement);
	if (!self || !data || len != 8) return 0;
	const bool sent = isobus::CANNetworkManager::CANNetwork.send_can_message(
	  static_cast<std::uint32_t>(isobus::CANLibParameterGroupNumber::ECUtoVirtualTerminal), data, 8, self->cf,
	  global ? nullptr : self->partner);
	flushTransmit();
	return sent ? 1 : 0;
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
	if (g_servers.empty()) return;
	std::deque<isobus::CANMessageFrame> in;
	{
		std::lock_guard<std::mutex> lock(g_rxMutex);
		in.swap(g_rx);
	}
	for (const auto &frame : in)
		if (frame.channel < g_channels) isobus::receive_can_message_frame_from_hardware(frame);

	isobus::periodic_update_from_hardware(); // the network manager: transport protocols, address claim
	for (const auto &implement : g_implements) implement->client->update();
	for (const auto &server : g_servers) server.terminal->step();
	flushTransmit();

	for (std::size_t i = 0; i < g_servers.size(); ++i) {
		auto &server = g_servers[i];
		if (!server.reported && server.cf->get_address_valid()) {
			server.reported = true;
			g_host.event(static_cast<std::int32_t>(i), ISOSHIM_SERVER_ADDRESS, server.cf->get_address(), 0);
		}
	}
	for (std::size_t i = 0; i < g_implements.size(); ++i) {
		auto &implement = *g_implements[i];
		if (!implement.reported && implement.cf->get_address_valid()) {
			implement.reported = true;
			g_host.event(implement.terminal, ISOSHIM_CLIENT_ADDRESS, implement.cf->get_address(), static_cast<std::int32_t>(i));
		}
	}
}

void isoshim_reply(const uint8_t *data, int32_t len) {
	if (data && len > 0) g_replies.emplace_back(data, data + len);
}

int32_t isoshim_soft_key(int32_t terminal, uint16_t objectId, int32_t state) {
	const auto t = terminalAt(terminal);
	if (!t || state < 0 || state > 3) return 0;
	const bool sent = t->key(objectId, state, false);
	flushTransmit();
	return sent ? 1 : 0;
}

int32_t isoshim_button(int32_t terminal, uint16_t objectId, int32_t state) {
	const auto t = terminalAt(terminal);
	if (!t || state < 0 || state > 3) return 0;
	const bool sent = t->key(objectId, state, true);
	flushTransmit();
	return sent ? 1 : 0;
}

int32_t isoshim_select(int32_t terminal, uint16_t objectId, int32_t open) {
	const auto t = terminalAt(terminal);
	if (!t) return 0;
	const bool done = t->select(objectId, open != 0);
	flushTransmit();
	return done ? 1 : 0;
}

int32_t isoshim_set_number(int32_t terminal, uint16_t objectId, uint32_t value) {
	const auto t = terminalAt(terminal);
	if (!t) return 0;
	const int result = t->setNumber(objectId, value);
	flushTransmit();
	return result;
}

int32_t isoshim_set_string(int32_t terminal, uint16_t objectId, const char *utf8, int32_t len) {
	const auto t = terminalAt(terminal);
	if (!t || !utf8 || len < 0) return 0;
	const int result = t->setString(objectId, std::string(utf8, static_cast<std::size_t>(len)));
	flushTransmit();
	return result;
}

int32_t isoshim_link_up(void) {
	if (g_servers.empty()) return 0;
	// Each terminal's own claim first: one sent while the link was down never reached the
	// implements, which then ignore a terminal whose NAME they don't know at its address. (The
	// stack sends it only when it claims, or on a request it hears; it never hears its own.)
	// Sent as the stack would (priority 6, PGN 0xEE00 to everyone), through the hardware hook.
	bool sent = true;
	for (const auto &server : g_servers) {
		if (!server.cf->get_address_valid()) continue;
		isobus::CANMessageFrame frame{};
		frame.channel = 0;
		frame.identifier = (6u << 26) | (static_cast<std::uint32_t>(isobus::CANLibParameterGroupNumber::AddressClaim) << 8) |
		  (static_cast<std::uint32_t>(isobus::BROADCAST_CAN_ADDRESS) << 8) | server.cf->get_address();
		frame.isExtendedFrame = true;
		frame.dataLength = 8;
		const std::uint64_t name = server.cf->get_NAME().get_full_name();
		for (std::size_t i = 0; i < 8; i++) frame.data[i] = static_cast<std::uint8_t>(name >> (8 * i));
		sent = isobus::send_can_message_frame_to_hardware(frame) && sent;
	}
	// Then everyone else's: implements already on the bus whose claims we never heard.
	sent = isobus::CANNetworkManager::CANNetwork.send_request_for_address_claim(0) && sent;
	flushTransmit();
	return sent ? 1 : 0;
}

int32_t isoshim_escape(int32_t terminal) {
	const auto t = terminalAt(terminal);
	if (!t) return 0;
	const bool done = t->escape();
	flushTransmit();
	return done ? 1 : 0;
}

int32_t isoshim_activate(int32_t terminal, uint64_t name) {
	const auto t = terminalAt(terminal);
	if (!t) return 0;
	const bool done = t->activate(name);
	flushTransmit();
	return done ? 1 : 0;
}

// Everything goes while the network manager is still alive: left to the end of the process, these
// objects would unregister from one that has already been destroyed.
void isoshim_stop(void) {
	for (const auto &implement : g_implements) {
		implement->client->terminate();
		isobus::CANNetworkManager::CANNetwork.remove_any_control_function_parameter_group_number_callback(
		  static_cast<std::uint32_t>(isobus::CANLibParameterGroupNumber::VirtualTerminalToECU), heardEscResponse, implement.get());
		implement->client.reset();
	}
	for (auto &server : g_servers) server.terminal.reset(); // joins any parsing thread
	for (const auto &implement : g_implements) {
		isobus::CANNetworkManager::CANNetwork.deactivate_control_function(implement->partner);
		isobus::CANNetworkManager::CANNetwork.deactivate_control_function(implement->cf);
	}
	for (const auto &server : g_servers) isobus::CANNetworkManager::CANNetwork.deactivate_control_function(server.cf);
	g_implements.clear();
	g_servers.clear();
	g_tx.clear();
	{
		std::lock_guard<std::mutex> lock(g_rxMutex);
		g_rx.clear();
	}
	g_channels = 0;
	g_host = {};
}

} // extern "C"
