# isoshim: a C interface over AgIsoStack++ for .NET (P/Invoke), on macOS, iOS, Android, Linux and
# Windows. The shim supplies the stack's hardware hooks itself, so only the stack's isobus and
# utility libraries are linked: no hardware layer, no stack threads.
cmake_minimum_required(VERSION 3.28)
project(isoshim VERSION 0.2.0 LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)

# AgIsoStack++ at a pinned upstream commit, fetched at configure time. The pin follows upstream
# HEAD: move it, then run the self-test on every platform (README.md). ISOSHIM_STACK_DIR builds
# against a local checkout instead.
set(AGISOSTACK_GIT_TAG "3e37ba629b53b565fa80c63feaed027a9156d3b9"
    CACHE STRING "AgIsoStack++ commit to build against (upstream HEAD of 2026-10-24)")
set(ISOSHIM_STACK_DIR "" CACHE PATH "Local AgIsoStack++ source tree to use instead of the pin")

set(BUILD_EXAMPLES OFF)
set(BUILD_TESTING OFF)
set(CAN_DRIVER "VirtualCAN" CACHE STRING "")  # the stack's hardware layer is configured but not linked
if(ISOSHIM_STACK_DIR)
  add_subdirectory(${ISOSHIM_STACK_DIR} stack EXCLUDE_FROM_ALL)
else()
  include(FetchContent)
  FetchContent_Declare(agisostack
    GIT_REPOSITORY https://github.com/Open-Agriculture/AgIsoStack-plus-plus.git
    GIT_TAG ${AGISOSTACK_GIT_TAG}
    EXCLUDE_FROM_ALL)
  FetchContent_MakeAvailable(agisostack)
endif()

if(CMAKE_SYSTEM_NAME STREQUAL "iOS")
  add_library(isoshim STATIC isoshim.cpp)  # iOS loads no libraries at run time: linked into the app
else()
  add_library(isoshim SHARED isoshim.cpp)
endif()
target_link_libraries(isoshim PRIVATE isobus::Isobus isobus::Utility)
