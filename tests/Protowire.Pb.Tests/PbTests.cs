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
}
