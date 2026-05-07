# Contributing to protowire-csharp

Welcome — this is the C# port of [protowire](https://protowire.org), a
language-neutral wire-format toolkit. It tracks the canonical specification
in [`trendvidia/protowire`](https://github.com/trendvidia/protowire) and is
one of nine sibling ports (Go, C++, Rust, Java, TypeScript, Python, C#,
Swift, Dart). The port is pure .NET (SDK-style csproj) and uses
[`Google.Protobuf`](https://www.nuget.org/packages/Google.Protobuf) as
its only runtime dependency.

> **Steward integration is rolling out.** The governance described in
> [GOVERNANCE.md](GOVERNANCE.md) is the steady-state model. While Steward
> is being finalised, pull requests are reviewed by human maintainers in
> the conventional way — open a PR, expect review, iterate.

## Where bugs go

| Symptom | File against |
|---|---|
| C# port-only crash, wrong API ergonomics, performance regression in this port only | `trendvidia/protowire-csharp` |
| The same input produces different output here vs another port | upstream [`trendvidia/protowire`](https://github.com/trendvidia/protowire) (cross-port wire-equivalence regression) |
| Spec / grammar / proto annotation question | upstream [`trendvidia/protowire`](https://github.com/trendvidia/protowire) |
| Decoder crash / hang / OOM on adversarial input | **email security@trendvidia.com**, do not file public issue (see [SECURITY.md](SECURITY.md)) |

## Toolchain

.NET 10.0 SDK (the `<TargetFramework>` set in `Directory.Build.props`).
Tested in CI on:

- Latest .NET SDK × {Linux, macOS, Windows}

Plus `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and
`<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>` are enabled
globally, so a build is the lint gate.

## Local development

```sh
# Restore + build all 4 publishable libraries + tests + cmd harnesses
dotnet build

# Tests
dotnet test

# Cross-port harnesses
dotnet run --project cmd/Protowire.DumpEnvelope
dotnet run --project cmd/Protowire.BenchPxf
dotnet run --project cmd/Protowire.CheckDecode -- \
  --format pxf --schema adversarial.v1.Tree \
  --proto ../protowire/testdata/adversarial/adversarial.proto \
  --input ../protowire/testdata/adversarial/pxf/deep-nesting-100.pxf

# Pack (produces .nupkg + .snupkg under bin/Release/)
dotnet pack -c Release
```

### Regenerating proto bindings

The `proto/` tree mirrors the upstream wire contract. Bindings are
generated through `buf` (see `buf.gen.yaml`).

## Sending changes

1. Open a draft PR early.
2. **For changes that touch parser/encoder behaviour**: comment with
   which fixtures from `tests/` you exercised. Cross-port
   wire-equivalence means a wrong move here can break six other ports'
   contracts.
3. **For changes that touch the wire format itself** — annotation field
   numbers in `proto/`, the PXF grammar, the SBE schema-id semantics —
   open the upstream PR in
   [`trendvidia/protowire`](https://github.com/trendvidia/protowire)
   first. This port shouldn't lead spec changes; it implements them.
4. **Anything that adds a new public symbol** must be in the right
   `Protowire.*` namespace and covered by a test under `tests/`.

## Code style

- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` is set
  workspace-wide. Suppress with `#pragma warning disable <ID>` and a
  one-line comment, restored with `#pragma warning restore <ID>` at
  the smallest viable scope.
- `<Nullable>enable</Nullable>` is on. Don't suppress with `!` unless
  you can prove non-null at the call site.
- `<ImplicitUsings>enable</ImplicitUsings>` is on; don't add `using`
  for namespaces already pulled in implicitly.
- Match the existing zero-allocation patterns in `src/Protowire.Sbe/View.cs` —
  the `View` API is the "zero allocation" reference point.

## What we don't accept

- Changes that break wire-equivalence with another sibling port.
- New top-level dependencies without a one-line justification in the
  PR description. We currently depend only on `Google.Protobuf`.
- `#pragma warning disable` on a whole file or whole namespace. Keep
  them line-scoped.

## Releases

This port releases in lockstep with the rest of the `protowire-*` stack.
The version line is `0.70.x` for the first coordinated public release;
ports that share a `0.70.x` minor implement the same wire contract.

Cutting a release:

1. Bump `<Version>` in `Directory.Build.props`.
2. Add a `## [X.Y.Z]` section to `CHANGELOG.md`.
3. Tag `vX.Y.Z` on `main`.
4. The `.github/workflows/publish.yml` workflow runs `dotnet pack` +
   `dotnet nuget push` for the 4 publishable packages.
