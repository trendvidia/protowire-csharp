// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pb;
using System.Numerics;

namespace Protowire.Pb.Tests;

public class PbTests
{
    public class SimpleMessage
    {
        [Protowire(1)] public int Id { get; set; }
        [Protowire(2)] public string Name { get; set; } = "";
        [Protowire(3)] public bool Active { get; set; }
    }

    [Fact]
    public void TestSimpleRoundTrip()
    {
        var msg = new SimpleMessage { Id = 42, Name = "test", Active = true };
        var data = Pb.Marshal(msg);
        
        var got = new SimpleMessage();
        Pb.Unmarshal(data, got);
        
        Assert.Equal(msg.Id, got.Id);
        Assert.Equal(msg.Name, got.Name);
        Assert.Equal(msg.Active, got.Active);
    }

    public class ZigZagMessage
    {
        [Protowire(1, ZigZag = true)] public int NegativeInt { get; set; }
        [Protowire(2, ZigZag = true)] public long NegativeLong { get; set; }
    }

    [Fact]
    public void TestZigZagRoundTrip()
    {
        var msg = new ZigZagMessage { NegativeInt = -123, NegativeLong = -456789L };
        var data = Pb.Marshal(msg);
        
        var got = new ZigZagMessage();
        Pb.Unmarshal(data, got);
        
        Assert.Equal(msg.NegativeInt, got.NegativeInt);
        Assert.Equal(msg.NegativeLong, got.NegativeLong);
    }

    public class NestedMessage
    {
        [Protowire(1)] public SimpleMessage? Simple { get; set; }
        [Protowire(2)] public List<int> Numbers { get; set; } = new();
    }

    [Fact]
    public void TestNestedRoundTrip()
    {
        var msg = new NestedMessage
        {
            Simple = new SimpleMessage { Id = 1, Name = "nested" },
            Numbers = { 10, 20, 30 }
        };
        var data = Pb.Marshal(msg);
        
        var got = new NestedMessage();
        Pb.Unmarshal(data, got);
        
        Assert.NotNull(got.Simple);
        Assert.Equal(msg.Simple.Id, got.Simple.Id);
        Assert.Equal(msg.Simple.Name, got.Simple.Name);
        Assert.Equal(msg.Numbers, got.Numbers);
    }

    public class BigNumMessage
    {
        [Protowire(1)] public BigInteger BigInt { get; set; }
        [Protowire(2)] public Decimal Dec { get; set; } = new();
    }

    [Fact]
    public void TestBigNumRoundTrip()
    {
        var msg = new BigNumMessage
        {
            BigInt = BigInteger.Parse("123456789012345678901234567890"),
            Dec = new Decimal(BigInteger.Parse("12345"), 2, true)
        };
        var data = Pb.Marshal(msg);
        
        var got = new BigNumMessage();
        Pb.Unmarshal(data, got);
        
        Assert.Equal(msg.BigInt, got.BigInt);
        Assert.Equal(msg.Dec.Unscaled, got.Dec.Unscaled);
        Assert.Equal(msg.Dec.Scale, got.Dec.Scale);
        Assert.Equal(msg.Dec.Negative, got.Dec.Negative);
    }

    public class ScaleMessage
    {
        [Protowire(1)] public Decimal Dec { get; set; } = new();
    }

    public class ExponentMessage
    {
        [Protowire(1)] public BigFloat Flt { get; set; } = new();
    }

    // bignum.proto declares scale and exponent int32, a plain varint. The
    // expected bytes come from `protoc --encode`, wrapped in field 1, so the
    // codec can't agree only with itself (#55).
    [Theory]
    [InlineData(2, "0a050a01051002")]
    [InlineData(-2, "0a0e0a010510feffffffffffffffff01")]
    [InlineData(4096, "0a060a0105108020")]
    public void TestDecimalScaleIsAPlainVarint(int scale, string hex)
    {
        var wire = Convert.FromHexString(hex);
        Assert.Equal(wire, Pb.Marshal(new ScaleMessage { Dec = new Decimal(5, scale, false) }));

        var got = new ScaleMessage();
        Pb.Unmarshal(wire, got);
        Assert.Equal(scale, got.Dec.Scale);
    }

