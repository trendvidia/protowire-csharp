#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 TrendVidia, LLC.
#
# Runs protowire-go over the corpora, then holds this port to its answers.
# Needs Go and the .NET SDK. CI does the same in go-conformance.yml.
set -euo pipefail
cd "$(dirname "$0")/.."

oracle="$(mktemp -t go-oracle.XXXXXX)"
trap 'rm -f "$oracle"' EXIT
(cd tool/go_oracle && go run . -root ../.. -out "$oracle")
GO_ORACLE="$oracle" REQUIRE_GO_ORACLE=1 dotnet test tests/Protowire.Conformance -c Release --nologo "$@"
