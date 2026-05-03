// Cross-port SBE microbench: C# implementation.
//
// Loads the canonical `bench.v1.Order` (10 scalars + 2-entry Fill group) and
// times marshal + unmarshal for at least `--seconds` (default 3). Prints
// one JSON line per op:
//
//   {"port":"csharp","op":"sbe-marshal","ns_per_op":...,"iterations":...,"bytes":94}
//   {"port":"csharp","op":"sbe-unmarshal","ns_per_op":...,"mib_per_sec":...,"iterations":...,"bytes":94}
//
// The other ports' bench-sbe binaries print the same shape; the spec repo's
// `scripts/cross_sbe_bench.sh` runner aggregates them.
//
// Mirrors `protowire-go/scripts/bench_sbe/main.go`.

using System.Diagnostics;
using System.Globalization;
using Protowire.Sbe;
using Protowire.Sbe.Tests.Bench;

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

// testdataDir is accepted for parity with the other ports' bench-sbe
// binaries, but C# builds the canonical Order in-process from generated
// classes — no descriptor-set file is read here.
_ = testdataDir;

var codec = new Codec(SbeBenchReflection.Descriptor);

var order = new Order
{
    OrderId = 1001,
    Symbol = "AAPL",
    Price = 19150,
    Quantity = 100,
    Side = Side.Sell,
    Active = true,
    Weight = 0.85,
    Score = 2.5f,
};
order.Fills.Add(new Order.Types.Fill { FillPrice = 19155, FillQty = 25, FillId = 5001 });
order.Fills.Add(new Order.Types.Fill { FillPrice = 19160, FillQty = 50, FillId = 5002 });

// One marshal up front to populate the bytes-count and warm caches.
byte[] wire = codec.Marshal(order);
int n = wire.Length;

var (itersM, elapsedM) = TimeLoop(seconds, () => codec.Marshal(order));
EmitJson(new Dictionary<string, object>
{
    ["port"] = "csharp",
    ["op"] = "sbe-marshal",
    ["ns_per_op"] = elapsedM.Ticks * 100 / itersM,
    ["iterations"] = itersM,
    ["bytes"] = n,
});

var (itersU, elapsedU) = TimeLoop(seconds, () =>
{
    var got = new Order();
    codec.Unmarshal(wire, got);
});
EmitJson(new Dictionary<string, object>
{
    ["port"] = "csharp",
    ["op"] = "sbe-unmarshal",
    ["ns_per_op"] = elapsedU.Ticks * 100 / itersU,
    ["mib_per_sec"] = (double)n * itersU / (1024.0 * 1024.0) / elapsedU.TotalSeconds,
    ["iterations"] = itersU,
    ["bytes"] = n,
});


static (long iters, TimeSpan elapsed) TimeLoop(double seconds, Action fn)
{
    var sw = Stopwatch.StartNew();
    long deadline = sw.ElapsedTicks + (long)(seconds * Stopwatch.Frequency);
    long iters = 0;
    while (true)
    {
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
