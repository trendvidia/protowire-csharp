// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf;

namespace Protowire.Pxf.Tests;

// HARDENING.md § Recursion, trendvidia/protowire#502 (#80 here): the PXF
// producer refuses a hand-written object nested past MaxNestingDepth, counted
// as the decoder counts the text it writes (a block, a list and a map are
// each one descent), as it already did for a generated message.
public class ProducerNestingDepthTests
{
    public class Tree
    {
        public Tree? Child { get; set; }
        public List<Tree> Children { get; set; } = new();
        public Dictionary<string, Tree> Branches { get; set; } = new();
    }

    private static Tree Chain(int n)
    {
        var root = new Tree();
        var at = root;
        for (int i = 0; i < n; i++) at = at.Child = new Tree();
        return root;
    }

    // n levels of a one-element list: 2n descents (the list and the block).
    private static Tree Lists(int n)
    {
        var root = new Tree();
        var at = root;
        for (int i = 0; i < n; i++)
        {
            var next = new Tree();
            at.Children.Add(next);
            at = next;
        }
        return root;
    }

    // n levels of a one-entry map with a message value: 2n descents.
    private static Tree Maps(int n)
    {
        var root = new Tree();
        var at = root;
        for (int i = 0; i < n; i++)
        {
            var next = new Tree();
            at.Branches["k"] = next;
            at = next;
        }
        return root;
    }

    private static Tree Deepest(Tree t)
    {
        while (true)
        {
            if (t.Child != null) t = t.Child;
            else if (t.Children.Count > 0) t = t.Children[0];
            else if (t.Branches.Count > 0) t = t.Branches["k"];
            else return t;
        }
    }

    // What the encoder writes, the decoder at the same limit reads.
    private static void AssertWrittenAndRead(Tree t, int limit = 100)
    {
        var text = new Encoder { MaxNestingDepth = limit }.Marshal(t);
        new Decoder { Limits = PxfLimits.Default with { MaxNestingDepth = limit } }.Unmarshal(text, new Tree());
    }

    private static void AssertRefused(Tree t, int limit = 100)
    {
        var e = Assert.ThrowsAny<PxfException>(() => new Encoder { MaxNestingDepth = limit }.Marshal(t));
        Assert.Contains($"nesting depth exceeds MaxNestingDepth={limit}", e.Message);
    }

    [Fact]
    public void Blocks_AtTheBound_Written() => AssertWrittenAndRead(Chain(100));

    [Fact]
    public void Blocks_OnePast_Refused() => AssertRefused(Chain(101));

    [Fact]
    public void Lists_AtTheBound_Written() => AssertWrittenAndRead(Lists(50));

    [Fact]
    public void Lists_OnePast_Refused()
    {
        var t = Lists(50);
        Deepest(t).Child = new Tree();
        AssertRefused(t);
    }

    [Fact]
    public void Maps_AtTheBound_Written() => AssertWrittenAndRead(Maps(50));

    [Fact]
    public void Maps_OnePast_Refused()
    {
        var t = Maps(50);
        Deepest(t).Child = new Tree();
        AssertRefused(t);
    }

    [Fact]
    public void TheLimitIsPerCall()
    {
        AssertWrittenAndRead(Chain(101), limit: 101);
        AssertRefused(Chain(11), limit: 10);
    }

    [Fact]
    public void ACycleIsRefusedNotOverflowed()
    {
        var a = new Tree();
        a.Child = a;
        AssertRefused(a);
    }
}
