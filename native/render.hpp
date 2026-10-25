// The renderer: an implement's active mask and soft key mask flattened into a draw list (JSON,
// docs/API.md), and the small JSON writer the shim's messages use.
#pragma once
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <set>
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

// Pool text (ISO 8859-1, or UTF-16 little endian after 0xFF 0xFE) as UTF-8.
std::string utf8(const std::string &raw);
// The characters pool text holds.
std::size_t textLength(const std::string &raw);
std::vector<std::uint32_t> codePoints(const std::string &utf8Text);
// UTF-8 text as a new value for a pool string that now holds current: the same encoding and
// length, padded with spaces. False if it is longer or has a character the encoding lacks.
bool poolText(const std::string &utf8Text, const std::string &current, std::string &out);

// Where a picture goes: id, width, height, RGBA rows top first.
using ImageSink = std::function<void(std::uint32_t, std::int32_t, std::int32_t, const std::uint8_t *)>;

class Renderer {
public:
	// The screen message for one implement: its active data or alarm mask, its soft key mask.
	// Pictures not sent before go to image first.
	// The focused input's hit is marked selected, and open when it is open for input.
	std::string screen(isobus::VirtualTerminalServerManagedWorkingSet &ws, std::uint64_t name, std::uint16_t maskWidth,
	                   std::uint16_t maskHeight, std::uint8_t keyWidth, std::uint8_t keyHeight, std::uint16_t focus, bool open,
	                   const ImageSink &image);
	// The edit message for one input field: what an editor for it needs.
	std::string edit(isobus::VirtualTerminalServerManagedWorkingSet &ws, std::uint64_t name, std::uint16_t id, const ImageSink &image);
	// Whether the last screen drew this input or button, enabled.
	bool touchable(std::uint16_t id) const { return touchable_.count(id) != 0; }
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
	std::set<std::uint16_t> touchable_;
};

} // namespace isoshim
