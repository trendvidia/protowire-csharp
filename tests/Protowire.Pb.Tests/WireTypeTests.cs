// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Protowire.Pb.Tests;

// A tag's wire type, against protobuf-go (#116, trendvidia/protowire#614).
//
// A PB decoder does what protobuf does with a tag's wire type: it rejects wire
// type 6 or 7 on any field, and handles a known field whose wire type does not
// match its type as an unknown field, skipped by its wire type with the field
// left as it was. Unmarshal used to read a known field by its type whatever
// the wire type said, so `0a01` on an int64 read the length byte as the value
// 1, and skipped an unknown wire type 7 by consuming nothing.
//
// testdata/wire-types.json is protowire-go's TestUnmarshal_WireTypeMatchesProtobuf
// with its oracle written down by tool/wire_type_oracle: every field of a
// 25-field message gets one record of every wire type, well formed and cut
// short, alone and inside each message and map field. For each input it holds
// protobuf-go's verdict, the message protobuf-go read (without its unknown
// fields) re-encoded, and protowire-go's error.
public class WireTypeTests(ITestOutputHelper output)
{
    public class ConfInner
    {
        [Protowire(1)] public string Name { get; set; } = "";
        [Protowire(2)] public long N { get; set; }
    }

    // protowire-go's confAll. The oneof's members are plain fields, as there.
    public class ConfAll
    {
        [Protowire(1)] public string S { get; set; } = "";
        [Protowire(2)] public long I64 { get; set; }
        [Protowire(3)] public int I32 { get; set; }
        [Protowire(4)] public ulong U64 { get; set; }
        [Protowire(5)] public uint U32 { get; set; }
        [Protowire(6, ZigZag = true)] public long Z64 { get; set; }
        [Protowire(7, ZigZag = true)] public int Z32 { get; set; }
        [Protowire(8)] public bool B { get; set; }
        [Protowire(9)] public double F64 { get; set; }
        [Protowire(10)] public float F32 { get; set; }
        [Protowire(11)] public byte[]? Buf { get; set; }
        [Protowire(12)] public ConfInner? Inner { get; set; }
        [Protowire(13)] public List<ConfInner>? List { get; set; }
        [Protowire(14)] public List<uint>? Nums { get; set; }
        [Protowire(15, ZigZag = true)] public List<long>? Zigs { get; set; }
        [Protowire(16)] public List<float>? Reals { get; set; }
        [Protowire(17)] public List<string>? Strs { get; set; }
        [Protowire(18)] public Dictionary<string, long>? M { get; set; }
        [Protowire(19)] public Dictionary<int, ConfInner>? MM { get; set; }
        [Protowire(20)] public string A { get; set; } = "";
        [Protowire(21)] public long C { get; set; }
        [Protowire(22)] public ConfInner? D { get; set; }
        [Protowire(23)] public List<bool>? Flags { get; set; }
        [Protowire(24)] public List<long>? Negs { get; set; }
        [Protowire(25)] public Dictionary<bool, bool>? BM { get; set; }
    }

    private sealed record Row(string Input, bool Accept, string? Known, string? Error);

