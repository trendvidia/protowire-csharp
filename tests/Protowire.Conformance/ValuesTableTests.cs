// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Numerics;
using Protowire.Pb;
using PbDecimal = Protowire.Pb.Decimal;

namespace Protowire.Conformance;

// The struct-binding table (trendvidia/protowire#509, #518; #82 here):
// testdata/values/values.tsv lists values written through a struct API with
// the bytes protowire-go's encoding/pb writes for each. This holds a twin of
// every struct in the spec's values_table_test.go, marshals the row's value
// with Pb.Marshal, and compares the bytes. The file is a byte copy of the
// spec's; the scheduled Go-conformance job diffs it with the spec.
public class ValuesTableTests
{
    public sealed class Ints
    {
        [Protowire(1)] public long A { get; set; }
        [Protowire(2)] public long B { get; set; }
        [Protowire(3)] public int C { get; set; }
        [Protowire(4)] public Dictionary<string, long> M { get; set; } = new();
        [Protowire(5)] public Dictionary<string, int> MI { get; set; } = new();
    }

    public sealed class Age
    {
        [Protowire(1)] public int Value { get; set; }
    }

    public sealed class MinLong
    {
        [Protowire(1)] public long V { get; set; }
    }

    public sealed class Zigzag
    {
        [Protowire(1, ZigZag = true)] public long D { get; set; }
        [Protowire(2, ZigZag = true)] public int E { get; set; }
    }

    public sealed class Packed
    {
        [Protowire(1)] public List<int> Xs { get; set; } = new();
        [Protowire(2)] public List<long> Ys { get; set; } = new();
        [Protowire(3)] public List<bool> Bs { get; set; } = new();
        [Protowire(4)] public List<double> Ds { get; set; } = new();
        [Protowire(5)] public List<float> Fs { get; set; } = new();
    }

    public sealed class ZeroMapEntry
    {
        [Protowire(1)] public Dictionary<string, string> M { get; set; } = new();
        [Protowire(2)] public Dictionary<string, int> Z { get; set; } = new();
        [Protowire(3)] public Dictionary<int, string> K { get; set; } = new();
    }

    public sealed class DecimalHolder
    {
        [Protowire(1)] public PbDecimal? D { get; set; }
    }

    public sealed class Decimals
    {
        [Protowire(1)] public List<PbDecimal?> Ds { get; set; } = new();
    }

    public sealed class Leaf
    {
        [Protowire(1)] public string Name { get; set; } = "";
        [Protowire(2)] public int N { get; set; }
    }

    public sealed class Nested
    {
        [Protowire(1)] public string S { get; set; } = "";
        [Protowire(2)] public List<Leaf> Ls { get; set; } = new();
        [Protowire(3)] public List<string> Ss { get; set; } = new();
    }

    public sealed class Zeros
    {
        [Protowire(1)] public int I { get; set; }
        [Protowire(2)] public string S { get; set; } = "";
        [Protowire(3)] public bool B { get; set; }
        [Protowire(4)] public double D { get; set; }
        [Protowire(5)] public byte[] Bs { get; set; } = [];
        [Protowire(6)] public ulong U { get; set; }
    }

    public sealed class BigNums
    {
        [Protowire(1)] public BigInteger? I { get; set; }
        [Protowire(2)] public PbDecimal? D { get; set; }
        [Protowire(3)] public BigFloat? F { get; set; }
    }

    public sealed class BigInts
    {
        [Protowire(1)] public List<BigInteger?> Is { get; set; } = new();
    }

    public sealed class BigFloatMap
    {
        [Protowire(1)] public Dictionary<string, BigFloat?> M { get; set; } = new();
    }

    public sealed class BigFloatHolder
    {
        [Protowire(1)] public BigFloat? F { get; set; }
    }

    private static PbDecimal Dec(long unscaled, int scale, bool negative = false) => new(unscaled, scale, negative);

    // Each row's value, rebuilt from its words in the table.
    private static readonly Dictionary<string, Func<object>> Values = new()
    {
        ["Ints"] = () => new Ints { A = -1, B = -1, C = -1, M = { ["k"] = -1 }, MI = { ["k"] = -1 } },
        ["Age"] = () => new Age { Value = 30 },
        ["MinLong"] = () => new MinLong { V = long.MinValue },
        ["Zigzag"] = () => new Zigzag { D = -1, E = -2 },
        ["Packed"] = () => new Packed { Xs = { 1, 0, -1 }, Ys = { 300 }, Bs = { true, false }, Ds = { 1.5 }, Fs = { -2.5f } },
        ["ZeroMapEntry"] = () => new ZeroMapEntry { M = { [""] = "" }, Z = { ["zero"] = 0 }, K = { [0] = "v" } },
        ["Decimal"] = () => new DecimalHolder { D = Dec(31415, 4) },
        ["DecimalOne"] = () => new DecimalHolder { D = Dec(100, 2) },
        ["DecimalFiveE2"] = () => new DecimalHolder { D = Dec(5, -2) },
        ["DecimalFifteen"] = () => new DecimalHolder { D = Dec(1500, 2) },
        ["DecimalZero"] = () => new DecimalHolder { D = Dec(0, 2) },
        ["DecimalNegative"] = () => new DecimalHolder { D = Dec(150, 2, negative: true) },
        ["Decimals"] = () => new Decimals { Ds = { Dec(0, 2), Dec(100, 2), Dec(5, -2) } },
        ["Nested"] = () => new Nested { S = "x", Ls = { new Leaf(), new Leaf { Name = "l", N = -3 } }, Ss = { "", "s" } },
        ["Zeros"] = () => new Zeros(),
        ["BigNumsNull"] = () => new BigNums(),
        ["BigNumsZero"] = () => new BigNums { I = BigInteger.Zero, D = Dec(0, 0), F = new BigFloat(BigInteger.Zero, 0, 0, false) },
        ["BigNumsNegative"] = () => new BigNums
        {
            I = BigInteger.Parse("-12345678901234567890", System.Globalization.CultureInfo.InvariantCulture),
            D = Dec(5, 1, negative: true),
            F = new BigFloat(3, -1, 53, negative: true),
        },
        ["BigIntsZeroElement"] = () => new BigInts { Is = { BigInteger.Zero, BigInteger.One } },
        ["BigIntsNullElement"] = () => new BigInts { Is = { BigInteger.One, null } },
        ["DecimalsNullElement"] = () => new Decimals { Ds = { Dec(1, 0), null } },
        ["BigFloatMapNullValue"] = () => new BigFloatMap { M = { ["k"] = null } },
        ["BigFloatPrec64"] = () => new BigFloatHolder { F = new BigFloat(3, -1, 64, false) },
    };

    public static TheoryData<string, string> Rows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "testdata", "values", "values.tsv")))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var cols = line.Split('\t');
            rows.Add(cols[0], cols[1]);
        }
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void WritesProtowireGosBytes(string name, string hex)
    {
        Assert.True(Values.TryGetValue(name, out var build), $"no twin for row {name}: add it from the spec's values_table_test.go");
        Assert.Equal(hex, Convert.ToHexString(Pb.Pb.Marshal(build!())).ToLowerInvariant());
    }

    [Fact]
    public void EveryTwinHasARow()
    {
        var names = Rows().Select(r => (string)r[0]).ToHashSet();
        var orphans = Values.Keys.Where(k => !names.Contains(k)).ToList();
        Assert.True(orphans.Count == 0, $"twins with no row: {string.Join(", ", orphans)}");
    }
}
