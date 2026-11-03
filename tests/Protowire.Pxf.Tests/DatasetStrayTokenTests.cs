// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf;
using Xunit;

namespace Protowire.Pxf.Tests;

// A token beside @dataset rows is reported as protowire-go reports it (#96):
// what it is doing there comes before what is wrong with it. The expected
// messages were printed by protowire-go f8a01b1's pxf.Parse and
// pxf.Unmarshal for the same documents.
public class DatasetStrayTokenTests
{
    [Theory]
    [InlineData("@dataset t.M (a)\n(1)\n$\n(2)\n",
        "3:1: @dataset directive cannot coexist with top-level field entries; the document's payload is the @dataset rows (draft §3.4.4)")]
    [InlineData("@dataset t.M (a)\n$\n",
        "2:1: @dataset directive cannot coexist with top-level field entries; the document's payload is the @dataset rows (draft §3.4.4)")]
    [InlineData("@dataset t.M (a)\n(1, 2)\n$\n",
        "2:1: @dataset row has 2 cells, expected 1 (column count)")]
    public void ParseReportsWhatTheReferenceReports(string doc, string want)
    {
        var e = Assert.ThrowsAny<PxfException>(() => Parser.Parse(doc));
        Assert.Equal(want, e.Message);
    }

    // Unmarshal refuses any @dataset document beside entries at the
    // header, as protowire-go's Unmarshal does (decode_fast.go), with its
    // own shorter message.
    [Theory]
    [InlineData("@dataset t.M (a)\n(1)\n$\n(2)\n")]
    [InlineData("@dataset t.M (a)\n$\n")]
    public void UnmarshalReportsWhatTheReferenceReports(string doc)
    {
        var e = Assert.ThrowsAny<PxfException>(() => new Decoder().Unmarshal(doc, new Google.Protobuf.WellKnownTypes.Value()));
        Assert.Equal("1:1: @dataset directive cannot coexist with top-level field entries (draft §3.4.4)", e.Message);
    }

    // The streaming reader yields the rows before the token, then reports it
    // as Parse does; protowire-go's DatasetReader gives the same rows and
    // error for these documents.
    [Theory]
    [InlineData("@dataset t.M (a)\n(1)\n$\n(2)\n", 1, "3:1")]
    [InlineData("@dataset t.M (a)\n$\n", 0, "2:1")]
    public void TheReaderReportsWhatTheReferenceReports(string doc, int rows, string at)
    {
        var reader = DatasetReader.Create(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(doc)));
        for (int i = 0; i < rows; i++) Assert.NotNull(reader.Next());
        var e = Assert.ThrowsAny<PxfException>(() => reader.Next());
        Assert.Equal($"{at}: @dataset directive cannot coexist with top-level field entries; the document's payload is the @dataset rows (draft §3.4.4)", e.Message);
    }
}
