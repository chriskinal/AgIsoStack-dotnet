// See render.hpp. The rules are ISO 11783-6 Annex B's, read from the standard and upstream's
// object classes; GPL renderers (AgIsoVirtualTerminal) were not used.
#include "render.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <set>

#include "isobus/isobus/isobus_virtual_terminal_objects.hpp"
#include "isobus/isobus/isobus_virtual_terminal_server_managed_working_set.hpp"

namespace isoshim {

// ---- JSON ----

void Json::separate() {
	if (afterKey_) {
		afterKey_ = false;
		return;
	}
	if (!first_.empty()) {
		if (!first_.back()) out_ += ',';
		first_.back() = false;
	}
}

Json &Json::begin_object() {
	separate();
	out_ += '{';
	first_.push_back(true);
	return *this;
}

Json &Json::end_object() {
	out_ += '}';
	first_.pop_back();
	return *this;
}

Json &Json::begin_array() {
	separate();
	out_ += '[';
	first_.push_back(true);
	return *this;
}

Json &Json::end_array() {
	out_ += ']';
	first_.pop_back();
	return *this;
}

Json &Json::key(const char *name) {
	separate();
	out_ += '"';
	out_ += name;
	out_ += "\":";
	afterKey_ = true;
	return *this;
}

Json &Json::value(const std::string &text) {
	separate();
	out_ += '"';
	for (const unsigned char c : text) {
		switch (c) {
			case '"': out_ += "\\\""; break;
			case '\\': out_ += "\\\\"; break;
			case '\n': out_ += "\\n"; break;
			case '\r': out_ += "\\r"; break;
			case '\t': out_ += "\\t"; break;
			default:
				if (c < 0x20) {
					char buf[8];
					std::snprintf(buf, sizeof buf, "\\u%04x", c);
					out_ += buf;
				} else {
					out_ += static_cast<char>(c);
				}
		}
	}
	out_ += '"';
	return *this;
}

Json &Json::value(const char *text) { return value(std::string(text)); }

Json &Json::value(int number) {
	separate();
	out_ += std::to_string(number);
	return *this;
}

Json &Json::value(double number) {
	separate();
	char buf[32];
	std::snprintf(buf, sizeof buf, "%.2f", number);
	std::string s(buf);
	while (s.back() == '0') s.pop_back();
	if (s.back() == '.') s.pop_back();
	out_ += s == "-0" ? "0" : s;
	return *this;
}

Json &Json::value(bool flag) {
	separate();
	out_ += flag ? "true" : "false";
	return *this;
}

Json &Json::null() {
	separate();
	out_ += "null";
	return *this;
}

std::string hexName(std::uint64_t name) {
	char buf[17];
	std::snprintf(buf, sizeof buf, "%016llX", static_cast<unsigned long long>(name));
	return buf;
}

// ---- The renderer ----

namespace {

using namespace isobus;
using Type = VirtualTerminalObjectType;
using WS = VirtualTerminalServerManagedWorkingSet;

constexpr int MaxDepth = 32; // pools can point in circles
constexpr double Pi = 3.14159265358979323846;

} // namespace

// Text in a pool is ISO 8859-1, or UTF-16 little endian after a 0xFF 0xFE mark: UTF-8 out.
std::string utf8(const std::string &in) {
	std::string out;
	auto put = [&out](std::uint32_t c) {
		if (c < 0x80) {
			out += static_cast<char>(c);
		} else if (c < 0x800) {
			out += static_cast<char>(0xC0 | (c >> 6));
			out += static_cast<char>(0x80 | (c & 0x3F));
		} else {
			out += static_cast<char>(0xE0 | (c >> 12));
			out += static_cast<char>(0x80 | ((c >> 6) & 0x3F));
			out += static_cast<char>(0x80 | (c & 0x3F));
		}
	};
	if (in.size() >= 2 && static_cast<unsigned char>(in[0]) == 0xFF && static_cast<unsigned char>(in[1]) == 0xFE) {
		for (std::size_t i = 2; i + 1 < in.size(); i += 2) {
			const std::uint32_t c = static_cast<unsigned char>(in[i]) | (static_cast<unsigned char>(in[i + 1]) << 8);
			put(c >= 0xD800 && c < 0xE000 ? 0xFFFD : c); // surrogate pairs are outside the standard's code planes
		}
	} else {
		for (const unsigned char c : in) {
			if (c == 0) break;
			put(c);
		}
	}
	return out;
}

// The characters a pool string holds: wide ones after the mark are two bytes each.
std::size_t textLength(const std::string &raw) {
	if (raw.size() >= 2 && static_cast<unsigned char>(raw[0]) == 0xFF && static_cast<unsigned char>(raw[1]) == 0xFE) return (raw.size() - 2) / 2;
	return raw.size();
}

// UTF-8 in, Unicode code points out; a malformed sequence becomes U+FFFD, one byte at a time.
std::vector<std::uint32_t> codePoints(const std::string &text) {
	std::vector<std::uint32_t> out;
	for (std::size_t i = 0; i < text.size();) {
		const unsigned char c = static_cast<unsigned char>(text[i]);
		const std::size_t extra = c < 0x80 ? 0 : (c >> 5) == 6 ? 1 : (c >> 4) == 14 ? 2 : (c >> 3) == 30 ? 3 : 4;
		std::uint32_t cp = c & (0x7F >> extra);
		bool ok = extra < 4 && i + extra < text.size();
		for (std::size_t k = 1; ok && k <= extra; ++k) {
			const unsigned char d = static_cast<unsigned char>(text[i + k]);
			ok = (d >> 6) == 2;
			cp = (cp << 6) | (d & 0x3F);
		}
		out.push_back(ok ? cp : 0xFFFD);
		i += ok ? extra + 1 : 1;
	}
	return out;
}

bool poolText(const std::string &text, const std::string &current, std::string &out) {
	const bool wide = current.size() >= 2 && static_cast<unsigned char>(current[0]) == 0xFF && static_cast<unsigned char>(current[1]) == 0xFE;
	const std::size_t length = textLength(current);
	const auto cps = codePoints(text);
	if (cps.size() > length) return false;
	out.clear();
	if (wide) out += "\xFF\xFE";
	for (std::size_t i = 0; i < length; ++i) {
		const std::uint32_t c = i < cps.size() ? cps[i] : ' ';
		if (wide) {
			if (c > 0xFFFF || (c >= 0xD800 && c < 0xE000)) return false;
			out += static_cast<char>(c & 0xFF);
			out += static_cast<char>(c >> 8);
		} else {
			if (c > 0xFF) return false;
			out += static_cast<char>(c);
		}
	}
	return true;
}

namespace {

std::string formatNumber(const NumberVTObject &n, std::uint32_t raw, int fieldChars) {
	const int decimals = std::min<int>(n.get_number_of_decimals(), 7);
	double value = (static_cast<double>(raw) + n.get_offset()) * n.get_scale();
	const double factor = std::pow(10.0, decimals);
	if (n.get_option(NumberVTObject::Options::Truncate))
		value = std::trunc(value * factor) / factor;
	else
		value = std::round(value * factor) / factor;
	if (n.get_option(NumberVTObject::Options::DisplayZeroAsBlank) && std::fabs(value) < 0.5 / factor) return "";
	char buf[64];
	if (n.get_format())
		std::snprintf(buf, sizeof buf, "%.*e", decimals, value);
	else
		std::snprintf(buf, sizeof buf, "%.*f", decimals, value);
	std::string s(buf);
	if (s == "-0" || (s[0] == '-' && std::fabs(value) < 0.5 / factor)) s.erase(0, 1);
	if (n.get_option(NumberVTObject::Options::DisplayLeadingZeros)) {
		const std::size_t sign = s[0] == '-' ? 1 : 0;
		while (static_cast<int>(s.size()) < fieldChars) s.insert(sign, 1, '0');
	}
	return s;
}

} // namespace

struct Renderer::Pass {
	WS &ws;
	Json &json;
	isoshim_image_fn image;
	const std::map<std::uint16_t, std::shared_ptr<VTObject>> &tree;
	std::uint16_t focus = NULL_OBJECT_ID; // the selected input, marked on its hit
	bool open = false;                    // and whether it is open for input
	std::set<std::uint16_t> *touchable = nullptr; // collects the enabled inputs and buttons drawn

