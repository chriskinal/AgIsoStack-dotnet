// Minimal test harness: CHECK records a failure and carries on; finish() reports and
// gives the exit code.
#pragma once

#include <cstdio>

static int failures = 0;
#define CHECK(c) do { if (!(c)) { std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #c); ++failures; } } while (0)

static inline int finish(const char* name) {
    if (failures) std::printf("%s: %d failure(s)\n", name, failures);
    else std::printf("all %s tests passed\n", name);
    return failures ? 1 : 0;
}
