// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pb.Tests;

// A field missing from a map entry is its type's default, so the entry stays
// and its value is never null (#60). Each byte string is one entry of field 1.
public class MapEntryDefaultsTests
{
    public class Inner
    {
        [Protowire(1)] public string Name { get; set; } = "";
    }

    public class Maps
    {
        [Protowire(1)] public Dictionary<string, string> Strings { get; set; } = new();
        [Protowire(2)] public Dictionary<string, Inner> Messages { get; set; } = new();
        [Protowire(3)] public Dictionary<int, string> Ints { get; set; } = new();
    }

    [Fact]
    public void StringValue_NoKey_TakesTheEmptyKey()
    {
        var got = new Maps();
        Pb.Unmarshal([0x0A, 0x03, 0x12, 0x01, (byte)'v'], got);

        Assert.Equal("v", Assert.Single(got.Strings).Value);
        Assert.Equal("", got.Strings.Keys.Single());
    }

    [Fact]
    public void MessageValue_NoKey_TakesTheEmptyKey()
    {
        var got = new Maps();
        Pb.Unmarshal([0x12, 0x05, 0x12, 0x03, 0x0A, 0x01, (byte)'n'], got);

        Assert.Equal("n", got.Messages[""].Name);
    }

    [Fact]
    public void IntKey_Missing_IsZero()
    {
        var got = new Maps();
        Pb.Unmarshal([0x1A, 0x03, 0x12, 0x01, (byte)'v'], got);

        Assert.Equal("v", got.Ints[0]);
    }

    [Fact]
    public void StringValue_NoValue_IsEmptyNotNull()
    {
        var got = new Maps();
        Pb.Unmarshal([0x0A, 0x03, 0x0A, 0x01, (byte)'k'], got);

        Assert.Equal("", got.Strings["k"]);
    }

    [Fact]
    public void MessageValue_NoValue_IsEmptyNotNull()
    {
        var got = new Maps();
        Pb.Unmarshal([0x12, 0x03, 0x0A, 0x01, (byte)'k'], got);

        Assert.Equal("", got.Messages["k"].Name);
    }

    [Fact]
    public void EmptyEntry_HoldsBothDefaults()
    {
        var got = new Maps();
        Pb.Unmarshal([0x0A, 0x00], got);

        Assert.Equal("", got.Strings[""]);
    }
}
