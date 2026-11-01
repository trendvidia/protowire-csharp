// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf.Tests.KeyedName;

namespace Protowire.Pxf.Tests;

// An entry name of a keyed block populates the element's key field (draft
// -01 §3.13), so it counts as the document supplying that field
// (trendvidia/protowire-go#214). The post-decode pass reads presence only.
//
// PostDecode does not enter list elements yet (#52), so the values here
// cannot be wrong today: only the presence marks can. The value assertions
// are what keep the name once it does.
public class KeyedEntryNameTests
{
    private static (P Msg, Result Result) Decode(string doc)
    {
        var m = new P();
        return (m, new Decoder().UnmarshalFull(doc, m));
    }

    public static TheoryData<string, string, string> EntryNames => new()
    {
        { "kd { alpha {} }", "kd[0].name", "alpha" },
        { "kd { \"not ident\" {} }", "kd[0].name", "not ident" },
        { "kd { alpha = {} }", "kd[0].name", "alpha" },
        { "kd = { alpha {} }", "kd[0].name", "alpha" },
        { "kd { a {} alpha {} }", "kd[1].name", "alpha" },
        { "kd { alpha { name = \"alpha\" } }", "kd[0].name", "alpha" },
        { "kr { alpha {} }", "kr[0].name", "alpha" },
        { "ko { alpha {} }", "ko[0].name", "alpha" },
        { "kn { a { inner { alpha {} } } }", "kn[0].inner[0].name", "alpha" },
    };

    [Theory]
    [MemberData(nameof(EntryNames))]
    public void EntryName_IsReportedSet(string doc, string mark, string _)
    {
        var (_, result) = Decode(doc);
        Assert.True(result.IsSet(mark), $"set: [{string.Join(", ", result.SetFields())}]");
        Assert.False(result.IsAbsent(mark));
    }

    [Theory]
    [MemberData(nameof(EntryNames))]
    public void EntryName_KeepsItsValue(string doc, string mark, string want)
    {
        var (m, _) = Decode(doc);
        string got = mark switch
        {
            "kd[0].name" => m.Kd[0].Name,
            "kd[1].name" => m.Kd[1].Name,
            "kr[0].name" => m.Kr[0].Name,
            "ko[0].name" => m.Ko[0].Name,
            "kn[0].inner[0].name" => m.Kn[0].Inner[0].Name,
            _ => throw new ArgumentException(mark),
        };
        Assert.Equal(want, got);
    }

    // Only an entry name supplies the key: the anonymous form leaves it absent.
    [Theory]
    [InlineData("kd = [{}]", "kd[0].name")]
    [InlineData("kr = [{}]", "kr[0].name")]
    public void AnonymousElement_LeavesTheKeyAbsent(string doc, string mark)
    {
        var (_, result) = Decode(doc);
        Assert.True(result.IsAbsent(mark));
    }

    [Fact]
    public void OneofSiblingOfTheKey_StaysOff()
    {
        var (m, _) = Decode("ko { alpha {} }");
        Assert.Equal(InOneof.PickOneofCase.Name, m.Ko[0].PickCase);
        Assert.Equal("", m.Ko[0].Other);
    }
}
