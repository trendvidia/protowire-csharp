<!--
For changes that touch wire-format behaviour: please open the upstream
PR in trendvidia/protowire FIRST. This port implements the spec; it
shouldn't lead spec changes. See CONTRIBUTING.md.
-->

## Summary

What this PR changes, in 1–3 sentences.

## Why

Link to the issue or upstream spec change that motivated this.

## Scope

- [ ] Wire-impacting source (`src/Protowire.{Pb,Pxf,Sbe,Envelope}/`)
- [ ] Vendored proto annotations (`proto/`)
- [ ] Tests / cross-port harnesses (`tests/`, `cmd/`, `testdata/`)
- [ ] Build / CI / repo plumbing (`Directory.Build.props`, `.github/`)
- [ ] Documentation only

## Test plan

- [ ] `dotnet build` clean (`-warningsaserrors` is set globally)
- [ ] `dotnet test` clean
- [ ] If parser/encoder change: `dotnet run --project cmd/Protowire.CheckDecode`
      clean against the upstream adversarial corpus
- [ ] If wire-impacting: matching upstream spec PR linked above
- [ ] If new public symbol: covered by a test under `tests/`