    [Theory]
    [InlineData(3, "0a070a010510031835")]
    [InlineData(-3, "0a100a010510fdffffffffffffffff011835")]
    public void TestBigFloatExponentIsAPlainVarint(int exponent, string hex)
    {
        var wire = Convert.FromHexString(hex);
        Assert.Equal(wire, Pb.Marshal(new ExponentMessage { Flt = new BigFloat(5, exponent, 53, false) }));

        var got = new ExponentMessage();
        Pb.Unmarshal(wire, got);
        Assert.Equal(exponent, got.Flt.Exponent);
    }

    // Expected bytes from `protoc --encode`, wrapped in field 1.
    [Fact]
    public void TestZeroMagnitudeIsEmptyOnTheWire()
    {
        Assert.Equal(Convert.FromHexString("0a021002"),
            Pb.Marshal(new ScaleMessage { Dec = new Decimal(BigInteger.Zero, 2, false) }));
        // A BigFloat zero keeps prec and drops its exponent.
        Assert.Equal(Convert.FromHexString("0a021835"),
            Pb.Marshal(new ExponentMessage { Flt = new BigFloat(BigInteger.Zero, 3, 53, false) }));
    }

    public class DictionaryMessage
    {
        [Protowire(1)] public Dictionary<string, string> Labels { get; set; } = new();
    }

    [Fact]
    public void TestDictionaryRoundTrip()
    {
        var msg = new DictionaryMessage
        {
            Labels = { { "env", "prod" }, { "version", "1.0" } }
        };
        var data = Pb.Marshal(msg);
        
        var got = new DictionaryMessage();
        Pb.Unmarshal(data, got);
        
        Assert.Equal(msg.Labels, got.Labels);
    }

    public class OneofMessage
    {
        [Protowire(1, Oneof = "choice")] public string? A { get; set; }
        [Protowire(2, Oneof = "choice")] public int? B { get; set; }
    }

    [Fact]
    public void TestOneofRoundTrip()
    {
        var msg = new OneofMessage { A = "hello" };
        var data = Pb.Marshal(msg);
        
        var got = new OneofMessage();
        Pb.Unmarshal(data, got);
        Assert.Equal("hello", got.A);
        Assert.Null(got.B);

        msg = new OneofMessage { B = 42 };
        data = Pb.Marshal(msg);
        
        got = new OneofMessage();
        Pb.Unmarshal(data, got);
        Assert.Equal(42, got.B);
        Assert.Null(got.A);
    }

    // HARDENING.md § Recursion: the root is depth 0 and every submessage or
    // map entry is one descent. The submessage shape is protowire's corpus
    // rows pb/deep-submessage-100 / -101.
    public class Node
    {
        [Protowire(1)] public Node? Child { get; set; }
        [Protowire(2)] public Dictionary<string, Node> Map { get; set; } = new();
    }

    private static byte[] Delimited(int field, byte[] payload)
    {
        using var ms = new MemoryStream();
        using var output = new Google.Protobuf.CodedOutputStream(ms);
        output.WriteTag(field, Google.Protobuf.WireFormat.WireType.LengthDelimited);
        output.WriteBytes(Google.Protobuf.ByteString.CopyFrom(payload));
        output.Flush();
        return ms.ToArray();
    }

    private static byte[] Submessages(int n)
    {
        byte[] data = [];
        for (int i = 0; i < n; i++) data = Delimited(1, data);
        return data;
    }

    // n levels of map entry + value submessage: 2n descents.
    private static byte[] MapEntries(int n)
    {
        byte[] data = [];
        for (int i = 0; i < n; i++) data = Delimited(2, Delimited(2, data));
        return data;
    }

    [Fact]
    public void TestSubmessages_AtTheBound_Accepted()
    {
        var got = new Node();
        Pb.Unmarshal(Submessages(100), got);

        int depth = 0;
        for (var n = got.Child; n != null; n = n.Child) depth++;
        Assert.Equal(100, depth);
    }

    [Fact]
    public void TestSubmessages_OnePast_Rejected()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Pb.Unmarshal(Submessages(101), new Node()));
        Assert.Contains("nesting depth exceeds 100", e.Message);
    }

    [Fact]
    public void TestMapEntries_AtTheBound_Accepted() => Pb.Unmarshal(MapEntries(50), new Node());

    [Fact]
    public void TestMapEntries_OnePast_Rejected()
    {
        // 50 map levels are 100 descents; one submessage around them is 101.
        var doc = Delimited(1, MapEntries(50));
        var e = Assert.Throws<InvalidOperationException>(() => Pb.Unmarshal(doc, new Node()));
        Assert.Contains("nesting depth exceeds 100", e.Message);
    }
}
