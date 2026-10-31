// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf.Tests.Bench;

namespace Protowire.Pxf.Tests;

// HARDENING.md § Recursion. The root message is depth 0 and every `{` or `[`
// is one descent: a document exactly MaxNestingDepth (100) deep is accepted
// and one deeper is rejected. The documents are the shapes of protowire's
// corpus rows pxf/deep-nesting-100 / -101 and pxf/deep-nesting-lists-100 /
// -101, whose generator is testdata/adversarial/generate.py.
public class NestingDepthTests
{
    public class Tree
    {
        public Tree? Child { get; set; }
        public string Label { get; set; } = "";
        public List<Tree> Children { get; set; } = new();
        public Google.Protobuf.WellKnownTypes.Any? Details { get; set; }
    }

    private static string Blocks(int n) =>
        string.Concat(Enumerable.Repeat("child{", n)) + new string('}', n);

    // `pairs` levels of `children = [{ … }]`, a list and a block per pair,
    // around `inner`.
    private static string Lists(int pairs, string inner = "") =>
        string.Concat(Enumerable.Repeat("children = [{", pairs)) + inner +
        string.Concat(Enumerable.Repeat("}]", pairs));

    private static void AssertAccepted(string doc) =>
        new Decoder().Unmarshal(doc, new Tree());

    private static void AssertRejected(string doc)
    {
        var e = Assert.Throws<PxfException>(() => new Decoder().Unmarshal(doc, new Tree()));
        Assert.Contains("nesting depth exceeds 100", e.Message);
    }

    [Fact]
    public void Blocks_AtTheBound_Accepted()
    {
        var doc = Blocks(100);
        var got = new Tree();
        new Decoder().Unmarshal(doc, got);

        int depth = 0;
        for (var t = got.Child; t != null; t = t.Child) depth++;
        Assert.Equal(100, depth);
    }

    [Fact]
    public void Blocks_OnePast_Rejected() => AssertRejected(Blocks(101));

    [Fact]
    public void Lists_AtTheBound_Accepted() => AssertAccepted(Lists(50));

    [Fact]
    public void Lists_OnePastByABlock_Rejected() => AssertRejected(Lists(50, "child{}"));

    [Fact]
    public void Lists_OnePastByAList_Rejected() =>
        AssertRejected(Lists(49, "child{child{children = []}}"));

    [Fact]
    public void Lists_AtTheBoundEndingInAList_Accepted() =>
        AssertAccepted(Lists(49, "child{children = []}"));

    [Fact]
    public void ListSiblings_DoNotAccumulate() =>
        AssertAccepted(string.Concat(Enumerable.Repeat("children = [{}, {}]\n", 200)));

    [Fact]
    public void SkippedBlock_AtTheBound_Accepted() => AssertAccepted("nope " + Skipped(99));

    [Fact]
    public void SkippedBlock_OnePast_Rejected() => AssertRejected("nope " + Skipped(100));

    [Fact]
    public void SkippedList_AtTheBound_Accepted() =>
        AssertAccepted("nope = " + new string('[', 100) + new string(']', 100));

    [Fact]
    public void SkippedList_OnePast_Rejected() =>
        AssertRejected("nope = " + new string('[', 101) + new string(']', 101));

    [Fact]
    public void SkippedValue_CountsFromTheEnclosingBody()
    {
        // 50 blocks, then a skipped value 50 deep: 100 descents.
        string Wrap(int skipped) =>
            string.Concat(Enumerable.Repeat("child{", 50)) +
            "nope " + Skipped(skipped - 1) + new string('}', 50);

        AssertAccepted(Wrap(50));
        AssertRejected(Wrap(51));
    }

    // n + 1 braces: the `{` opening the value and n inside it.
    private static string Skipped(int n) =>
        "{" + string.Concat(Enumerable.Repeat("a {", n)) + new string('}', n + 1);

    [Fact]
    public void AnyBody_SharesItsBlocksDescent()
    {
        const string body = "details { @type type.googleapis.com/bench.v1.Endpoint\npath = \"/api\" }";
        string Nested(int blocks) =>
            string.Concat(Enumerable.Repeat("child{", blocks)) + body + new string('}', blocks);

        var dec = new Decoder
        {
            Registry = Google.Protobuf.Reflection.TypeRegistry.FromFiles(BenchTestReflection.Descriptor),
        };

        var got = new Tree();
        dec.Unmarshal(Nested(99), got);
        for (int i = 0; i < 99; i++) got = got.Child!;
        Assert.Equal("/api", got.Details!.Unpack<Endpoint>().Path);

        var e = Assert.Throws<PxfException>(() => dec.Unmarshal(Nested(100), new Tree()));
        Assert.Contains("nesting depth exceeds 100", e.Message);
    }

    // The text parser and the decoder agree at the edge.
    [Fact]
    public void Parser_AgreesWithTheDecoder()
    {
        Parser.Parse(Blocks(100));
        Parser.Parse(Lists(50));
        Assert.Contains("nesting depth", Assert.Throws<PxfException>(() => Parser.Parse(Blocks(101))).Message);
        Assert.Contains("nesting depth", Assert.Throws<PxfException>(() => Parser.Parse(Lists(50, "child{}"))).Message);
    }

    [Fact]
    public void Parser_DirectiveBodyIsOneDescent()
    {
        string Directive(int inner) => "@header {" + Blocks(inner) + "}";

        Parser.Parse(Directive(99));
        Assert.Contains("nesting depth", Assert.Throws<PxfException>(() => Parser.Parse(Directive(100))).Message);
    }
}
