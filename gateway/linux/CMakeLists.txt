# SPDX-License-Identifier: MIT
# Copyright (c) 2026 The SharpIsoTerm contributors
#
# aow-linkd: the AOW link gateway for Linux (SocketCAN <-> TCP), and the protocol's gateway end
# as a library (aow_link: no Linux in it) with its unit tests. MIT.
#   cmake -S gateway/linux -B build && cmake --build build && ctest --test-dir build
cmake_minimum_required(VERSION 3.18)
project(aow-linkd VERSION 0.1.0 LANGUAGES CXX)
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(AOW_LINKD_VERSION "${PROJECT_VERSION}" CACHE STRING "Version aow-linkd reports")

add_library(aow_link STATIC aow_link.cpp)
target_include_directories(aow_link PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_compile_options(aow_link PRIVATE -Wall -Wextra -Wpedantic)

if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  add_executable(aow-linkd aow_linkd.cpp)
  target_compile_definitions(aow-linkd PRIVATE AOW_LINKD_VERSION="${AOW_LINKD_VERSION}")
  target_compile_options(aow-linkd PRIVATE -Wall -Wextra -Wpedantic)
  target_link_libraries(aow-linkd PRIVATE aow_link)
  install(TARGETS aow-linkd RUNTIME DESTINATION bin)
endif()

if(PROJECT_IS_TOP_LEVEL)
  enable_testing()
  add_executable(test_aow_link tests/test_aow_link.cpp)
  target_link_libraries(test_aow_link PRIVATE aow_link)
  add_test(NAME aow_link COMMAND test_aow_link)
endif()
