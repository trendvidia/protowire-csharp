# Protowire C#

**PXF** (Proto eXpressive Format) is a human-friendly text serialization format backed by protobuf schemas.

This is the C# (.NET 10.0+) port of the [original Go Protowire](https://github.com/trendvidia/protowire) library. It provides:
- **PXF**: A human-friendly text representation of Protobuf messages.
- **Protowire.Pb**: Reflection-based binary Protobuf marshaling for native C# classes.
- **Protowire.Sbe**: [FIX SBE](https://www.fixtrading.org/standards/sbe/) (Simple Binary Encoding) for ultra-low-latency use cases.

```
@type infra.v1.ServerConfig

hostname = "web-01.prod.example.com"
port     = 8443
enabled  = true
status   = STATUS_SERVING

# Well-known type literals
created_at = 2024-01-15T10:30:00Z
timeout    = 30s

# Nested messages use block syntax
tls {
  cert_file = "/etc/ssl/cert.pem"
  key_file  = "/etc/ssl/key.pem"
  verify    = true
}

# Repeated fields use list syntax
tags = ["production", "us-east", "frontend"]

# Maps use : for key-value pairs
labels = {
  env: "production"
  team: "platform"
  "hello world": "quoted keys supported"
}
```

## Why PXF?

| Format | Problem |
|--------|---------|
| JSON | Loosely typed, no comments, verbose, ambiguous without schema |
| YAML | Indentation-fragile, type coercion surprises (`no` -> `false`), complex spec |
| Protobuf textproto | No list/map literals, repeated fields are ugly, `:` separators feel archaic |

PXF uses your existing `.proto` files as the schema. No new schema language. No ambiguity — the parser always knows every field's type.

## Struct binary marshaling (`Protowire.Pb`)

Marshal any C# class to/from protobuf binary using `[Protowire(N)]` attributes — no `.proto` files or code generation required for simple use cases.

```csharp
using Protowire.Pb;

public class Endpoint {
    [Protowire(1)] public string Path { get; set; } = "";
    [Protowire(2)] public string Method { get; set; } = "";
    [Protowire(3)] public int Port { get; set; }
}

public class Config {
    [Protowire(1)] public string Hostname { get; set; } = "";
    [Protowire(2)] public bool Enabled { get; set; }
    [Protowire(3)] public List<Endpoint> Endpoints { get; set; } = new();
    [Protowire(4)] public byte[] Data { get; set; }
}

// Encode
var data = Pb.Marshal(new Config {
    Hostname = "web-01",
    Enabled = true,
    Endpoints = { new Endpoint { Path = "/api", Method = "GET", Port = 8080 } }
});

// Decode
var cfg = new Config();
Pb.Unmarshal(data, cfg);
```

The output is standard protobuf binary — wire-compatible with any `.proto` definition using the same field numbers.

### Supported types

- `bool`, `int`, `long`, `uint`, `ulong`, `float`, `double`
- `string`, `byte[]`
- `BigInteger`, `Decimal`, `BigFloat` (Protowire extensions)
- Enums
- Nested classes
- `List<T>`, `Arrays`, `Dictionary<K, V>`

## PXF C# API (`Protowire.Pxf`)

### Unmarshal

```csharp
var decoder = new Protowire.Pxf.Decoder();
var config = new ServerConfig();
decoder.Unmarshal(pxfText, config);
```

The decoder automatically maps PXF keys to C# property names. It handles both `ExactMatch` and `snake_case` to `PascalCase` mapping (e.g., `transport_error` -> `TransportError`).

### Marshal

```csharp
var encoder = new Protowire.Pxf.Encoder();
string pxfText = encoder.Marshal(config);
```

## Code Generation (`buf.build`)

This project uses [buf.build](https://buf.build) for managing `.proto` files and generating C# code.

To generate C# code from the schemas in the `proto/` directory:

```bash
buf generate proto
```

Generated files are placed in the `Generated/` folders of each project (e.g., `src/Protowire.Envelope/Generated/`).

## Project Structure

- `src/Protowire.Pb`: Core reflection-based Protobuf binary codec.
- `src/Protowire.Pxf`: PXF text format Lexer, Parser, and AST.
- `src/Protowire.Envelopes`: Standard API response envelope port.
- `src/Protowire.Sbe`: FIX SBE implementation.
- `proto/`: Shared Protobuf schemas.
- `tests/`: XUnit test suites for each component.
- `cmd/{Protowire.BenchPxf, Protowire.BenchSbe, Protowire.DumpEnvelope}`: Cross-port test harnesses.

## Limitations & open gaps

Built on `Google.Protobuf` reflection (`MessageDescriptor`, `IFieldAccessor`) — the descriptor-driven design means the same codec works for any compiled-in message type. A few items fall out of that or are deferred:

- **`net10.0` minimum.** The build leans on C# 13 features (collection expressions, file-scoped types). Targeting `net8.0` or earlier would require backporting and is not currently planned.
- **`Google.Protobuf` doesn't expose extension fields as readable named fields on `FieldOptions` for arbitrary `.proto` files.** The `(pxf.required)` / `(pxf.default)` reader works because the `AnnotationsExtensions` class is shipped in this repo; downstream `.proto` files that bring their own extensions need their generated `*Extensions` class on the runtime classpath.
- **No async API on the codec surface.** All decode / encode is synchronous in-memory. For very large PXF documents an async streaming surface would be welcome.
- **The CLI lives in [trendvidia/protowire/cmd/protowire](https://github.com/trendvidia/protowire/tree/main/cmd/protowire), not here.** This repo ships only the library + cross-port harnesses.

## Contributing & governance

This repository is part of the `protowire-*` family and is governed by [**Steward**](https://github.com/trendvidia/steward) — the meritocratic, AI-driven governance engine that runs all of the ports. Voting weight is per-directory expertise, the constitution is public in [`governance.pxf`](https://github.com/trendvidia/steward/blob/main/governance.pxf), and Steward routes draft / first-time PRs through a [private mentorship pipeline](https://github.com/trendvidia/steward#-private-mentorship-mode) so initial contributions get private feedback rather than public-review friction.

If any of the items above sound interesting, pull requests are welcome. New contributors start at zero trust and accumulate influence by shipping merged PRs in the directories they actually work on — the [escrow pipeline](https://github.com/trendvidia/steward#%EF%B8%8F-the-escrow-pipeline-zero-trust-onboarding) auto-routes large first-time PRs through 2–3 sandbox issues before unlocking them for community review.

See the [Steward README](https://github.com/trendvidia/steward) for a longer walkthrough of vector reputation, escrow, and the immune system.