	std::shared_ptr<VTObject> find(std::uint16_t id) const {
		const auto it = tree.find(id);
		return it == tree.end() ? nullptr : it->second;
	}
	template<typename T>
	std::shared_ptr<T> find(std::uint16_t id, Type type) const {
		const auto object = find(id);
		return object && object->get_object_type() == type ? std::static_pointer_cast<T>(object) : nullptr;
	}
	std::string colour(std::uint8_t index) const {
		const auto c = ws.get_colour(index);
		char buf[8];
		std::snprintf(buf, sizeof buf, "#%02x%02x%02x", static_cast<int>(std::lround(c.r * 255)),
		              static_cast<int>(std::lround(c.g * 255)), static_cast<int>(std::lround(c.b * 255)));
		return buf;
	}
	// A number object's or list's value: its variable's when it has one.
	std::uint32_t number(std::uint16_t variable, std::uint32_t own) const {
		const auto v = find<NumberVariable>(variable, Type::NumberVariable);
		return v ? v->get_value() : own;
	}
	std::string text(std::uint16_t variable, const std::string &own) const {
		const auto v = find<StringVariable>(variable, Type::StringVariable);
		return utf8(v ? v->get_value() : own);
	}

	void item(const char *t, std::uint16_t id) { json.begin_object().key("t").value(t).key("o").value(static_cast<int>(id)); }
	void box(const char *t, std::uint16_t id, int x, int y, int w, int h) {
		item(t, id);
		json.key("x").value(x).key("y").value(y).key("w").value(w).key("h").value(h);
	}
	void clip(std::uint16_t id, int x, int y, int w, int h) {
		box("clip", id, x, y, w, h);
		json.end_object();
	}
	void unclip(std::uint16_t id) {
		item("unclip", id);
		json.end_object();
	}
	void hit(std::uint16_t id, int x, int y, int w, int h, const char *kind, bool on, bool latch = false, bool latched = false) {
		box("hit", id, x, y, w, h);
		json.key("kind").value(kind).key("on").value(on);
		if (latch) json.key("latch").value(true).key("latched").value(latched);
		if (id == focus) {
			json.key("sel").value(true);
			if (open) json.key("open").value(true);
		}
		if (on && touchable) touchable->insert(id);
		json.end_object();
	}
	void labelled(std::uint16_t id, int x, int y, int w, int h, const char *label) {
		box("box", id, x, y, std::max(w, 8), std::max(h, 8));
		json.key("label").value(label).end_object();
	}

