// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pxf.Tests;

// HARDENING.md § Recursion: a map's `{` is a descent whatever its values are.
// The documents are the shapes of protowire's corpus rows
// pxf/deep-nesting-maps-100 / -101 and pxf/deep-nesting-scalar-map-100 / -101.
public class MapNestingDepthTests
{
    public class Tree
    {
        public Tree? Child { get; set; }
        public Dictionary<string, Tree> Branches { get; set; } = new();
        public Dictionary<string, string> Tags { get; set; } = new();
    }

    // `pairs` levels of `branches = {k: { … }}`, two descents each, around
    // `blocks` more at the deepest point.
    private static string Maps(int pairs, int blocks = 0) =>
        string.Concat(Enumerable.Repeat("branches = {k: {", pairs)) +
        string.Concat(Enumerable.Repeat("child{", blocks)) + new string('}', blocks) +
        string.Concat(Enumerable.Repeat("}}", pairs));

    private static string ScalarMap(int blocks) =>
        string.Concat(Enumerable.Repeat("child{", blocks)) + "tags = {k: \"v\"}" + new string('}', blocks);

    private static void AssertRejected(string doc)
    {
        var e = Assert.Throws<PxfException>(() => new Decoder().Unmarshal(doc, new Tree()));
        Assert.Contains("nesting depth exceeds 100", e.Message);
    }

    [Fact]
    public void Maps_AtTheBound_Accepted()
    {
        var got = new Tree();
        new Decoder().Unmarshal(Maps(50), got);

        int depth = 0;
        for (var t = got; t.Branches.TryGetValue("k", out var next); t = next) depth++;
        Assert.Equal(50, depth);
    }

    [Fact]
    public void Maps_OnePast_Rejected() => AssertRejected(Maps(50, 1));

    [Fact]
    public void ScalarMap_AtTheBound_Accepted()
    {
        var got = new Tree();
        new Decoder().Unmarshal(ScalarMap(99), got);

        for (int i = 0; i < 99; i++) got = got.Child!;
        Assert.Equal("v", got.Tags["k"]);
    }

    [Fact]
    public void ScalarMap_OnePast_Rejected() => AssertRejected(ScalarMap(100));

    [Fact]
    public void SiblingMaps_DoNotAccumulate() =>
        new Decoder().Unmarshal(string.Concat(Enumerable.Repeat("tags = {k: \"v\"}\n", 200)), new Tree());

    // The text parser and the decoder agree at the edge.
    [Fact]
    public void Parser_AgreesWithTheDecoder()
    {
        Parser.Parse(Maps(50));
        Parser.Parse(ScalarMap(99));
        Assert.Contains("nesting depth", Assert.Throws<PxfException>(() => Parser.Parse(Maps(50, 1))).Message);
        Assert.Contains("nesting depth", Assert.Throws<PxfException>(() => Parser.Parse(ScalarMap(100))).Message);
    }
}
