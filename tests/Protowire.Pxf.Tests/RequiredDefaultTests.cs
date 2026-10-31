// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf.Tests.RequiredDefault;

namespace Protowire.Pxf.Tests;

// draft -01 "Required and Default": the pair is refused when the schema is
// bound, whatever the document holds.
public class RequiredDefaultTests
{
    private const string Exclusive = "are exclusive on one field";

    [Theory]
    [InlineData("")]                // the field is absent
    [InlineData("f = \"present\"")]  // the field is present, so no document can hide the schema
    [InlineData("%%% not pxf")]     // the document is never read
    public void Pair_RefusedByUnmarshalFull(string doc)
    {
        var e = Assert.Throws<PxfException>(() => new Decoder().UnmarshalFull(doc, new RequiredWithDefault()));
        Assert.Contains("(pxf.required) and (pxf.default) " + Exclusive, e.Message);
        Assert.Contains("required_default.v1.RequiredWithDefault.f", e.Message);
    }

    [Fact]
    public void Pair_RefusedByUnmarshal()
    {
        var e = Assert.Throws<PxfException>(() => new Decoder().Unmarshal("", new RequiredWithDefault()));
        Assert.Contains(Exclusive, e.Message);
    }

    [Fact]
    public void Pair_InImportedFile_RefusedAndAttributedToThatFile()
    {
        var e = Assert.Throws<PxfException>(() => new Decoder().UnmarshalFull("s = \"x\"", new Clean()));
        Assert.Contains("pxf-required-default-test.proto: field", e.Message);
    }

    [Fact]
    public void ValidateDescriptor_ReportsEveryPair_NestedAndEmptyDefaultIncluded()
    {
        var vs = Schema.ValidateDescriptor(Clean.Descriptor);
        Assert.Equal(
            ["required_default.v1.Outer.Inner.empty_default", "required_default.v1.RequiredWithDefault.f"],
            vs.Select(v => v.Element));
        Assert.All(vs, v => Assert.Equal("pxf-required-default-test.proto", v.File));
    }

    [Fact]
    public void RequiredFalse_WithDefault_Accepted()
    {
        Assert.Empty(Schema.ValidateDescriptor(NotRequiredWithDefault.Descriptor));

        var got = new NotRequiredWithDefault();
        new Decoder().UnmarshalFull("", got);
        Assert.Equal("x", got.F);
    }

    public class PocoPair
    {
        [PxfRequired, PxfDefault("x")] public string F { get; set; } = "";
    }

    public class PocoDefaultOnly
    {
        [PxfDefault("x")] public string F { get; set; } = "";
    }

    [Theory]
    [InlineData("")]
    [InlineData("F = \"present\"")]
    public void PocoAttributes_Pair_Refused(string doc)
    {
        var e = Assert.Throws<PxfException>(() => new Decoder().Unmarshal(doc, new PocoPair()));
        Assert.Contains("[PxfRequired] and [PxfDefault] " + Exclusive, e.Message);
    }

    [Fact]
    public void PocoAttributes_DefaultAlone_Applied()
    {
        var got = new PocoDefaultOnly();
        new Decoder().Unmarshal("", got);
        Assert.Equal("x", got.F);
    }
}
