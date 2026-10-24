// The renderer: an implement's active mask and soft key mask flattened into a draw list (JSON,
// docs/API.md), and the small JSON writer the shim's messages use.
#pragma once
#include <cstdint>
#include <map>
#include <memory>
#include <string>
#include <utility>
#include <vector>

#include "isoshim.h"

namespace isobus {
class VTObject;
class VirtualTerminalServerManagedWorkingSet;
} // namespace isobus

namespace isoshim {

// Writes JSON in order; commas are placed for the caller.
class Json {
public:
	Json &begin_object();
	Json &end_object();
	Json &begin_array();
	Json &end_array();
	Json &key(const char *name);
	Json &value(const std::string &text);
	Json &value(const char *text);
	Json &value(int number);
	Json &value(double number);
	Json &value(bool flag);
	Json &null();
	const std::string &str() const { return out_; }

private:
	void separate();
	std::string out_;
	std::vector<bool> first_;
	bool afterKey_ = false;
};

// A NAME as 16 hex digits, the form the host sees it in.
std::string hexName(std::uint64_t name);

class Renderer {
public:
	// The screen message for one implement: its active data or alarm mask, its soft key mask.
	// Pictures not sent before go to image first.
	std::string screen(isobus::VirtualTerminalServerManagedWorkingSet &ws, std::uint64_t name, std::uint16_t maskWidth,
	                   std::uint16_t maskHeight, std::uint8_t keyWidth, std::uint8_t keyHeight, isoshim_image_fn image);
	// The implement has gone, or its pool was replaced: its pictures are sent again if needed.
	void forget(const isobus::VirtualTerminalServerManagedWorkingSet *ws);

private:
	struct Pass;
	void draw(Pass &pass, std::uint16_t id, int x, int y, int depth);
	int picture(Pass &pass, std::uint16_t id);

	// Pictures sent, by implement and object id, with the object they were made from: a new
	// pool's object of the same id is sent again.
	std::map<std::pair<const isobus::VirtualTerminalServerManagedWorkingSet *, std::uint16_t>,
	         std::pair<std::uint32_t, const isobus::VTObject *>>
	  images_;
	std::uint32_t nextImage_ = 1;
};

} // namespace isoshim
