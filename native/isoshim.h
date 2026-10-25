// A C interface over AgIsoStack++ for .NET: a virtual terminal whose CAN frames are carried by the
// caller, and whose screen leaves as a draw list (docs/API.md).
//
// Threading: the stack runs on the caller's thread and nowhere else. isoshim_receive may be called
// from any thread (it only queues the frame); every other function, and every callback except the
// log, runs on the one thread that calls isoshim_step. The stack's hardware layer and its threads
// are not used: the caller is the bus.
//
// One to four terminals (ISOBUS VTs: each its own NAME and address, as several displays in one cab)
// share channel 0; each implement connects to the one it chooses. Every call for one terminal and
// every callback from one names it by its position, 0 to count - 1. isoshim_test_implement adds
// upstream's VT client on channel 1 for the self-test; the caller then hands each frame one
// channel sends to the other.
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define ISOSHIM_API __declspec(dllexport)
#else
#define ISOSHIM_API __attribute__((visibility("default")))
#endif

// Every frame a channel transmits, from isoshim_step or an input call, on the caller's thread. It
// may call isoshim_receive.
typedef void (*isoshim_send_fn)(int32_t channel, uint32_t id, const uint8_t *data, int32_t len, int32_t extended);

enum isoshim_event {
	ISOSHIM_SERVER_ADDRESS = 1, // a: address the terminal claimed
	ISOSHIM_CLIENT_ADDRESS = 2, // a: address a test implement claimed, b: which (terminal: the one it is for)
	ISOSHIM_POOL_OK = 3,        // a: objects in the parsed pool, b: the implement's address
	ISOSHIM_POOL_BAD = 4,       // a: faulty object id, b: the implement's address
	ISOSHIM_MASK = 5,           // a: the active implement's active data or alarm mask, b: its address
	ISOSHIM_PARSE_START = 7,    // a pool has arrived and is being parsed, b: the implement's address
	ISOSHIM_WS_SHUTDOWN = 8,    // an implement went away, a: reason (0 maintenance timeout, 1 re-initiated), b: its address
	ISOSHIM_ACTIVE = 9,         // a: the address of the implement now shown (-1: none)
};
// From isoshim_step, on the caller's thread; terminal: the one it concerns.
typedef void (*isoshim_event_fn)(int32_t terminal, int32_t kind, int32_t a, int32_t b);
// A JSON object, UTF-8, "type" first: workingSets, screen (docs/API.md). Valid during the call.
typedef void (*isoshim_message_fn)(int32_t terminal, const char *json, int32_t len);
// A picture, once per terminal, before the first screen that refers to it: width x height RGBA,
// row by row. Ids are unique across the terminals.
typedef void (*isoshim_image_fn)(int32_t terminal, uint32_t id, int32_t width, int32_t height, const uint8_t *rgba);
// The stack's log lines. level: 0 debug .. 4 critical. Can come from the pool parsing thread.
typedef void (*isoshim_log_fn)(int32_t level, const char *text);

// Storage of pool versions, keyed by the implement's NAME and the version label (7 or 32 bytes;
// the two lengths are separate versions). One store for every terminal: an implement moved to
// another terminal finds its versions there. Any of them may be null: nothing is stored.
// save: append 0 starts the version afresh, 1 adds to it (a pool sent in several parts). 1: saved.
typedef int32_t (*isoshim_save_fn)(uint64_t name, const uint8_t *label, int32_t labelLen, const uint8_t *data, int32_t len, int32_t append);
// load: hand the pool back with isoshim_reply before returning; 0: no such version.
typedef int32_t (*isoshim_load_fn)(uint64_t name, const uint8_t *label, int32_t labelLen);
// list: each stored label of this length, one isoshim_reply each, before returning.
typedef void (*isoshim_list_fn)(uint64_t name, int32_t labelLen);
// remove: label null removes every version of labelLen. 1: removed.
typedef int32_t (*isoshim_remove_fn)(uint64_t name, const uint8_t *label, int32_t labelLen);

typedef struct isoshim_host {
	isoshim_send_fn send;       // required
	isoshim_event_fn event;     // required
	isoshim_message_fn message; // required
	isoshim_image_fn image;
	isoshim_save_fn save;
	isoshim_load_fn load;
	isoshim_list_fn list;
	isoshim_remove_fn remove;
} isoshim_host;

// One terminal. Zero for a field takes the default shown.
typedef struct isoshim_vt_config {
	uint32_t identity;        // NAME identity number (21 bits)
	uint16_t manufacturer;    // NAME manufacturer code (11 bits)
	uint16_t mask_width;      // data mask area in pixels: 480
	uint16_t mask_height;     // 480
	uint8_t function_instance; // as given (0 is instance 0); terminals in one start need different NAMEs
	uint8_t preferred_address; // 0x26 + the terminal's position
	uint8_t key_width;        // soft key descriptor in pixels: 80
	uint8_t key_height;       // 80
	uint8_t physical_keys;    // 6
	uint8_t virtual_keys;     // soft keys a soft key mask may hold: 64
	uint8_t navigation_keys;  // 0
	uint8_t version;          // VT version, 3 to 6: 5
	uint8_t small_fonts;      // supported font bitfields (ISO 11783-6 Get Text Font Data): 0xFF
	uint8_t large_fonts;      // 0xFF
	uint8_t packets_per_cts;  // frames per clear-to-send and per DPO, received and sent: 255 (the first terminal's, for all)
	uint8_t reserved[3];
} isoshim_vt_config;

