// A C interface over AgIsoStack++ for .NET, with CAN frames carried by the caller.
//
// Threading: the stack runs on the caller's thread and nowhere else. isoshim_receive may be called
// from any thread (it only queues the frame); every other function, and every callback except the
// log, runs on the one thread that calls isoshim_step. The stack's hardware layer and its threads
// are not used: the caller is the bus.
//
// isoshim_start: channel 0 holds a virtual terminal server, channel 1 an implement that uploads a
// pool to it; the caller hands each frame one channel sends to the other (the spike's self-test).
// isoshim_start_terminal: the terminal alone on channel 0, its frames carried to a real bus (a
// gateway).
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

// Every frame a channel transmits, from isoshim_step (or a command such as isoshim_press_soft_key)
// on the caller's thread. It may call isoshim_receive.
typedef void (*isoshim_send_fn)(int32_t channel, uint32_t id, const uint8_t *data, int32_t len, int32_t extended);

enum isoshim_event {
	ISOSHIM_SERVER_ADDRESS = 1, // a: address the terminal claimed
	ISOSHIM_CLIENT_ADDRESS = 2, // a: address the implement claimed
	ISOSHIM_POOL_OK = 3,        // a: objects in the parsed pool, b: the implement's address
	ISOSHIM_POOL_BAD = 4,       // a: faulty object id, b: the implement's address
	ISOSHIM_MASK = 5,           // a: active data mask now on the terminal
	ISOSHIM_OTHER_POOL = 6,     // another implement's pool, a: objects, b: its address
	ISOSHIM_PARSE_START = 7,    // a pool has arrived and is being parsed, b: the implement's address
	ISOSHIM_WS_SHUTDOWN = 8,    // an implement went away, a: reason (0 maintenance timeout, 1 re-initiated), b: its address
};
// The stack's log lines. level: 0 debug .. 4 critical. Can come from the pool parsing thread.
typedef void (*isoshim_log_fn)(int32_t level, const char *text);
// From isoshim_step, on the caller's thread.
typedef void (*isoshim_event_fn)(int32_t kind, int32_t a, int32_t b);

ISOSHIM_API int32_t isoshim_version(void);
ISOSHIM_API int32_t isoshim_start(isoshim_send_fn send, isoshim_event_fn event, const uint8_t *pool, int32_t poolLen);
// The implement under test is the one whose NAME has function code wantedFunction (0xFFFF: the
// first pool to arrive); its pool becomes the active one.
ISOSHIM_API int32_t isoshim_start_terminal(isoshim_send_fn send, isoshim_event_fn event, uint16_t wantedFunction);
ISOSHIM_API void isoshim_set_log(isoshim_log_fn fn, int32_t level);
// Any thread. Frames are handed to the stack at the next isoshim_step.
ISOSHIM_API void isoshim_receive(int32_t channel, uint32_t id, const uint8_t *data, int32_t len, int32_t extended);
// Runs the stack: the queued frames in, its timers, its frames out. Call every few milliseconds;
// a multi-frame transfer moves one step per clear-to-send.
ISOSHIM_API void isoshim_step(void);
ISOSHIM_API int32_t isoshim_press_soft_key(uint16_t objectId);
// Safe at any point, including while a pool is being parsed; isoshim_start can follow.
ISOSHIM_API void isoshim_stop(void);
// Frames the receiver of a multi-frame transfer allows per clear-to-send (stack default 16), and
// the sender's frames per data packet offset; set before isoshim_start.
ISOSHIM_API void isoshim_set_packets_per_cts(uint8_t packets);

#ifdef __cplusplus
}
#endif