    private static List<Row> Table()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "wire-types.json");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path), options)!;
    }

    [Fact]
    public void MatchesProtobuf()
    {
        var table = Table();
        int accepted = 0, rejected = 0, sameText = 0;
        var failures = new List<string>();
        foreach (var row in table)
        {
            var input = Convert.FromHexString(row.Input);
            var got = new ConfAll();
            Exception? err = null;
            try
            {
                Pb.Unmarshal(input, got);
            }
            catch (Exception e)
            {
                err = e;
            }
            if (!row.Accept)
            {
                rejected++;
                if (err == null)
                {
                    failures.Add($"{row.Input}: protobuf-go rejects it, accepted here");
                    continue;
                }
                if (err is not InvalidOperationException and not Google.Protobuf.InvalidProtocolBufferException)
                {
                    failures.Add($"{row.Input}: rejected with a {err.GetType().Name}: {err.Message}");
                    continue;
                }
                // A field that cannot be skipped reads as protowire-go's does.
                // protowire-go prefixes a nested field's error with the fields
                // above it ("field Inner: "), which this port does not.
                int at = row.Error!.IndexOf("corrupt field ", StringComparison.Ordinal);
                if (at >= 0)
                {
                    if (err.Message != row.Error[at..])
                    {
                        failures.Add($"{row.Input}: error \"{err.Message}\", protowire-go's ends \"{row.Error[at..]}\"");
                        continue;
                    }
                    sameText++;
                }
                continue;
            }
            accepted++;
            if (err != null)
            {
                failures.Add($"{row.Input}: protobuf-go accepts it, rejected here: {err.Message}");
                continue;
            }
            var want = new ConfAll();
            Pb.Unmarshal(Convert.FromHexString(row.Known ?? ""), want);
            if (Render(want) != Render(got))
            {
                failures.Add($"{row.Input}: values differ from protobuf-go's: want {Render(want)}, got {Render(got)}");
            }
        }
        // A positive marker that both verdicts were exercised, and not only one.
        output.WriteLine($"{table.Count} inputs: protobuf-go accepts {accepted}, rejects {rejected}; {sameText} skip errors in protowire-go's words; {failures.Count} failures");
        foreach (var f in failures) output.WriteLine(f);
        Assert.Empty(failures);
        Assert.Equal(606, table.Count);
        Assert.Equal(254, accepted);
        Assert.Equal(352, rejected);
        Assert.Equal(302, sameText);
    }

    // The values a ConfAll holds, as text. A proto3 scalar, list or map that is
    // empty is the same as one never set; a message is not.
    private static string Render(ConfAll m)
    {
        var sb = new StringBuilder();
        void Add(string name, object? v) => sb.Append(name).Append('=').Append(v).Append(';');
        static string Inner(ConfInner? i) => i == null ? "nil" : $"{{{i.Name}|{i.N}}}";
        static string Seq<T>(IEnumerable<T>? xs, Func<T, string> f) => xs == null ? "[]" : "[" + string.Join(",", xs.Select(f)) + "]";
        static string Num(object o) => Convert.ToString(o, CultureInfo.InvariantCulture)!;
        Add("s", m.S);
        Add("i64", m.I64);
        Add("i32", m.I32);
        Add("u64", m.U64);
        Add("u32", m.U32);
        Add("z64", m.Z64);
        Add("z32", m.Z32);
        Add("b", m.B);
        Add("f64", BitConverter.DoubleToInt64Bits(m.F64));
        Add("f32", BitConverter.SingleToInt32Bits(m.F32));
        Add("buf", Convert.ToHexString(m.Buf ?? []));
        Add("inner", Inner(m.Inner));
        Add("list", Seq(m.List, Inner));
        Add("nums", Seq(m.Nums, x => Num(x)));
        Add("zigs", Seq(m.Zigs, x => Num(x)));
        Add("reals", Seq(m.Reals, x => Num(BitConverter.SingleToInt32Bits(x))));
        Add("strs", Seq(m.Strs, x => x));
        Add("m", Seq(m.M?.OrderBy(e => e.Key, StringComparer.Ordinal), e => $"{e.Key}:{e.Value}"));
        Add("mm", Seq(m.MM?.OrderBy(e => e.Key), e => $"{e.Key}:{Inner(e.Value)}"));
        Add("a", m.A);
        Add("c", m.C);
        Add("d", Inner(m.D));
        Add("flags", Seq(m.Flags, x => Num(x)));
        Add("negs", Seq(m.Negs, x => Num(x)));
        Add("bm", Seq(m.BM?.OrderBy(e => e.Key), e => $"{e.Key}:{e.Value}"));
        return sb.ToString();
    }

    public class I64Holder
    {
        [Protowire(1)] public long V { get; set; }
    }

    public class StrHolder
    {
        [Protowire(1)] public string V { get; set; } = "";
    }

    // The rows of #116's table, by name, with protowire-go's error text.
    [Theory]
    [InlineData("int64, wire type 6", "0e01", false, "corrupt field 1: proto: cannot parse reserved wire type")]
    [InlineData("string, wire type 7", "0f0161", true, "corrupt field 1: proto: cannot parse reserved wire type")]
    [InlineData("string, wire type 6", "0e0161", true, "corrupt field 1: proto: cannot parse reserved wire type")]
    [InlineData("unknown field 99, wire type 7", "9f06", false, "corrupt field 99: proto: cannot parse reserved wire type")]
    [InlineData("int64 sent as wire type 2", "0a0101", false, null)]
    [InlineData("int64 sent as wire type 5", "0d01000000", false, null)]
    [InlineData("string sent as wire type 0", "0801", true, null)]
    [InlineData("int64 as wire type 2, cut short", "0a01", false, "corrupt field 1: unexpected EOF")]
    [InlineData("int64 as wire type 5, cut short", "0d01", false, "corrupt field 1: unexpected EOF")]
    public void IssueRows(string name, string hex, bool str, string? error)
    {
        _ = name;
        object m = str ? new StrHolder() : new I64Holder();
        var e = Record.Exception(() => Pb.Unmarshal(Convert.FromHexString(hex), m));
        if (error == null)
        {
            Assert.Null(e);
        }
        else
        {
            Assert.Equal(error, Assert.IsType<InvalidOperationException>(e).Message);
        }
        if (m is StrHolder s) Assert.Equal("", s.V);
        else Assert.Equal(0, ((I64Holder)m).V);
    }

    public class Holder
    {
        [Protowire(1)] public long? P { get; set; }
        [Protowire(2)] public List<string>? L { get; set; }
        [Protowire(3)] public string Kept { get; set; } = "";
    }

    // A skipped field is not touched: a nullable stays null and a list gets no
    // element.
    [Fact]
    public void MismatchLeavesFieldAlone()
    {
        var m = new Holder();
        Pb.Unmarshal(Convert.FromHexString("0d01000000" + "1001" + "1a0161"), m);
        Assert.Null(m.P);
        Assert.Null(m.L);
        Assert.Equal("a", m.Kept);
    }

    public class OneofHolder
    {
        [Protowire(1, Oneof = "choice")] public string? A { get; set; }
        [Protowire(2, Oneof = "choice")] public string? B { get; set; }
    }

    // A tag that is skipped is not a oneof member's value, so it does not clear
    // the member that is set.
    [Fact]
    public void MismatchLeavesOneofAlone()
    {
        var m = new OneofHolder();
        Pb.Unmarshal(Convert.FromHexString("0a0161" + "1001"), m);
        Assert.Equal("a", m.A);
        Assert.Null(m.B);
    }

    // Groups are skipped as protobuf-go skips them: nested, matched by number,
    // and a reserved wire type inside one is refused.
    [Theory]
    [InlineData("4b" + "0b" + "0c" + "1001" + "4c" + "0801", null)]
    [InlineData("4b" + "0e01" + "4c", "corrupt field 9: proto: cannot parse reserved wire type")]
    [InlineData("4b" + "54", "corrupt field 9: proto: mismatching end group marker")]
    [InlineData("4b" + "0801", "corrupt field 9: unexpected EOF")]
    [InlineData("4b" + "00", "corrupt field 9: proto: invalid field number")]
    [InlineData("4b" + "08ffffffffffffffffff02" + "4c", "corrupt field 9: proto: variable length integer overflow")]
    public void UnknownGroups(string hex, string? error)
    {
        var m = new I64Holder();
        var e = Record.Exception(() => Pb.Unmarshal(Convert.FromHexString(hex), m));
        if (error == null)
        {
            Assert.Null(e);
            Assert.Equal(1, m.V);
        }
        else
        {
            Assert.Equal(error, Assert.IsType<InvalidOperationException>(e).Message);
        }
    }

    // protobuf-go skips groups nested 10001 deep and refuses one more, without
    // a stack frame per level here.
    [Theory]
    [InlineData(10001, true)]
    [InlineData(10002, false)]
    public void DeepUnknownGroups(int depth, bool accept)
    {
        var b = new List<byte>();
        for (int i = 0; i < depth; i++) b.Add(0x4b);
        for (int i = 0; i < depth; i++) b.Add(0x4c);
        var e = Record.Exception(() => Pb.Unmarshal([.. b], new I64Holder()));
        if (accept) Assert.Null(e);
        else Assert.Equal("corrupt field 9: proto: parse error", Assert.IsType<InvalidOperationException>(e).Message);
    }

    public class MapHolder
    {
        [Protowire(1)] public Dictionary<string, long> M { get; set; } = new();
    }

    // A map entry's key and value are read by the same rule.
    [Theory]
    [InlineData("0a08" + "0a0161" + "1501000000", "a", 0L, null)]
    [InlineData("0a07" + "0d01000000" + "1007", "", 7L, null)]
    [InlineData("0a03" + "0a0161" + "", "a", 0L, null)]
    [InlineData("0a02" + "1601", null, 0L, "corrupt field 2 in map entry: proto: cannot parse reserved wire type")]
    [InlineData("0a02" + "1201", null, 0L, "corrupt field 2 in map entry: unexpected EOF")]
    public void MapEntries(string hex, string? key, long value, string? error)
    {
        var m = new MapHolder();
        var e = Record.Exception(() => Pb.Unmarshal(Convert.FromHexString(hex), m));
        if (error != null)
        {
            Assert.Equal(error, Assert.IsType<InvalidOperationException>(e).Message);
            return;
        }
        Assert.Null(e);
        var entry = Assert.Single(m.M);
        Assert.Equal(key, entry.Key);
        Assert.Equal(value, entry.Value);
    }

    public class BigHolder
    {
        [Protowire(1)] public BigInteger I { get; set; }
        [Protowire(2)] public Decimal? R { get; set; }
        [Protowire(3)] public BigFloat? F { get; set; }
    }

    // The big-number messages are read by the same rule: a field whose wire
    // type does not match is skipped, and wire types 6 and 7 are refused.
    // protowire-go's TestUnmarshal_WireTypeBigNumbers, with its error text.
    [Fact]
    public void BigNumbers()
    {
        static BigHolder Read(string hex)
        {
            var m = new BigHolder();
            Pb.Unmarshal(Convert.FromHexString(hex), m);
            return m;
        }
        static string Refused(string hex) =>
            Assert.Throws<InvalidOperationException>(() => Read(hex)).Message;

        // BigInt{abs: 5, negative sent as wire type 2}: negative is skipped.
        Assert.Equal(new BigInteger(5), Read("0a06" + "0a0105" + "120101").I);
        // BigInt{abs sent as a varint}: abs is skipped, the value is 0.
        Assert.Equal(BigInteger.Zero, Read("0a02" + "0805").I);
        // Decimal{unscaled: 15, scale sent as fixed32}: scale stays 0.
        var r = Read("1208" + "0a010f" + "1501000000").R!;
        Assert.Equal((new BigInteger(15), 0, false), (r.Unscaled, r.Scale, r.Negative));
        // BigFloat{mantissa: 1, exponent sent as bytes, prec 53}: exponent stays 0.
        var f = Read("1a08" + "0a0101" + "120101" + "1835").F!;
        Assert.Equal((BigInteger.One, 0, 53u, false), (f.Mantissa, f.Exponent, f.Precision, f.Negative));

        Assert.Equal("corrupt field 1 in BigInt: proto: cannot parse reserved wire type", Refused("0a02" + "0e01"));
        Assert.Equal("corrupt field 3 in Decimal: proto: cannot parse reserved wire type", Refused("1203" + "1f0101"));
        Assert.Equal("corrupt field 4 in BigFloat: proto: cannot parse reserved wire type", Refused("1a02" + "2601"));
        Assert.Equal("corrupt field 2 in BigFloat: unexpected EOF", Refused("1a02" + "1201"));
    }
}
