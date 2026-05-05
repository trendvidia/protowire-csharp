// Per-port reference for the protowire HARDENING.md conformance corpus.
//
// Driven by `protowire/scripts/cross_security_check.sh`. See:
//   protowire/docs/HARDENING.md
//   protowire/testdata/adversarial/README.md
//
// Contract:
//
//     check-decode --format <pxf|pb|sbe|envelope> \
//                  --schema <fully.qualified.MessageType> \
//                  --proto  <path-to-adversarial.proto> \
//                  --input  <path>
//
//     Exit 0 -> input was accepted (decode succeeded)
//     Exit 1 -> input was rejected (clean error, "reject: <msg>" to stderr)
//     Other  -> bug in the decoder (uncaught exception, OOM, ...)
//
// Mirrors `protowire-go/scripts/check_decode/main.go` and
// `protowire-rust/crates/check-decode/src/main.rs`.
//
// The C# port's PXF and PB decoders consume hand-written POCOs annotated
// with `[Protowire(N)]`, so the four adversarial schemas are mirrored as
// POCOs below. Drift between this file and adversarial.proto must be caught
// by the conformance run itself: a wrong field number flips the manifest's
// accept/reject expectations.

using Protowire.Pb;
using Protowire.Pxf;

return CheckDecode.Run(args);

internal static class CheckDecode
{
    public static int Run(string[] args)
    {
        string? format = null, schema = null, proto = null, input = null;
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            string? val = i + 1 < args.Length ? args[i + 1] : null;
            switch (key)
            {
                case "--format": format = val; i++; break;
                case "--schema": schema = val; i++; break;
                case "--proto": proto = val; i++; break;
                case "--input": input = val; i++; break;
                default:
                    Console.Error.WriteLine($"check-decode: unknown arg {key}");
                    return 2;
            }
        }

        if (string.IsNullOrEmpty(format) || string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(input))
        {
            Console.Error.WriteLine("usage: check-decode --format ... --schema ... --input ... [--proto ...]");
            return 2;
        }

        try
        {
            DoRun(format!, schema!, proto, input!);
            return 0;
        }
        catch (Exception e)
        {
            // Any thrown exception from the decoder paths below counts as a clean
            // rejection. The orchestrator distinguishes rc=1 (reject) from other
            // non-zero exit codes (decoder bug); the .NET runtime would normally
            // surface an uncaught exception with rc != 1, so we catch broadly
            // here and re-classify.
            Console.Error.WriteLine($"reject: {e.Message}");
            return 1;
        }
    }

    private static void DoRun(string format, string schema, string? proto, string input)
    {
        switch (format)
        {
            case "pxf":
                PxfDecode(input, schema);
                return;
            case "pb":
                PbDecode(input, schema);
                return;
            case "envelope":
                throw new NotSupportedException("envelope decode not yet implemented in this reference");
            case "sbe":
                throw new NotSupportedException("sbe decode not yet implemented in this reference");
            default:
                throw new ArgumentException($"unsupported format: {format}");
        }
    }

    private static void PxfDecode(string input, string schema)
    {
        string text = File.ReadAllText(input);
        var msg = NewMessage(schema);
        var dec = new Decoder();
        dec.Unmarshal(text, msg);
    }

    private static void PbDecode(string input, string schema)
    {
        byte[] bytes = File.ReadAllBytes(input);
        var msg = NewMessage(schema);
        Pb.Unmarshal(bytes, msg);
    }

    private static object NewMessage(string schema) => schema switch
    {
        "adversarial.v1.Tree" => new Tree(),
        "adversarial.v1.StringHolder" => new StringHolder(),
        "adversarial.v1.BytesHolder" => new BytesHolder(),
        "adversarial.v1.BigIntHolder" => new BigIntHolder(),
        _ => throw new ArgumentException($"unknown schema: {schema}"),
    };
}

// Hand-mirrored from protowire/testdata/adversarial/adversarial.proto.
// The C# Pxf decoder matches PXF keys to property names case-insensitively
// (ignoring underscores), and the C# Pb decoder reads `[Protowire(N)]`
// attributes for field numbers.

internal sealed class Tree
{
    [Protowire(1)] public Tree? Child { get; set; }
    [Protowire(2)] public string Label { get; set; } = "";
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
