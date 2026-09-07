#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 TrendVidia, LLC.
#
# Regenerates the C# proto bindings into the projects that compile them.
#
#   tool/generate.sh          regenerate in place
#   tool/generate.sh --check  regenerate to a scratch tree and fail if any
#                             compiled binding differs, lacks the SPDX
#                             header, or is not in the table below
#
# `buf generate proto` alone writes to .generated/ (see buf.gen.yaml), a
# staging tree nothing compiles: SDK-style projects glob their own
# directory, and the bindings live in each project's Generated/ folder.
# This script is the documented way to regenerate (#24). It needs `buf`;
# the generator is the pinned remote plugin in buf.gen.yaml, so output
# does not depend on a local protoc.
set -euo pipefail
cd "$(dirname "$0")/.."

# staging path (under .generated/, laid out by base_namespace) → compiled file
TABLE=(
  "Pxf/Annotations.cs                    src/Protowire.Pxf/Generated/Annotations.cs"
  "Pxf/Bignum.cs                         src/Protowire.Pxf/Generated/Bignum.cs"
  "Sbe/Annotations.cs                    src/Protowire.Sbe/Generated/Annotations.cs"
  "Envelopes/V1/Envelope.cs              src/Protowire.Envelope/Generated/Envelope.cs"
  "Pxf/Tests/Bench/BenchTest.cs          tests/Protowire.Pxf.Tests/Generated/BenchTest.cs"
  "Pxf/Tests/Presence/PxfPresenceTest.cs tests/Protowire.Pxf.Tests/Generated/PxfPresenceTest.cs"
  "Sbe/Tests/Bench/SbeBench.cs           tests/Protowire.Sbe.Tests/Generated/SbeBench.cs"
  "Sbe/Tests/Bench/SbeExtra.cs           tests/Protowire.Sbe.Tests/Generated/SbeExtra.cs"
  "Sbe/Tests/Bench/SbeOneof.cs           tests/Protowire.Sbe.Tests/Generated/SbeOneof.cs"
  "DumpEnvelope/Fixtures/Settings.cs     cmd/Protowire.DumpEnvelope/Generated/Settings.cs"
)
HEADER=$'// SPDX-License-Identifier: MIT\n// Copyright (c) 2026 TrendVidia, LLC.\n'

check=0
[[ "${1:-}" == "--check" ]] && check=1

command -v buf >/dev/null || { echo "generate: buf is not installed (https://buf.build/docs/installation)" >&2; exit 2; }

rm -rf .generated
buf generate proto
[[ -d .generated ]] || { echo "generate: buf produced nothing" >&2; exit 2; }

status=0
staged_seen=()
for row in "${TABLE[@]}"; do
  read -r staged dest <<<"$row"
  staged_seen+=("$staged")
  if [[ ! -f ".generated/$staged" ]]; then
    echo "generate: table expects .generated/$staged, buf did not produce it" >&2
    status=1; continue
  fi
  tmp="$(mktemp)"
  { printf '%s' "$HEADER"; cat ".generated/$staged"; } > "$tmp"
  if [[ $check -eq 1 ]]; then
    if [[ ! -f "$dest" ]]; then
      echo "DRIFT  $dest is missing (would be created from $staged)"; status=1
    elif ! cmp -s "$tmp" "$dest"; then
      echo "DRIFT  $dest differs from a fresh generation of $staged"; status=1
      diff -u "$dest" "$tmp" | head -20 || true
    fi
  else
    mkdir -p "$(dirname "$dest")"
    if [[ -f "$dest" ]] && cmp -s "$tmp" "$dest"; then
      echo "same   $dest"
    else
      cp "$tmp" "$dest"; echo "wrote  $dest"
    fi
  fi
  rm -f "$tmp"
done

# Every file buf produced must have a home, so a new .proto cannot land
# in .generated/ and be forgotten.
while IFS= read -r f; do
  rel="${f#.generated/}"
  found=0
  for s in "${staged_seen[@]}"; do [[ "$s" == "$rel" ]] && found=1; done
  if [[ $found -eq 0 ]]; then
    echo "generate: buf produced .generated/$rel, which has no row in the table" >&2; status=1
  fi
done < <(find .generated -name '*.cs' | sort)

# Every compiled binding must be a table destination and carry the header.
while IFS= read -r f; do
  in_table=0
  for row in "${TABLE[@]}"; do read -r _ dest <<<"$row"; [[ "$dest" == "$f" ]] && in_table=1; done
  [[ $in_table -eq 1 ]] || { echo "ORPHAN $f is not produced by any table row" >&2; status=1; }
  if [[ "$(head -n 2 "$f")" != "${HEADER%$'\n'}" ]]; then
    echo "HEADER $f lacks the SPDX header" >&2; status=1
  fi
done < <(find src tests cmd -path '*/Generated/*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | sort)

rm -rf .generated
if [[ $status -ne 0 ]]; then
  [[ $check -eq 1 ]] && echo "generate --check: bindings are out of date; run tool/generate.sh" >&2
  exit 1
fi
[[ $check -eq 1 ]] && echo "generate --check: every compiled binding is current and headed"
exit 0
