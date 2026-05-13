# Changelog

All notable changes to `protowire-csharp` are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The version number is kept aligned with the rest of the `protowire-*`
stack — releases bump in lockstep across language ports when the wire
format changes.

## [Unreleased]

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
