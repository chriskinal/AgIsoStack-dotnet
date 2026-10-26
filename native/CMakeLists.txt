# SPDX-License-Identifier: MIT
# Copyright (c) 2026 The SharpIsoTerm contributors
#
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
# against a local checkout instead. Not a cache variable, so a build directory follows the pin.
set(AGISOSTACK_GIT_TAG "7b771274422ea0f62d4eac042fa1684ee373d463")  # upstream HEAD of 2026-10-26, with #801 and #809
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
  add_library(isoshim STATIC isoshim.cpp render.cpp)  # iOS loads no libraries at run time: linked into the app
else()
  add_library(isoshim SHARED isoshim.cpp render.cpp)
endif()
target_link_libraries(isoshim PRIVATE isobus::Isobus isobus::Utility)
if(WIN32)
  set_target_properties(isoshim PROPERTIES PREFIX "")  # isoshim.dll, not libisoshim.dll (MinGW)
  if(MINGW)
    target_link_options(isoshim PRIVATE -static)  # the C++ runtime inside: no DLLs to ship beside it
  endif()
endif()