#define ISOSHIM_MAX_TERMINALS 4

ISOSHIM_API int32_t isoshim_version(void); // 6
// count terminals, configs[0] .. configs[count - 1]. 0: started. -1: bad arguments (count outside 1
// to ISOSHIM_MAX_TERMINALS, two with the same NAME), -2: already running.
ISOSHIM_API int32_t isoshim_vt_start(const isoshim_vt_config *configs, int32_t count, const isoshim_host *host);
// Self-test only: upstream's VT client on channel 1 with this pool, connecting to this terminal (a
// partner filter on its function instance); after isoshim_vt_start. Returns which implement it is
// (0, 1, ... in the order added), or -1.
ISOSHIM_API int32_t isoshim_test_implement(int32_t terminal, const uint8_t *pool, int32_t poolLen);
// Self-test only: the data and alarm mask IDs in the pool of the implement a terminal shows (up to
// max of them written; returns how many there are), and a test implement asking for one of its
// own to be shown.
ISOSHIM_API int32_t isoshim_test_masks(int32_t terminal, uint16_t *ids, int32_t max);
ISOSHIM_API int32_t isoshim_test_show(int32_t implement, uint16_t mask);
// Self-test only: a test implement sends these 8 bytes to its terminal (ECU to VT); global: to
// everyone instead.
ISOSHIM_API int32_t isoshim_test_send(int32_t implement, const uint8_t *data, int32_t len, int32_t global);
ISOSHIM_API void isoshim_set_log(isoshim_log_fn fn, int32_t level);
// Any thread. Frames are handed to the stack at the next isoshim_step.
ISOSHIM_API void isoshim_receive(int32_t channel, uint32_t id, const uint8_t *data, int32_t len, int32_t extended);
// Runs the stack: the queued frames in, its timers, its frames out, then the screen if it changed.
// Call every few milliseconds; a multi-frame transfer moves one step per clear-to-send.
ISOSHIM_API void isoshim_step(void);
// Safe at any point, including while a pool is being parsed; isoshim_vt_start can follow.
ISOSHIM_API void isoshim_stop(void);
// From inside a load or list callback: the data it asked for (copied).
ISOSHIM_API void isoshim_reply(const uint8_t *data, int32_t len);

// The operator, on one terminal. Objects are in the pool of the implement that terminal shows.
enum isoshim_key_state {
	ISOSHIM_RELEASED = 0,
	ISOSHIM_PRESSED = 1,
	ISOSHIM_HELD = 2,    // still pressed: repeat about every 200 ms while held
	ISOSHIM_ABORTED = 3, // the press ended without a release (the finger slid off, the link went)
};
// 1: sent (or queued to send at the next step).
ISOSHIM_API int32_t isoshim_soft_key(int32_t terminal, uint16_t objectId, int32_t state);
ISOSHIM_API int32_t isoshim_button(int32_t terminal, uint16_t objectId, int32_t state);
// Which implement the terminal shows, by its NAME (one connected to that terminal). 1: it is now active.
ISOSHIM_API int32_t isoshim_activate(int32_t terminal, uint64_t name);

// Input fields, in the active implement's pool and on its screen, enabled (a hit with kind input).
// Select one, or a button, or nothing (0xFFFF). open: an editor is up for it; the host gets an
// edit message describing the field, and one with "o":null when it closes. 1: done.
ISOSHIM_API int32_t isoshim_select(int32_t terminal, uint16_t objectId, int32_t open);
// Enter a value; the field is selected first if it was not, and closes. Input number: the raw
// value (before offset and scale), within its minimum and maximum; input boolean: 0 or 1; input
// list: the item's index. 1: entered and sent to the implement; -1: not a valid value; 0: not an
// enabled input on the screen.
ISOSHIM_API int32_t isoshim_set_number(int32_t terminal, uint16_t objectId, uint32_t value);
// Enter text (UTF-8) in an input string: at most its length in characters, only characters its
// input attributes allow and its encoding holds. Results as isoshim_set_number.
ISOSHIM_API int32_t isoshim_set_string(int32_t terminal, uint16_t objectId, const char *utf8, int32_t len);
// Abandon the open field (VT ESC). 1: one was open.
ISOSHIM_API int32_t isoshim_escape(int32_t terminal);

// The link to the bus is up (again): each terminal sends its own address claim again (if it has
// an address), then every control function is asked for theirs. Both can go out at start before the
// link is up and be lost: implements then ignore a terminal they don't know, and the terminal
// ignores implements it never heard claim. 1: sent.
ISOSHIM_API int32_t isoshim_link_up(void);

#ifdef __cplusplus
}
#endif
