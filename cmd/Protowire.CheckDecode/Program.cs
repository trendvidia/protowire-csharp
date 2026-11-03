// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
// Per-port reference for the protowire HARDENING.md conformance corpus.
//
// Driven by `protowire/scripts/cross_security_check.sh`. See:
//   protowire/docs/HARDENING.md
//   protowire/testdata/adversarial/README.md
//
// Contract:
//
//     check-decode --format <pxf|pb|pb-render|pb-text|sbe|envelope> \
//                  --schema <fully.qualified.MessageType> \
//                  --proto  <path-to-adversarial.proto> \
//                  --input  <path> \
//                  [--limit NAME=VALUE ...]
//
//     Exit 0 -> input was accepted (decode succeeded)
//     Exit 1 -> input was rejected (clean error, "reject: <msg>" to stderr)
//     Other  -> bug in the decoder (uncaught exception, OOM, ...)
//
// Mirrors `protowire-go/scripts/check_decode/main.go`.
//
// This is a codegen port, so --proto is not read: --schema names a message of
// proto/conformance/adversarial.proto, a byte copy of the corpus's schema,
// whose generated class the PXF, pb-render and SBE paths use. pb-text does the
// same with text.proto. The PB codec consumes hand-written classes annotated
// with `[Protowire(N)]`, so the PB path has the mirrors below. Drift between
// them and the schema is caught by the corpus: a wrong field number flips an
// accept or reject expectation.
//
// A rejection that is not one of this port's exceptions is exit 2. That is the
// contract's "bug in the decoder", and it is why a format this harness does not
// run cannot pass as a clean rejection.

using Google.Protobuf;
using Google.Protobuf.Reflection;
using Protowire.CheckDecode.Schemas;
using Protowire.Conformance.Schemas.Text;
using Protowire.Pb;
using Protowire.Pxf;
using Protowire.Sbe;

return CheckDecode.Run(args);

internal static class CheckDecode
{
    private static readonly MessageDescriptor[] Adversarial =
        AdversarialReflection.Descriptor.MessageTypes.ToArray();

    public static int Run(string[] args)
    {
        string? format = null, schema = null, input = null;
        var limits = new Dictionary<string, int>();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string? val = i + 1 < args.Length ? args[i + 1] : null;
            switch (key)
            {
                case "--format": format = val; i++; break;
                case "--schema": schema = val; i++; break;
                case "--proto": i++; break;
                case "--input": input = val; i++; break;
                case "--limit":
                    i++;
                    if (!TryLimit(val, limits))
                    {
                        Console.Error.WriteLine($"check-decode: bad --limit {val}");
                        return 2;
                    }
                    break;
                default:
                    Console.Error.WriteLine($"check-decode: unknown arg {key}");
                    return 2;
            }
        }

        if (string.IsNullOrEmpty(format) || string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(input))
        {
            Console.Error.WriteLine("usage: check-decode --format ... --schema ... --input ... [--proto ...] [--limit NAME=VALUE]");
            return 2;
        }

