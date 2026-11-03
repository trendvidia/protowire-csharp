# Changelog

All notable changes to `protowire-csharp` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The version number is kept aligned with the rest of the `protowire-*`
stack — releases bump in lockstep across language ports when the wire
format changes.

## [Unreleased]

### Added

- **CI compares this port with protowire-go** (#66). `tool/go_oracle` records what the reference does with every document in the vendored spec corpora, and `tests/Protowire.Conformance` fails on a different verdict, PB bytes, written PXF or formatted PXF. `pxf.Secret` is vendored as a generated type, because the corpora use it.

### Changed

- **Generated messages now decode from their descriptors**, by the reference's rules, and several things that were accepted are now rejected. Check documents that relied on any of these:
  - An unknown field is an error. `Decoder.DiscardUnknown` skips it instead. A hand-written class still skips.
  - A singular field or a map key bound twice in one body (#46, #48), where the last value used to win. Keys compare as values, so `1:` and `"1":` are one key.
  - `null` on a plain scalar, a repeated field, a list element or a map value (#44). It remains legal on a field with presence.
  - A raw line feed in a simple string (#44), and `,` or `;` between the entries of a block (#49).
  - A value of the wrong type, such as a string in an `int32` field, which was either coerced or failed as a raw .NET exception. Every rejection is now a `PxfException`.
  - The `@type url` spelling inside an Any block. It is `@type = "url"` (#42), and the encoder writes that.
  - An identifier outside ASCII.
- **Generated messages are written from their descriptors**, in the reference's text. `tests/Protowire.Conformance` compares it byte for byte, and written PXF changes for existing callers in these ways: fields follow declaration order and generated plumbing such as `has_x` and `x_case` is gone, repeated and map fields of well-known types use their literals, a Timestamp has no trailing zeros, a Duration is spelled as Go's `time.Duration.String` spells it (`1.5s`, not `1s500ms`), floats use `1e+06`, and a map's keys sort by written form.
- The formatter quotes strings as `strconv.Quote` does, so an unusual character may be escaped differently.
- **More schemas are refused at bind time** (#52), as they are by the reference: a field, oneof or enum value named `null`, `true` or `false`; `(pxf.default)` on a repeated, map or group field, or on a message type no literal denotes; `(pxf.required)` on a oneof member; two defaults in one oneof; and a message that takes a well-known type's name without its fields.
- **A descriptor compiled before protowire v1.12.0 is refused at bind time** (#76), as the reference refuses it (STABILITY.md promise 3). In a file that imports protowire's annotations, an option at one of the retired numbers 50000–51002, on the Options kind that number was allocated on, is a violation naming the number, what it was, its registered number now, and that the descriptor must be recompiled. Such a schema used to bind with none of its annotations enforced.
- `(pxf.required)` and `(pxf.default)` now apply inside list elements and map values (#52).
- A nesting-depth error now reads `nesting depth exceeds MaxNestingDepth=100`.

### Added

- `Encoder.EmitDefaults`, `Encoder.NullFields` and `Encoder.MaxNestingDepth`. A message deeper than the limit is an error, because a reader at that limit would refuse it.
- **The draft's mandatory limits are enforced and configurable per call** (#32), on PXF, PB and SBE. `MaxMessageSize` is checked before any allocation proportional to the input, `MaxBytesLiteralLength` from a literal's length before it is decoded, and `MaxRepeatedCount` on an SBE group's `numInGroup` and as a counter on repeated and map elements. Use `PxfLimits`, `PbLimits` and `Codec`'s init properties. `check-decode` takes `--limit NAME=VALUE`, and runs `pb-render`, `pb-text` and `sbe`.
- `PxfLimits`, set through `Decoder.Limits` or `Parser.Parse(input, limits)`, makes the draft's limits per-call.
- The type entry of an Any block, in a singular field, a list element or a map value (#42).
- Wrapper-typed fields take a scalar or a block (#45).
- **`@required` and `@default(...)` are honoured** (#58), read from the 1327 carrier. A schema written that way used to get no required check, no default and no bind-time check, so it decoded differently here than in protowire-go. Two spellings of one default that disagree are refused.
- **`Decoder.BindRow` binds a `@dataset` row to a generated message** (#41, #43). This port parsed rows but had no binder. Cells may be lists or blocks, each meaning what `c = <value>` means in a body, and columns may be dotted paths into sub-messages. A column the row type cannot reach is refused, naming it. There is no streaming reader yet.
- Bool map keys, `+inf`, and the BigFloat precision suffix in the lexer.

### Fixed

- **`pxf.BigFloat` is read and written as a binary floating-point value** (#47). It was stored with a decimal scale as its exponent, so a value decoded from PXF was wrong on the wire, and the encoder wrote raw fields. It now follows the spec's corpus both ways: a suffixed literal reads at its precision, an unsuffixed one at 256 bits, and `MaxBigFloatExponent` bounds the value. **A BigFloat decoded from PXF by an earlier version holds the wrong value.**
- **A string map key is written bare exactly when it is identifier-safe** (#35, #36), dots included. `"true"`, `"null"`, `"123"` and `".e"` stay quoted, which keeps a formatted document binding to the same keys.
- **An Any is written as a type entry and a body only when it reads back unchanged**, and otherwise as `type_url` and `value`.
- **A Duration past the literal's 292-year range is written as a block**, and a `pxf.Secret` with metadata keeps its block form.
- **Fractional and `µs` duration literals** (#19), such as `1.5ms` and `2µs`. Other ports write them for any measured latency. A duration now keeps every nanosecond, and a malformed one such as `5min` is rejected by the lexer.
- **A Timestamp keeps nanoseconds**, and any RFC 3339 offset, rather than stopping at 100 ns.
- **A `(pxf.default)` on a oneof member no longer clears the sibling the document set** (#63).
- **A keyed entry body cannot undo its entry name** (#65): `null` on the key, or another member of the key's oneof, is a key conflict.
- **A block on an unkeyed repeated field is rejected** (#52), where `items { a { } }` used to decode to nothing.
- **PB reads a packed repeated field** (found by the limit corpus). It read the run's length as one element and the rest as tags, and packed is what proto3 writes by default.
- **PB rejects invalid UTF-8 in a string, a Decimal scale past `MaxNumericLiteralDigits` and a BigFloat past `MaxBigFloatExponent`**, and refuses to marshal a message past `MaxNestingDepth` or with such a scale.
- **A lone surrogate in a string passed to the decoder is a `PxfException`**, not a raw `EncoderFallbackException`.
- **An SBE root or group block shorter than its template is rejected** rather than crashing the decoder with a slice exception.
- **A PB map entry missing its key or value is no longer dropped** (#60). A missing field is now its type's default, as protobuf says, so such an entry stays in the map, and a `string` or message value is never null. `dump-envelope --vector zero-map-entry` prints the wire vector the spec checks (#31); the writer already produced it.
- **A keyed entry's name now counts as supplying the key field**
  ([trendvidia/protowire-go#214](https://github.com/trendvidia/protowire-go/issues/214)).
  After `kd { alpha {} }`, `Result` reports `kd[0].name` as set, where it
  used to report it absent and leave it out of `SetFields()`. The decoded
  value was already right: `(pxf.required)` and `(pxf.default)` are not
  applied inside list elements yet (#52), so the wrong values protowire-go
  saw cannot happen here. The mark is what keeps them from appearing once
  they are. This costs `UnmarshalFull` one presence record per entry;
  `Unmarshal` is unchanged.

## [1.1.0]

### Fixed

- **`pxf.Decimal.scale` and `pxf.BigFloat.exponent` are plain varints on the
  PB wire** (#55). `Protowire.Pb` read and wrote both with zigzag, but
  `bignum.proto` declares them `int32`, so it disagreed with every other
  port on every non-zero value: a wire scale of 2 read as 1, and 4096 as
  2048. **Bytes written by earlier versions read back wrong.** This is the
  bug protowire-go fixed in protowire-go#96 (STABILITY.md § v1.13).
- **A zero magnitude is empty on the PB wire.** `Protowire.Pb` wrote a zero
  `BigInt`, `Decimal.unscaled` or `BigFloat.mantissa` as one `0` byte, and
  kept a `BigFloat` zero's exponent. A zero now has the one encoding
  STABILITY.md § v1.13 describes. Earlier bytes still decode.

### Changed

- **The vendored shared schemas are re-synced with [trendvidia/protos](https://github.com/trendvidia/protos) v0.1.2** ([trendvidia/protowire#447](https://github.com/trendvidia/protowire/issues/447)). They gain protos' file options, and `envelope.proto`'s `go_package` now names a published package. Declarations and field numbers are unchanged, and so is this port's public API.

- **Proto bindings are regenerated by `tool/generate.sh`, not `buf generate`**
  (#24). `buf generate proto` wrote to `src/Pxf/`, `src/Envelopes/`,
  `src/Sbe/` — directories no project compiles — while the compiled
  copies under each project's `Generated/` folder were left as they were,
  and the SPDX header was lost on every run. `buf.gen.yaml` now targets a
  gitignored `.generated/` staging tree; the script moves each file to the
  project that compiles it, prepends the header, and refuses output it
  has no table row for. `tool/generate.sh --check` runs in CI and fails on
  drift, a missing header, or an orphaned binding. The four committed
  stray copies are removed.
- **A map's `{` and every PB map entry count toward the nesting limit**
  (protowire#449, HARDENING.md § Recursion). The PXF decoder did not count
  a map's own `{`, and PB counted neither a map entry nor anything about a
  scalar-valued one, so maps could nest past the limit in both. This
  narrows what decodes: a document nesting maps past 50 levels is now
  rejected, as it is by the text parser here and by protowire-go.
  `check-decode`'s `Tree` gains `branches` and `tags`. Without them the
  corpus rows for this were skipped as unknown fields and tested nothing.

### Added

- **Quoted entry names and keyed repeated fields** ([#15](https://github.com/trendvidia/protowire-csharp/issues/15), [trendvidia/protowire#116](https://github.com/trendvidia/protowire/issues/116)). The parser accepts a string as a field-entry name and the AST keeps the unquoted value plus `Assignment.KeyQuoted` / `Block.NameQuoted`, so `Format` reproduces the spelling. `(pxf.key)` = 1316 is back in `proto/pxf/annotations.proto`, and a repeated message field carrying it can be written as a block of named blocks: the entry name fills the key field and entry order is list order. The decoder rejects duplicate names (compared unquoted), an empty key in either surface form, an entry name that disagrees with an explicit key assignment, and a quoted name outside a keyed block, as `KeyedException` with a `KeyedErrorKind`. The encoder writes the keyed form whenever every key is present, non-empty and distinct, and the anonymous list form otherwise. `Keyed.Canonicalize` is the schema-aware half of fmt; run it before `Format.FormatDocument`. `Schema.ValidateDescriptor` and `Schema.ValidateFile` report misplaced `(pxf.key)` annotations across the import closure, and `Decoder` refuses such a schema. The pb and SBE wire outputs do not change. The cross-port fixtures from the spec repo's `testdata/keyed` are vendored under `tests/Protowire.Pxf.Tests/testdata/keyed` and all run.

- `dump-envelope --pb FDS MESSAGE DOC` and `--sbe FDS MESSAGE DOC` (#26),
  the two legs of protowire's `cross_envelope_check.sh` that prove a port
  reads `(pxf.required)` = 1314, `(pxf.default)` = 1315 and the SBE
  numbers 1319–1323 from a schema. `Decoder.UnmarshalFull` and `Sbe.Codec`
  already read them from the generated types' descriptors; the dumper maps
  `MESSAGE` to those types (`settings.v1.Settings` generated from a copy of
  protowire's `testdata/annotations/settings.proto`, `bench.v1.Order` from
  `sbe-bench.proto`) and reads FDS only to check it declares `MESSAGE`.

### Fixed

- **`MaxNumericLiteralDigits` is enforced** (#34, HARDENING.md § Mandatory
  limits). The port had no digit cap, so a big-number literal of any length
  reached `BigInteger.Parse`, on generated messages, POCOs and
  `(pxf.default)` strings alike. A `pxf.BigInt`, `pxf.Decimal` or
  `pxf.BigFloat` literal of exactly 4096 significant digits now decodes and
  one digit more is rejected naming the limit. Leading zeros, the sign, the
  point, an exponent and a precision suffix are not counted (protowire#410).
  A `pxf.Decimal` literal's scale is bounded by the same number.
  `check-decode` links `adversarial.v1.BigNumHolder`. It did not, so
  `pxf/long-numeric` was rejected as an unknown schema, never by a cap.
  
- **`uint32` / `uint64` fields decode** (#26). The reflection decode path
  had no branch for `uint` or `ulong`, so a generated message with either
  was rejected with `unsupported type System.UInt64`; protowire's shared
  `sbe-bench.pxf` tripped it at `order_id`.
  
- **`(pxf.required)` with `(pxf.default)` on one field is refused when the
  schema is bound** (#57, draft -01 "Required and Default"). `Decoder` used
  to accept such a schema, and which option won depended on whether the
  document omitted the field and on which engine read it. `Unmarshal` and
  `UnmarshalFull` now check the file declaring the bound message and its
  transitive imports before reading the document, and
  `Schema.ValidateDescriptor` / `Schema.ValidateFile` report the same
  violations. `(pxf.required) = false` beside a default stays accepted.
  `[PxfRequired]` with `[PxfDefault]` on one property of a hand-written type
  is refused the same way.
  
- **Nesting depth counts every descent, from a root at depth 0** (#33,
  HARDENING.md § Recursion). The PXF decoder did not count a `[`, so
  `children = [{ … }]` nested twice as deep as the limit said, and it did
  not bound a value skipped for an unknown field at all; both now count
  from the enclosing body. An `Any` body written as `details { @type … }`
  counted its one `{` twice, and so did the text parser for a directive
  body, which rejected a document one level short of the limit. PB now
  counts a map entry as a descent when its value is a message.
  `check-decode`'s `Tree` gains the `children` field the corpus rows
  `pxf/deep-nesting-lists-100` / `-101` use. Without it those rows were
  skipped as an unknown field and never reached the list decoder.

## [1.0.0]

Lockstep release with the rest of the `protowire-*` stack at the v1.0.0
spec freeze. Catches the C# port up from v0.70 to the full v0.72–v1.0
directive grammar (drafts §3.4.2–§3.4.6).

### Added

- **`@<name>` generic directives** (draft §3.4.2): top-of-document
  `@<name> *(<prefix-id>) [{ ... }]` blocks, e.g. chameleon's
  `@header chameleon.v1.LayerHeader { id = "x" }`. Captured on
  `Document.Directives` and on `Result.Directives` from `UnmarshalFull`.
- **`@entry` named directive** (draft §3.4.3): zero/one/two-prefix
  shape, handled by the same generic mechanism. The `entry` name itself
  is registered, not future-reserved.
- **`@dataset <type> ( col1, col2, ... ) row*`** (draft §3.4.4): the
  protowire-native CSV — many instances of one message type in a single
  document. Mutually exclusive with `@type` and top-level field entries.
  Exposed on `Document.Datasets` and `Result.Datasets`.
- **`@proto`** (draft §3.4.5): embedded protobuf schema with four
  lexically-distinguished body shapes — anonymous `{ ... }`, named
  `name { ... }`, source `""" ... """`, descriptor `b"..."`. Bodies are
  captured as raw bytes; protobuf decoding is downstream.
- **`Schema.IsFutureReservedDirective`** (draft §3.4.6): v1 decoders
  reject `@table`, `@datasource`, `@view`, `@procedure`, `@function`,
  `@permissions` so applications cannot squat the names before the spec
  allocates semantics.

### Changed (breaking)

- **`@table` removed**; use `@dataset` (no alias period — same change
  the rest of the stack made for v1.0).
- **`Document` shape extended** with `Directives`, `Datasets`, `Protos`,
  `BodyOffset` collections. Existing consumers of `Document.TypeURL` /
  `Document.Entries` are unaffected.

## [0.70.0]

Initial public release. The version number aligns this port with the rest
of the `protowire-*` stack, which targets the 0.70.x series for the first
coordinated public release.

### Added

- **NuGet distribution** for the four publishable packages: `Protowire.Pb`,
  `Protowire.Pxf`, `Protowire.Sbe`, `Protowire.Envelope`. The cross-port
  harnesses under `cmd/` (`Protowire.BenchPxf`, `Protowire.DumpEnvelope`,
  `Protowire.CheckDecode`) stay unpublished — they're consumed by the
  spec repo's `scripts/cross_*.sh` aggregators.
- **HARDENING.md decoder safety** (M8): bounded recursion depth and
  PB length-prefix overflow rejection in `Protowire.Pxf` and
  `Protowire.Pb`. Verified by the `cmd/Protowire.CheckDecode`
  adversarial corpus reference.
- **Comprehensive CI matrix**: `dotnet build` + `dotnet test` on the
  latest .NET SDK across Linux/macOS/Windows. Weekly CodeQL SAST.
- **Governance scaffolding**: `LICENSE` (MIT), `CONTRIBUTING.md`,
  `SECURITY.md` (security@trendvidia.com), `GOVERNANCE.md`,
  `CODE_OF_CONDUCT.md`, `.github/CODEOWNERS`, issue + PR templates,
  Dependabot for nuget + GitHub Actions.

### Changed (breaking)

- **PXF parser stricter on key forms**, mirroring the upstream grammar
  tightening in
  [`trendvidia/protowire@8262bbb`](https://github.com/trendvidia/protowire/commit/8262bbb)
  (`docs/grammar.ebnf`, `docs/draft-trendvidia-protowire-00.txt`):
  - `=` (field assignment) and `{ … }` (submessage) now require an
    identifier key. Inputs like `123 = 234` or `child { 123 = 123 }`
    now throw `PxfException` with
    `"field assignment with '=' requires an identifier key, got integer
    (\"123\"); use ':' for map entries"`.
  - `:` (map entry) is rejected at document top level — the document
    represents a proto message, never a `Dictionary<K, V>`. Use `=` for
    top-level field assignments. Map literals (`field = { 1: "x" }`)
    still work because `:` remains valid inside `{ … }` blocks.
