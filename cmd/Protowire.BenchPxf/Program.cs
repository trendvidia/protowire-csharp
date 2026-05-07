// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
// Cross-port PXF microbench: C# implementation.
//
// Reads `testdata/bench-test.pxf` (text payload), times unmarshal + marshal
// of `bench.v1.Config` for at least `--seconds` (default 3), and prints
// one JSON line per op:
//
//   {"port":"csharp","op":"unmarshal","ns_per_op":...,"mib_per_sec":...,"iterations":...,"bytes":...}
//   {"port":"csharp","op":"marshal","ns_per_op":...,"iterations":...}
//
// The other ports' bench-pxf binaries print the same shape; the spec repo's
// `scripts/cross_pxf_bench.sh` runner aggregates them.
//
// Mirrors `protowire-go/scripts/bench_pxf/main.go`.

using System.Diagnostics;
using System.Globalization;
using Protowire.Pxf;
using Protowire.Pxf.Tests.Bench;

double seconds = 3.0;
string? testdataDir = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seconds":
            seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--testdata":
            testdataDir = args[++i];
            break;
        default:
            Console.Error.WriteLine($"unknown flag: {args[i]}");
            Environment.Exit(2);
            break;
    }
}

testdataDir ??= Path.Combine(Directory.GetCurrentDirectory(), "testdata");
string pxfPath = Path.Combine(testdataDir, "bench-test.pxf");
string pxfText = File.ReadAllText(pxfPath);
int payloadBytes = System.Text.Encoding.UTF8.GetByteCount(pxfText);

// Warm up.
{
    var cfg = new Config();
    new Decoder().Unmarshal(pxfText, cfg);
}

var (itersU, elapsedU) = TimeLoop(seconds, () =>
{
    var cfg = new Config();
    new Decoder().Unmarshal(pxfText, cfg);
});
EmitJson(new Dictionary<string, object>
{
    ["port"] = "csharp",
    ["op"] = "unmarshal",
    ["ns_per_op"] = elapsedU.Ticks * 100 / itersU,
    ["mib_per_sec"] = (double)payloadBytes * itersU / (1024.0 * 1024.0) / elapsedU.TotalSeconds,
    ["iterations"] = itersU,
    ["bytes"] = payloadBytes,
});

// Seed a populated message for the marshal loop.
var seed = new Config();
new Decoder().Unmarshal(pxfText, seed);

var (itersM, elapsedM) = TimeLoop(seconds, () => new Encoder().Marshal(seed));
EmitJson(new Dictionary<string, object>
{
    ["port"] = "csharp",
    ["op"] = "marshal",
    ["ns_per_op"] = elapsedM.Ticks * 100 / itersM,
    ["iterations"] = itersM,
});


static (long iters, TimeSpan elapsed) TimeLoop(double seconds, Action fn)
{
    var sw = Stopwatch.StartNew();
    long deadline = sw.ElapsedTicks + (long)(seconds * Stopwatch.Frequency);
    long iters = 0;
    while (true)
    {
        // Run in batches of 64 to keep timer overhead in the noise.
        for (int i = 0; i < 64; i++) fn();
        iters += 64;
        if (sw.ElapsedTicks >= deadline) break;
    }
    return (iters, sw.Elapsed);
}

static void EmitJson(IReadOnlyDictionary<string, object> record)
{
    var sb = new System.Text.StringBuilder("{");
    bool first = true;
    foreach (var (k, v) in record)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"').Append(k).Append("\":");
        switch (v)
        {
            case string s:
                sb.Append('"').Append(s).Append('"');
                break;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                break;
        }
    }
    sb.Append('}');
    Console.WriteLine(sb.ToString());
}
