# SPDX-License-Identifier: MIT
# Copyright (c) 2026 The SharpIsoTerm contributors
#
# The Linux build: Debian 12 (glibc 2.36, as Raspberry Pi OS bookworm), so the library loads on
# that and anything newer. CMake 3.28+ from bookworm-backports.
FROM debian:bookworm
RUN echo "deb http://deb.debian.org/debian bookworm-backports main" > /etc/apt/sources.list.d/backports.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends g++ ninja-build git ca-certificates \
 && apt-get install -y --no-install-recommends -t bookworm-backports cmake \
 && rm -rf /var/lib/apt/lists/*
