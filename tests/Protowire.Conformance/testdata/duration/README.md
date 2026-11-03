# Duration literal fixtures

Cross-port conformance fixtures for **signed duration literals**
([issue #234](https://github.com/trendvidia/protowire/issues/234);
IETF draft `-01` §3.3 `duration = ["-"] 1*duration-segment` and
§"Timestamps and Durations").

`google.protobuf.Duration` is signed, and every port in the family already
emitted a negative Duration with a leading `-` (mirroring
`time.Duration.String()`) and parsed it back — while draft `-01`'s grammar
admitted no sign, and its disambiguation prose said `5seconds` was an
identifier, which no port did. These fixtures pin what the ports do and
the draft now says. As with [`testdata/keyed/`](../keyed/), the
conformance harness wiring is per port; each file's leading comment states
the expected behavior, and `cmd/pxf/duration_test.go` runs them through
the reference implementation.

All fixtures bind against [`duration.proto`](duration.proto) (package
`duration.v1`, one field `Holder.d`); each file's `@type` directive names
the message.

## Accept fixtures

| File | Asserts |
|---|---|
| `negative-seconds.pxf` | `-30s` binds to seconds = -30. |
| `negative-fractional.pxf` | `-1.5s` binds to seconds = -1, nanos = -500000000 — same sign, per `google.protobuf.Duration`. |
| `negative-multi-segment.pxf` | `-1h30m` binds to seconds = -5400: one sign for the whole literal. |
| `negative-zero.pxf` | `-0s` is zero. |

## fmt canonicalization pair

| Pair | Asserts |
|---|---|
| `roundtrip-negative` | A negative Duration re-encodes as one leading `-` before the first segment; the expected file is a fmt fixed point. Comment-free apart from `@type`, as in `keyed/`, so the byte-level expectation pins the literal, not comment placement. |

## Reject fixtures

| File | Expected error |
|---|---|
| `err-sign-per-segment.pxf` | `1h-30m` — a sign inside the literal; the duration token ends at `1h`. |
| `err-digit-led-identifier.pxf` | `5seconds` — DIGIT-led token past a unit into letters is an invalid duration, not an identifier. |
| `err-unit-then-alpha.pxf` | `5sx` — the same rule at the shortest distance. |

## Not pinned here

A leading `+` (`+30s`) is deliberately absent. It is not in the grammar
and no encoder writes it, but ports differ on lenience (the reference
port rejects it at the lexer; several others skip a `+` inside their
duration validators), and issue #234 leaves whether to admit it as an
open decision. A fixture either way would force that decision by
breaking half the family; add one when it is taken.

## Adding fixtures

Extend `duration.proto` additively only (new fields / messages); renaming
or renumbering `Holder.d` invalidates every existing fixture at once.
