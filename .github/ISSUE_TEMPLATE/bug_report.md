---
name: Bug report
about: Report a defect — wrong output, crash, parse error on valid input, etc.
title: "bug: "
labels: bug
---

<!--
Cross-port issues (the same input behaves differently on multiple ports)
belong upstream at trendvidia/protowire, not here. See CONTRIBUTING.md.

Security issues (decoder crash/hang/OOM on adversarial input) go to
security@trendvidia.com instead. See SECURITY.md.
-->

## What happened

A clear description of the bug.

## How to reproduce

Smallest possible PXF / PB / SBE / envelope input + C# snippet that
triggers it.

```csharp
using Protowire.Pxf;
// ...
```

## What you expected

What you thought should happen.

## Versions

- `Protowire.*` package version (from `*.csproj` or `dotnet list package`):
- `dotnet --version`:
- OS / arch:
