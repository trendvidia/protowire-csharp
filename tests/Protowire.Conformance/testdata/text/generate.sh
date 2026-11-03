#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 TrendVidia, LLC.
#
# Regenerates testdata/text from its sources: text.binpb and each case's
# <name>.binpb with protoc, and each <name>.expected.pxf with protowire-go,
# the reference. GO_DIR points at a protowire-go checkout. A changed golden is
# a text change for every port, so read the diff before committing it.

set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
GO_DIR="${GO_DIR:-$HERE/../../../protowire-go}"
bin="$(mktemp -t check-decode-go.XXXXXX)"
trap 'rm -f "$bin"' EXIT
(cd "$GO_DIR" && go build -o "$bin" ./scripts/check_decode)

cd "$HERE"
include=(-I . -I ../../proto)
protoc "${include[@]}" --include_imports -o text.binpb text.proto
for source in cases/*.textpb; do
  name="$(basename "$source" .textpb)"
  schema="text.v1.${name%%.*}"
  protoc "${include[@]}" --encode="$schema" text.proto < "$source" > "cases/$name.binpb"
  "$bin" --format pb-text --schema "$schema" --proto text.proto \
    --input "cases/$name.binpb" > "cases/$name.expected.pxf"
done