        try
        {
            DoRun(format!, schema!, input!, limits);
            return 0;
        }
        catch (Exception e) when (IsRejection(e))
        {
            Console.Error.WriteLine($"reject: {e.Message}");
            return 1;
        }
    }

    // The exception types a decode rejects with. SBE's are plain Exception.
    private static bool IsRejection(Exception e) =>
        e is PxfException or InvalidProtocolBufferException or InvalidOperationException
            or FormatException or System.Text.DecoderFallbackException
        || e.GetType() == typeof(Exception);

    private static bool TryLimit(string? spec, Dictionary<string, int> limits)
    {
        var parts = spec?.Split('=', 2);
        if (parts is not { Length: 2 } || !int.TryParse(parts[1], out int n) || n <= 0) return false;
        if (parts[0] is not ("MaxMessageSize" or "MaxNestingDepth" or "MaxNumericLiteralDigits"
            or "MaxBytesLiteralLength" or "MaxRepeatedCount" or "MaxBigFloatExponent"))
        {
            return false;
        }
        limits[parts[0]] = n;
        return true;
    }

    private static void DoRun(string format, string schema, string input, Dictionary<string, int> limits)
    {
        switch (format)
        {
            case "pxf":
                PxfDecode(input, schema, limits);
                return;
            case "pb":
                PbDecode(input, schema, limits);
                return;
            case "pb-render":
                PbRender(input, schema);
                return;
            case "pb-text":
                PbText(input, schema);
                return;
            case "sbe":
                SbeDecode(input, schema, limits);
                return;
            case "envelope":
                throw new NotSupportedException("envelope decode not yet implemented in this reference");
            default:
                throw new ArgumentException($"unsupported format: {format}");
        }
    }

    private static MessageDescriptor Generated(string schema) =>
        Adversarial.FirstOrDefault(m => m.FullName == schema)
        ?? throw new ArgumentException($"unknown schema: {schema}");

    private static void PxfDecode(string input, string schema, Dictionary<string, int> limits)
    {
        var defaults = PxfLimits.Default;
        var decoder = new Decoder
        {
            Limits = defaults with
            {
                MaxMessageSize = limits.GetValueOrDefault("MaxMessageSize", defaults.MaxMessageSize),
                MaxNestingDepth = limits.GetValueOrDefault("MaxNestingDepth", defaults.MaxNestingDepth),
                MaxNumericLiteralDigits = limits.GetValueOrDefault("MaxNumericLiteralDigits", defaults.MaxNumericLiteralDigits),
                MaxBytesLiteralLength = limits.GetValueOrDefault("MaxBytesLiteralLength", defaults.MaxBytesLiteralLength),
                MaxRepeatedCount = limits.GetValueOrDefault("MaxRepeatedCount", defaults.MaxRepeatedCount),
                MaxBigFloatExponent = limits.GetValueOrDefault("MaxBigFloatExponent", defaults.MaxBigFloatExponent),
            },
        };
        decoder.Unmarshal(File.ReadAllText(input), Generated(schema).Parser.ParseFrom(ByteString.Empty));
    }

    // pb-render tests the PXF producer, so the stock reader in front of it must
    // not be the one that stops a deep message: Google.Protobuf's default
    // recursion limit is 100, which would refuse the 101-deep rows before the
    // encoder ran, and the reject would pass without testing it (#80). The
    // limit here is protobuf-go's default, as protowire-go's harness reads.
    private const int StockRecursionLimit = 10000;

    private static void PbRender(string input, string schema)
    {
        var bytes = File.ReadAllBytes(input);
        var stream = CodedInputStream.CreateWithLimits(new MemoryStream(bytes), bytes.Length, StockRecursionLimit);
        var msg = Generated(schema).Parser.ParseFrom(stream);
        new Encoder().Marshal(msg);
    }

    private static void PbDecode(string input, string schema, Dictionary<string, int> limits)
    {
        var defaults = PbLimits.Default;
        Protowire.Pb.Pb.Unmarshal(File.ReadAllBytes(input), NewMessage(schema), defaults with
        {
            MaxMessageSize = limits.GetValueOrDefault("MaxMessageSize", defaults.MaxMessageSize),
            MaxNestingDepth = limits.GetValueOrDefault("MaxNestingDepth", defaults.MaxNestingDepth),
            MaxNumericLiteralDigits = limits.GetValueOrDefault("MaxNumericLiteralDigits", defaults.MaxNumericLiteralDigits),
            MaxRepeatedCount = limits.GetValueOrDefault("MaxRepeatedCount", defaults.MaxRepeatedCount),
            MaxBigFloatExponent = limits.GetValueOrDefault("MaxBigFloatExponent", defaults.MaxBigFloatExponent),
        });
    }

    private static void PbText(string input, string schema)
    {
        var desc = TextReflection.Descriptor.MessageTypes.FirstOrDefault(m => m.FullName == schema)
            ?? throw new ArgumentException($"unknown schema: {schema}");
        // Bytes rather than text: the gate compares them, and Console would
        // translate the line ends on Windows.
        var text = new Encoder().Marshal(desc.Parser.ParseFrom(File.ReadAllBytes(input)));
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(new System.Text.UTF8Encoding(false).GetBytes(text));
    }

    private static void SbeDecode(string input, string schema, Dictionary<string, int> limits)
    {
        var codec = new Codec(AdversarialReflection.Descriptor)
        {
            MaxMessageSize = limits.GetValueOrDefault("MaxMessageSize", 64 << 20),
            MaxRepeatedCount = limits.GetValueOrDefault("MaxRepeatedCount", 64 << 20),
        };
        codec.Unmarshal(File.ReadAllBytes(input), Generated(schema).Parser.ParseFrom(ByteString.Empty));
    }

    private static object NewMessage(string schema) => schema switch
    {
        "adversarial.v1.Tree" => new Tree(),
        "adversarial.v1.StringHolder" => new StringHolder(),
        "adversarial.v1.BytesHolder" => new BytesHolder(),
        "adversarial.v1.BigIntHolder" => new BigIntHolder(),
        "adversarial.v1.BigNumHolder" => new BigNumHolder(),
        "adversarial.v1.ListHolder" => new ListHolder(),
        _ => throw new ArgumentException($"unknown schema: {schema}"),
    };
}

// Hand-mirrored from protowire/testdata/adversarial/adversarial.proto.
// The C# Pb decoder reads `[Protowire(N)]` attributes for field numbers.

internal sealed class Tree
{
    [Protowire(4)] public Dictionary<string, Tree> Branches { get; set; } = new();
    [Protowire(5)] public Dictionary<string, string> Tags { get; set; } = new();
    [Protowire(1)] public Tree? Child { get; set; }
    [Protowire(2)] public string Label { get; set; } = "";
    [Protowire(3)] public List<Tree> Children { get; set; } = new();
}

internal sealed class StringHolder
{
    [Protowire(1)] public string Value { get; set; } = "";
}

internal sealed class BytesHolder
{
    [Protowire(1)] public byte[] Value { get; set; } = Array.Empty<byte>();
}

internal sealed class BigIntHolder
{
    [Protowire(1)] public long Value { get; set; }
}

internal sealed class BigNumHolder
{
    [Protowire(1)] public System.Numerics.BigInteger BigInt { get; set; }
    [Protowire(2)] public Protowire.Pb.Decimal? Decimal { get; set; }
    [Protowire(3)] public Protowire.Pb.BigFloat? BigFloat { get; set; }
}

internal sealed class ListHolder
{
    [Protowire(1)] public List<int> Values { get; set; } = new();
}
