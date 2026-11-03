// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf.Tests.Bench;
using Protowire.Pxf.Tests.KeyedName;
using Protowire.Pxf.Tests.Presence;

namespace Protowire.Pxf.Tests;

// The rules a generated message's decode holds a document to, one case each.
// tests/Protowire.Conformance checks the same rules against protowire-go on
// the spec's corpora, but it needs Go, and these run in every `dotnet test`.
public class StrictDecoderTests
{
    private static PxfException Rejects(string doc, Google.Protobuf.IMessage msg) =>
        Assert.Throws<PxfException>(() => new Decoder().Unmarshal(doc, msg));

    // #19, and the table in protowire-go's lexer_duration_test.go.
    [Theory]
    [InlineData("1.5ms", 0, 1_500_000)]
    [InlineData("1.234567ms", 0, 1_234_567)]
    [InlineData("1.5h", 5400, 0)]
    [InlineData("2µs", 0, 2_000)]
    [InlineData("312.5µs", 0, 312_500)]
    [InlineData("1h30m0.5s", 5400, 500_000_000)]
    [InlineData("-2.5s", -2, -500_000_000)]
    [InlineData("100ns", 0, 100)]
    public void Duration_Fractions(string literal, long seconds, int nanos)
    {
        var got = new Config();
        new Decoder().Unmarshal($"timeout = {literal}\n", got);
        Assert.Equal(seconds, got.Timeout.Seconds);
        Assert.Equal(nanos, got.Timeout.Nanos);
    }

    [Theory]
    [InlineData("5min")]
    [InlineData("1.5min")]
    [InlineData("5seconds")]
    [InlineData("2μs")] // U+03BC GREEK SMALL LETTER MU is not the unit's µ
    [InlineData("1.5x")]
    [InlineData("1.ms")]
    [InlineData("1.5e3ms")]
    [InlineData("10000000h")] // past the literal's 64 bits of nanoseconds
    public void Duration_NotALiteral(string literal) =>
        Rejects($"timeout = {literal}\n", new Config());

    // #44
    [Theory]
    [InlineData("hostname = \"x\ny\"\n")]
    [InlineData("hostname = null\n")]
    [InlineData("tags = null\n")]
    [InlineData("tags = [\"a\", null]\n")]
    [InlineData("labels = { a: null }\n")]
    public void Null_And_Newline_Rejected(string doc) => Rejects(doc, new Config());

    [Fact]
    public void Null_ListElement_NamesTheField() =>
        Assert.Contains("repeated field \"tags\"", Rejects("tags = [null]\n", new Config()).Message);

    [Fact]
    public void Null_OnPresence_LeavesItUnset()
    {
        var got = new User();
        var result = new Decoder().UnmarshalFull("name = \"n\"\nrole = null\n", got);
        Assert.True(result.IsNull("role"));
        Assert.False(got.HasRole);
    }

    // #45
    [Fact]
    public void Wrapper_Scalar()
    {
        var got = new Defaults();
        new Decoder().Unmarshal("wrapped = 7\n", got);
        Assert.Equal(7, got.Wrapped);
    }

    [Fact]
    public void Wrapper_Null_LeavesItUnset()
    {
        var got = new Defaults();
        new Decoder().Unmarshal("wrapped = null\n", got);
        Assert.Null(got.Wrapped);
    }

    // #46
    [Theory]
    [InlineData("hostname = \"a\"\nhostname = \"a\"\n")]
    [InlineData("tls { verify = true }\ntls { cert_file = \"c\" }\n")]
    public void Singular_BoundTwice_Rejected(string doc) =>
        Assert.Contains("is already set", Rejects(doc, new Config()).Message);

    [Fact]
    public void Null_ThenValue_IsBoundTwice() =>
        Assert.Contains("is already set", Rejects("name = \"n\"\nrole = null\nrole = \"r\"\n", new User()).Message);

    // #48
    [Theory]
    [InlineData("labels = { a: \"1\" a: \"2\" }\n")]
    [InlineData("labels = { a: \"1\" }\nlabels = { a: \"2\" }\n")]
    public void MapKey_BoundTwice_Rejected(string doc) =>
        Assert.Contains("is already set", Rejects(doc, new Config()).Message);

    [Fact]
    public void MapKey_DistinctAcrossBindings_Merge()
    {
        var got = new Config();
        new Decoder().Unmarshal("labels = { a: \"1\" }\nlabels = { b: \"2\" }\n", got);
        Assert.Equal(2, got.Labels.Count);
    }

    // #49
    [Theory]
    [InlineData("tls { verify = true, cert_file = \"c\" }\n")]
    [InlineData("tls { verify = true; cert_file = \"c\" }\n")]
    [InlineData("labels = { a: \"1\", b: \"2\" }\n")]
    public void BlockEntries_TakeNoSeparator(string doc) => Rejects(doc, new Config());

    // #52
    [Fact]
    public void UnkeyedRepeated_RefusesABlock() =>
        Assert.Contains("must use list syntax", Rejects("endpoints { a { } }\n", new Config()).Message);

    [Fact]
    public void ListElements_TakeRequiredAndDefault()
    {
        var parent = new P();
        new Decoder().UnmarshalFull("kd = [{}, { name = \"x\" }]\n", parent);
        Assert.Equal(["dflt", "x"], parent.Kd.Select(e => e.Name));
    }

    // #63: a default must not clear the member the document chose.
    [Theory]
    [InlineData("ko = [{ name = \"x\" }]", "x", "")]
    [InlineData("ko = [{ name = null }]", "", "")]
    public void OneofDefault_LeavesAChosenSiblingAlone(string doc, string name, string other)
    {
        var got = new P();
        new Decoder().UnmarshalFull(doc, got);
        Assert.Equal(name, got.Ko[0].Name);
        Assert.Equal(other, got.Ko[0].Other);
    }

    [Fact]
    public void OneofDefault_AppliesWhenNoMemberIsWritten()
    {
        var got = new P();
        new Decoder().UnmarshalFull("ko = [{}]", got);
        Assert.Equal("other-dflt", got.Ko[0].Other);
    }

    // #65: a binding that would undo the entry name.
    [Theory]
    [InlineData("ko { alpha { other = \"x\" } }")]
    [InlineData("ko { alpha { other = null } }")]
    public void NamedEntry_RefusesASiblingOfItsKey(string doc)
    {
        var e = Assert.Throws<KeyedException>(() => new Decoder().UnmarshalFull(doc, new P()));
        Assert.Equal(KeyedErrorKind.KeyConflict, e.Kind);
    }

    [Fact]
    public void UnknownField_IsRejectedUnlessDiscarded()
    {
        Assert.Contains("unknown field", Rejects("nope = 1\n", new Config()).Message);

        var got = new Config();
        new Decoder { DiscardUnknown = true }.Unmarshal("nope = { a = [1] }\nhostname = \"h\"\n", got);
        Assert.Equal("h", got.Hostname);
    }
}