	// Line attributes: colour, width (0: no line) and the 16 pixel art pattern.
	struct Line {
		std::string colour;
		int width = 0;
		int art = 0xFFFF;
	};
	Line line(std::uint16_t id) const {
		Line l;
		if (const auto a = find<LineAttributes>(id, Type::LineAttributes)) {
			l.colour = colour(a->get_background_color());
			l.width = a->get_width();
			l.art = a->get_line_art_bit_pattern();
		}
		return l;
	}
	void stroke(const Line &l) {
		if (l.width <= 0) return;
		json.key("line").value(l.colour).key("lw").value(l.width);
		if (l.art != 0xFFFF) json.key("dash").value(l.art);
	}

	// Fill attributes: a colour, a picture (picture: its image id, 0 if none), or nothing.
	template<typename Picture>
	void fill(std::uint16_t id, const Line &l, Picture picture) {
		const auto a = find<FillAttributes>(id, Type::FillAttributes);
		if (!a) return;
		switch (a->get_type()) {
			case FillAttributes::FillType::FillWithLineColor:
				if (!l.colour.empty()) json.key("fill").value(l.colour);
				break;
			case FillAttributes::FillType::FillWithSpecifiedColorInFillColorAttribute:
				json.key("fill").value(colour(a->get_background_color()));
				break;
			case FillAttributes::FillType::FillWithPatternGivenByFillPatternAttribute: {
				const int image = picture(a->get_fill_pattern());
				if (image > 0)
					json.key("pat").value(image);
				else
					json.key("fill").value(colour(a->get_background_color()));
				break;
			}
			default: break;
		}
	}
};

std::string Renderer::screen(WS &ws, std::uint64_t name, std::uint16_t maskWidth, std::uint16_t maskHeight, std::uint8_t keyWidth,
                             std::uint8_t keyHeight, std::uint16_t focus, bool open, isoshim_image_fn image) {
	Json json;
	touchable_.clear();
	Pass pass{ ws, json, image, ws.get_object_tree(), focus, open, &touchable_ };
	json.begin_object().key("type").value("screen").key("ws").value(hexName(name));

	const auto workingSet = std::static_pointer_cast<WorkingSet>(ws.get_working_set_object());
	const std::uint16_t maskId = workingSet->get_active_mask();
	const auto mask = pass.find(maskId);
	std::uint16_t keysId = NULL_OBJECT_ID;
	const char *kind = nullptr;
	if (mask && mask->get_object_type() == Type::DataMask) {
		kind = "data";
		keysId = std::static_pointer_cast<DataMask>(mask)->get_soft_key_mask();
	} else if (mask && mask->get_object_type() == Type::AlarmMask) {
		kind = "alarm";
		keysId = std::static_pointer_cast<AlarmMask>(mask)->get_soft_key_mask();
	}
	json.key("mask").value(static_cast<int>(maskId));
	if (!kind) {
		json.key("kind").null().key("items").begin_array().end_array().key("keys").null().end_object();
		return json.str();
	}
	json.key("kind").value(kind).key("w").value(static_cast<int>(maskWidth)).key("h").value(static_cast<int>(maskHeight));
	json.key("bg").value(pass.colour(mask->get_background_color()));
	if (kind[0] == 'a') {
		const auto alarm = std::static_pointer_cast<AlarmMask>(mask);
		json.key("priority").value(static_cast<int>(alarm->get_mask_priority()));
		json.key("signal").value(static_cast<int>(alarm->get_signal_priority()));
	}
	json.key("items").begin_array();
	for (std::uint16_t i = 0; i < mask->get_number_children(); ++i) draw(pass, mask->get_child_id(i), mask->get_child_x(i), mask->get_child_y(i), 1);
	json.end_array();

	// The soft keys, one cell each in order; a pointer to nothing keeps its place empty.
	json.key("keys");
	const auto keys = pass.find<SoftKeyMask>(keysId, Type::SoftKeyMask);
	if (!keys) {
		json.null();
	} else {
		json.begin_object().key("mask").value(static_cast<int>(keysId)).key("w").value(static_cast<int>(keyWidth));
		json.key("h").value(static_cast<int>(keyHeight)).key("bg").value(pass.colour(keys->get_background_color()));
		json.key("keys").begin_array();
		for (std::uint16_t i = 0; i < keys->get_number_children(); ++i) {
			std::uint16_t id = keys->get_child_id(i);
			if (const auto pointer = pass.find<ObjectPointer>(id, Type::ObjectPointer)) id = pointer->get_value();
			const auto key = pass.find<Key>(id, Type::Key);
			if (!key) {
				json.null();
				continue;
			}
			json.begin_object().key("o").value(static_cast<int>(id)).key("code").value(static_cast<int>(key->get_key_code()));
			json.key("bg").value(pass.colour(key->get_background_color())).key("items").begin_array();
			for (std::uint16_t c = 0; c < key->get_number_children(); ++c) draw(pass, key->get_child_id(c), key->get_child_x(c), key->get_child_y(c), 2);
			json.end_array().end_object();
		}
		json.end_array().end_object();
	}
	json.end_object();
	return json.str();
}

// The editor for one input field (docs/API.md, the edit message).
std::string Renderer::edit(WS &ws, std::uint64_t name, std::uint16_t id, isoshim_image_fn image) {
	Json json;
	Pass pass{ ws, json, image, ws.get_object_tree() };
	const auto object = pass.find(id);
	json.begin_object().key("type").value("edit").key("ws").value(hexName(name)).key("o");
	if (!object) {
		json.null().end_object();
		return json.str();
	}
	json.value(static_cast<int>(id));
	switch (object->get_object_type()) {
		case Type::InputNumber: {
			const auto n = std::static_pointer_cast<InputNumber>(object);
			const std::uint32_t value = pass.number(n->get_variable_reference(), n->get_value());
			json.key("kind").value("number").key("value").value(static_cast<double>(value));
			json.key("min").value(static_cast<double>(n->get_minimum_value())).key("max").value(static_cast<double>(n->get_maximum_value()));
			json.key("offset").value(static_cast<double>(n->get_offset())).key("scale").value(static_cast<double>(n->get_scale()));
			json.key("decimals").value(static_cast<int>(n->get_number_of_decimals()));
			json.key("text").value(formatNumber(*n, value, 0));
			break;
		}
		case Type::InputBoolean: {
			const auto b = std::static_pointer_cast<InputBoolean>(object);
			json.key("kind").value("bool").key("value").value(static_cast<int>(pass.number(b->get_variable_reference(), b->get_value()) != 0));
			break;
		}
		case Type::InputString: {
			const auto s = std::static_pointer_cast<InputString>(object);
			const auto variable = pass.find<StringVariable>(s->get_variable_reference(), Type::StringVariable);
			const std::string raw = variable ? variable->get_value() : s->get_value();
			std::string value = utf8(raw);
			while (!value.empty() && value.back() == ' ') value.pop_back();
			json.key("kind").value("string").key("value").value(value).key("length").value(static_cast<int>(textLength(raw)));
			if (const auto a = pass.find<InputAttributes>(s->get_input_attributes(), Type::InputAttributes)) {
				json.key("chars").value(utf8(a->get_validation_string()));
				json.key("only").value(a->get_validation_type() == InputAttributes::ValidationType::ValidCharactersAreListed);
			}
			break;
		}
		case Type::InputList: {
			// Each item drawn on its own, in the field's size, at 0,0; an empty place is null.
			const auto l = std::static_pointer_cast<InputList>(object);
			json.key("kind").value("list").key("value").value(static_cast<int>(pass.number(l->get_variable_reference(), l->get_value())));
			json.key("w").value(static_cast<int>(object->get_width())).key("h").value(static_cast<int>(object->get_height()));
			json.key("options").begin_array();
			for (std::uint16_t i = 0; i < object->get_number_children(); ++i) {
				const std::uint16_t item = object->get_child_id(i);
				if (item == NULL_OBJECT_ID || !pass.find(item)) {
					json.null();
					continue;
				}
				json.begin_array();
				draw(pass, item, 0, 0, 1);
				json.end_array();
			}
			json.end_array();
			break;
		}
		default: json.key("kind").null(); break;
	}
	json.end_object();
	return json.str();
}

void Renderer::forget(const WS *ws) {
	for (auto it = images_.begin(); it != images_.end();) it = it->first.first == ws ? images_.erase(it) : std::next(it);
}

// A picture graphic as RGBA through the working set's colour table, sent once; its image id.
int Renderer::picture(Pass &pass, std::uint16_t id) {
	const auto object = pass.find<PictureGraphic>(id, Type::PictureGraphic);
	if (!object || !pass.image) return 0;
	auto &cached = images_[{ &pass.ws, id }];
	if (cached.second == object.get()) return static_cast<int>(cached.first);
	const int w = object->get_actual_width(), h = object->get_actual_height();
	const auto &pixels = object->get_raw_data();
	const bool transparent = object->get_option(PictureGraphic::Options::Transparent);
	const std::uint8_t clear = object->get_transparency_colour();
	std::vector<std::uint8_t> rgba(static_cast<std::size_t>(w) * h * 4, 0);
	const std::size_t n = std::min(pixels.size(), static_cast<std::size_t>(w) * h);
	for (std::size_t i = 0; i < n; ++i) {
		std::uint8_t index = pixels[i];
		// A monochrome picture's 0 and 1 are black and white (colours 0 and 1 of the table).
		const auto c = pass.ws.get_colour(index);
		rgba[i * 4 + 0] = static_cast<std::uint8_t>(std::lround(c.r * 255));
		rgba[i * 4 + 1] = static_cast<std::uint8_t>(std::lround(c.g * 255));
		rgba[i * 4 + 2] = static_cast<std::uint8_t>(std::lround(c.b * 255));
		rgba[i * 4 + 3] = transparent && index == clear ? 0 : 255;
	}
	cached = { nextImage_++, object.get() };
	pass.image(cached.first, w, h, rgba.data());
	return static_cast<int>(cached.first);
}

void Renderer::draw(Pass &pass, std::uint16_t id, int x, int y, int depth) {
	if (id == NULL_OBJECT_ID || depth > MaxDepth) return;
	const auto object = pass.find(id);
	if (!object) return;
	auto &json = pass.json;
	const int w = object->get_width(), h = object->get_height();
	const auto picture = [this, &pass](std::uint16_t p) { return this->picture(pass, p); };
	const auto children = [&](int dx, int dy) {
		for (std::uint16_t i = 0; i < object->get_number_children(); ++i)
			draw(pass, object->get_child_id(i), x + dx + object->get_child_x(i), y + dy + object->get_child_y(i), depth + 1);
	};
	// Text in a box: a string or a number, its font, its own background unless transparent.
	const auto text = [&](const std::string &s, const TextualVTObject &t, bool transparent, bool wrap) {
		pass.box("text", id, x, y, w, h);
		json.key("s").value(s);
		if (!transparent) json.key("bg").value(pass.colour(t.get_background_color()));
		if (const auto font = pass.find<FontAttributes>(t.get_font_attributes(), Type::FontAttributes)) {
			json.key("c").value(pass.colour(font->get_colour()));
			json.key("fw").value(static_cast<int>(font->get_font_width_pixels())).key("fh").value(static_cast<int>(font->get_font_height_pixels()));
			if (font->get_style()) json.key("style").value(static_cast<int>(font->get_style()));
		}
		json.key("hj").value(static_cast<int>(t.get_horizontal_justification()));
		json.key("vj").value(static_cast<int>(t.get_vertical_justification()));
		if (wrap) json.key("wrap").value(true);
		json.end_object();
	};
	const auto fontWidth = [&](const TextualVTObject &t) {
		const auto font = pass.find<FontAttributes>(t.get_font_attributes(), Type::FontAttributes);
		return font && font->get_font_width_pixels() ? font->get_font_width_pixels() : 8;
	};

	switch (object->get_object_type()) {
		case Type::Container: {
			if (std::static_pointer_cast<Container>(object)->get_hidden()) return;
			pass.clip(id, x, y, w, h);
			children(0, 0);
			pass.unclip(id);
			return;
		}
		case Type::ObjectPointer:
			draw(pass, std::static_pointer_cast<ObjectPointer>(object)->get_value(), x, y, depth + 1);
			return;
		case Type::Button: {
			const auto b = std::static_pointer_cast<Button>(object);
			const bool border = !b->get_option(Button::Options::NoBorder) && !b->get_option(Button::Options::SuppressBorder);
			const bool latchable = b->get_option(Button::Options::Latchable);
			const bool latched = latchable && b->get_option(Button::Options::CurrentButtonStateIfLatchable);
			pass.box("rect", id, x, y, w, h);
			if (!b->get_option(Button::Options::TransparentBackground)) json.key("fill").value(pass.colour(b->get_background_color()));
			if (border) json.key("line").value(pass.colour(b->get_border_colour())).key("lw").value(latched ? 3 : 2);
			json.end_object();
			// The face inside the border: the children are placed and clipped there.
			const int inset = border ? 4 : 0;
			pass.clip(id, x + inset, y + inset, w - 2 * inset, h - 2 * inset);
			children(inset, inset);
			pass.unclip(id);
			pass.hit(id, x, y, w, h, "button", !b->get_option(Button::Options::Disabled), latchable, latched);
			return;
		}
		case Type::OutputString:
		case Type::InputString: {
			const auto s = std::static_pointer_cast<StringVTObject>(object);
			text(pass.text(s->get_variable_reference(), s->get_value()), *s, s->get_option(StringVTObject::Options::Transparent),
			     s->get_option(StringVTObject::Options::AutoWrap));
			if (object->get_object_type() == Type::InputString)
				pass.hit(id, x, y, w, h, "input", std::static_pointer_cast<InputString>(object)->get_enabled());
			return;
		}
		case Type::OutputNumber:
		case Type::InputNumber: {
			const auto n = std::static_pointer_cast<NumberVTObject>(object);
			const std::uint32_t value = pass.number(n->get_variable_reference(), n->get_value());
			text(formatNumber(*n, value, w / fontWidth(*n)), *n, n->get_option(NumberVTObject::Options::Transparent), false);
			if (object->get_object_type() == Type::InputNumber)
				pass.hit(id, x, y, w, h, "input", std::static_pointer_cast<InputNumber>(object)->get_option2(InputNumber::Options2::Enabled));
			return;
		}
		case Type::InputBoolean: {
			const auto b = std::static_pointer_cast<InputBoolean>(object);
			const bool on = pass.number(b->get_variable_reference(), b->get_value()) != 0;
			pass.box("rect", id, x, y, w, w);
			json.key("fill").value(pass.colour(b->get_background_color())).end_object();
			if (on) {
				// A check mark in the foreground colour (the font attributes' colour).
				const auto font = pass.find<FontAttributes>(b->get_foreground_colour_object_id(), Type::FontAttributes);
				pass.item("poly", id);
				json.key("pts").begin_array();
				for (const auto &p : { std::make_pair(0.2, 0.5), std::make_pair(0.42, 0.75), std::make_pair(0.8, 0.25) })
					json.value(x + p.first * w).value(y + p.second * w);
				json.end_array().key("closed").value(false);
				json.key("line").value(pass.colour(font ? font->get_colour() : 0)).key("lw").value(std::max(2, w / 8)).end_object();
			}
			pass.hit(id, x, y, w, w, "input", b->get_enabled());
			return;
		}
		case Type::OutputList:
		case Type::InputList: {
			const auto l = std::static_pointer_cast<ListVTObject>(object);
			const std::uint32_t index = pass.number(l->get_variable_reference(), l->get_value());
			pass.clip(id, x, y, w, h);
			if (index < object->get_number_children()) draw(pass, object->get_child_id(static_cast<std::uint16_t>(index)), x, y, depth + 1);
			pass.unclip(id);
			if (object->get_object_type() == Type::InputList)
				pass.hit(id, x, y, w, h, "input", std::static_pointer_cast<InputList>(object)->get_option(InputList::Options::Enabled));
			return;
		}
		case Type::OutputLine: {
			const auto l = std::static_pointer_cast<OutputLine>(object);
			const bool up = l->get_line_direction() == OutputLine::LineDirection::BottomLeftToTopRight;
			pass.item("line", id);
			json.key("x1").value(x).key("y1").value(up ? y + h : y).key("x2").value(x + w).key("y2").value(up ? y : y + h);
			pass.stroke(pass.line(l->get_line_attributes()));
			json.end_object();
			return;
		}
		case Type::OutputRectangle: {
			const auto r = std::static_pointer_cast<OutputRectangle>(object);
			const auto line = pass.line(r->get_line_attributes());
			pass.box("rect", id, x, y, w, h);
			pass.fill(r->get_fill_attributes(), line, picture);
			pass.stroke(line);
			// Bits set: top, right, bottom, left suppressed.
			if (r->get_line_suppression_bitfield() & 0x0F) json.key("sides").value(~r->get_line_suppression_bitfield() & 0x0F);
			json.end_object();
			return;
		}
		case Type::OutputEllipse: {
			const auto e = std::static_pointer_cast<OutputEllipse>(object);
			const auto line = pass.line(e->get_line_attributes());
			pass.box("ellipse", id, x, y, w, h);
			const int kind = static_cast<int>(e->get_ellipse_type());
			if (kind) json.key("kind").value(kind).key("a0").value(e->get_start_angle() * 2).key("a1").value(e->get_end_angle() * 2);
			if (kind != 1) pass.fill(e->get_fill_attributes(), line, picture); // an open ellipse is not filled
			pass.stroke(line);
			json.end_object();
			return;
		}
		case Type::OutputPolygon: {
			const auto p = std::static_pointer_cast<OutputPolygon>(object);
			const auto line = pass.line(p->get_line_attributes());
			const bool open = p->get_type() == OutputPolygon::PolygonType::Open;
			pass.item("poly", id);
			json.key("pts").begin_array();
			for (std::uint8_t i = 0; i < p->get_number_of_points(); ++i) {
				const auto point = p->get_point(i);
				json.value(x + point.xValue).value(y + point.yValue);
			}
			json.end_array().key("closed").value(!open);
			if (!open) pass.fill(p->get_fill_attributes(), line, picture);
			pass.stroke(line);
			json.end_object();
			return;
		}
		case Type::OutputMeter: {
			// A circle of the object's width. Angles count anticlockwise from 3 o'clock in 2 degree steps.
			const auto m = std::static_pointer_cast<OutputMeter>(object);
			const double r = w / 2.0, cx = x + r, cy = y + r;
			const double a0 = m->get_start_angle() * 2, a1 = m->get_end_angle() * 2;
			const double sweep = std::fmod(a1 - a0 + 360.0, 360.0);
			const int lw = std::max(1, w / 60);
			if (m->get_option(OutputMeter::Options::DrawBorder)) {
				pass.box("ellipse", id, x, y, w, w);
				json.key("line").value(pass.colour(m->get_border_colour())).key("lw").value(lw).end_object();
			}
			if (m->get_option(OutputMeter::Options::DrawArc)) {
				pass.box("ellipse", id, x + lw * 3, y + lw * 3, w - lw * 6, w - lw * 6);
				json.key("kind").value(1).key("a0").value(a0).key("a1").value(a1);
				json.key("line").value(pass.colour(m->get_arc_and_tick_colour())).key("lw").value(lw).end_object();
			}
			const auto ray = [&](double degrees, double from, double to, const std::string &colour, int width) {
				const double a = degrees * Pi / 180.0;
				pass.item("line", id);
				json.key("x1").value(cx + from * std::cos(a)).key("y1").value(cy - from * std::sin(a));
				json.key("x2").value(cx + to * std::cos(a)).key("y2").value(cy - to * std::sin(a));
				json.key("line").value(colour).key("lw").value(width).end_object();
			};
			const int ticks = m->get_number_of_ticks();
			if (m->get_option(OutputMeter::Options::DrawTicks) && ticks > 0)
				for (int i = 0; i < ticks; ++i)
					ray(a0 + sweep * (ticks == 1 ? 0 : static_cast<double>(i) / (ticks - 1)), r * 0.75, r * 0.9, pass.colour(m->get_arc_and_tick_colour()), lw);
			const double lo = m->get_min_value(), hi = m->get_max_value();
			const double v = pass.number(m->get_variable_reference(), m->get_value());
			const double f = hi > lo ? std::clamp((v - lo) / (hi - lo), 0.0, 1.0) : 0.0;
			// Clockwise deflection runs from the end angle back towards the start.
			const bool clockwise = m->get_option(OutputMeter::Options::DeflectionDirection);
			ray(clockwise ? a1 - f * sweep : a0 + f * sweep, 0, r * 0.85, pass.colour(m->get_needle_colour()), lw * 2);
			return;
		}
		case Type::OutputLinearBarGraph: {
			const auto g = std::static_pointer_cast<OutputLinearBarGraph>(object);
			const std::string colour = pass.colour(g->get_colour());
			const double lo = g->get_min_value(), hi = g->get_max_value();
			const auto fraction = [&](double v) { return hi > lo ? std::clamp((v - lo) / (hi - lo), 0.0, 1.0) : 0.0; };
			const double f = fraction(pass.number(g->get_variable_reference(), g->get_value()));
			const double target = fraction(pass.number(g->get_target_value_reference(), g->get_target_value()));
			const bool horizontal = g->get_option(OutputLinearBarGraph::Options::AxisOrientation);
			const bool positive = g->get_option(OutputLinearBarGraph::Options::Direction);
			// Where a fraction lies along the axis: positive grows rightwards or upwards.
			const auto along = [&](double t) {
				if (horizontal) return positive ? x + t * w : x + w - t * w;
				return positive ? y + h - t * h : y + t * h;
			};
			const auto bar = [&](double from, double to) {
				const double a = std::min(along(from), along(to)), b = std::max(along(from), along(to));
				pass.item("rect", id);
				if (horizontal)
					json.key("x").value(a).key("y").value(y).key("w").value(b - a).key("h").value(h);
				else
					json.key("x").value(x).key("y").value(a).key("w").value(w).key("h").value(b - a);
			};
			const auto mark = [&](double t, const std::string &c, double from, double to) {
				pass.item("line", id);
				const double p = along(t);
				if (horizontal)
					json.key("x1").value(p).key("y1").value(y + from * h).key("x2").value(p).key("y2").value(y + to * h);
				else
					json.key("x1").value(x + from * w).key("y1").value(p).key("x2").value(x + to * w).key("y2").value(p);
				json.key("line").value(c).key("lw").value(1).end_object();
			};
			if (g->get_option(OutputLinearBarGraph::Options::BarGraphType)) {
				mark(f, colour, 0, 1); // a line at the value
			} else {
				bar(0, f);
				json.key("fill").value(colour).end_object();
			}
			const int ticks = g->get_number_of_ticks();
			if (g->get_option(OutputLinearBarGraph::Options::DrawTicks) && ticks > 1)
				for (int i = 0; i < ticks; ++i) mark(static_cast<double>(i) / (ticks - 1), colour, 0, 0.2);
			if (g->get_option(OutputLinearBarGraph::Options::DrawTargetLine)) mark(target, pass.colour(g->get_target_line_colour()), 0, 1);
			if (g->get_option(OutputLinearBarGraph::Options::DrawBorder)) {
				pass.box("rect", id, x, y, w, h);
				json.key("line").value(colour).key("lw").value(1).end_object();
			}
			return;
		}
		case Type::OutputArchedBarGraph: {
			const auto g = std::static_pointer_cast<OutputArchedBarGraph>(object);
			const std::string colour = pass.colour(g->get_colour());
			const double rx = w / 2.0, ry = h / 2.0, cx = x + rx, cy = y + ry;
			const double bw = std::min<double>(g->get_bar_graph_width(), std::min(rx, ry));
			const double a0 = g->get_start_angle() * 2, a1 = g->get_end_angle() * 2;
			const double sweep = std::fmod(a1 - a0 + 360.0, 360.0);
			const double lo = g->get_min_value(), hi = g->get_max_value();
			const auto fraction = [&](double v) { return hi > lo ? std::clamp((v - lo) / (hi - lo), 0.0, 1.0) : 0.0; };
			const bool clockwise = g->get_option(OutputArchedBarGraph::Options::Deflection);
			const auto angle = [&](double t) { return clockwise ? a1 - t * sweep : a0 + t * sweep; };
			const auto at = [&](double degrees, double inset, double &px, double &py) {
				const double a = degrees * Pi / 180.0;
				px = cx + (rx - inset) * std::cos(a);
				py = cy - (ry - inset) * std::sin(a);
			};
			// The band between two angles, as a polygon along the outer edge and back along the inner.
			const auto band = [&](double from, double to) {
				const int steps = std::max(2, static_cast<int>(std::fabs(to - from) / 3));
				pass.item("poly", id);
				json.key("pts").begin_array();
				double px, py;
				for (int i = 0; i <= steps; ++i) {
					at(from + (to - from) * i / steps, 0, px, py);
					json.value(px).value(py);
				}
				for (int i = steps; i >= 0; --i) {
					at(from + (to - from) * i / steps, bw, px, py);
					json.value(px).value(py);
				}
				json.end_array().key("closed").value(true);
			};
			const auto radial = [&](double degrees, const std::string &c) {
				double x1, y1, x2, y2;
				at(degrees, 0, x1, y1);
				at(degrees, bw, x2, y2);
				pass.item("line", id);
				json.key("x1").value(x1).key("y1").value(y1).key("x2").value(x2).key("y2").value(y2);
				json.key("line").value(c).key("lw").value(2).end_object();
			};
			const double f = fraction(pass.number(g->get_variable_reference(), g->get_value()));
			if (g->get_option(OutputArchedBarGraph::Options::BarGraphType)) {
				radial(angle(f), colour);
			} else if (f > 0) {
				band(clockwise ? angle(f) : a0, clockwise ? a1 : angle(f));
				json.key("fill").value(colour).end_object();
			}
			if (g->get_option(OutputArchedBarGraph::Options::DrawTargetLine))
				radial(angle(fraction(pass.number(g->get_target_value_reference(), g->get_target_value()))), pass.colour(g->get_target_line_colour()));
			if (g->get_option(OutputArchedBarGraph::Options::DrawBorder)) {
				band(a0, a0 + sweep);
				json.key("line").value(colour).key("lw").value(1).end_object();
			}
			return;
		}
		case Type::PictureGraphic: {
			const int image = picture(id);
			if (image <= 0) {
				pass.labelled(id, x, y, w, h, "picture");
				return;
			}
			pass.box("image", id, x, y, w, h);
			json.key("img").value(image).end_object();
			return;
		}
		// Not drawn: values, attributes, macros and the like.
		case Type::NumberVariable:
		case Type::StringVariable:
		case Type::FontAttributes:
		case Type::LineAttributes:
		case Type::FillAttributes:
		case Type::InputAttributes:
		case Type::ExtendedInputAttributes:
		case Type::Macro:
		case Type::ColourMap:
		case Type::ObjectLabelRefrenceList:
		case Type::GraphicData:
		case Type::WorkingSetSpecialControls:
		case Type::AuxiliaryFunctionType1:
		case Type::AuxiliaryInputType1:
		case Type::AuxiliaryFunctionType2:
		case Type::AuxiliaryInputType2:
		case Type::AuxiliaryControlDesignatorType2:
		case Type::ExternalObjectDefinition:
		case Type::ExternalReferenceNAME:
			return;
		default: {
			char label[24];
			std::snprintf(label, sizeof label, "type %d", static_cast<int>(object->get_object_type()));
			pass.labelled(id, x, y, w, h, label);
			return;
		}
	}
}

} // namespace isoshim
