// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pb.Tests;

// HARDENING.md § Recursion, trendvidia/protowire#502 (#80 here): a producer
// refuses a value nested past MaxNestingDepth, counted as a decoder reading
// its output counts it, and the limit is configurable per call.
public class ProducerNestingDepthTests
{
    public class Node
    {
        [Protowire(1)] public Node? Child { get; set; }
        [Protowire(2)] public Dictionary<string, Node> Branches { get; set; } = new();
        [Protowire(3)] public Dictionary<string, string> Tags { get; set; } = new();
    }

    // n submessages under the root: n descents.
    private static Node Chain(int n)
    {
        var root = new Node();
        var at = root;
        for (int i = 0; i < n; i++) at = at.Child = new Node();
        return root;
    }

    // n map entries, each with a message value: 2n descents. tail hangs under
    // the deepest value.
    private static Node Entries(int n, Node? tail = null)
    {
        var root = new Node();
        var at = root;
        for (int i = 0; i < n; i++)
        {
            var next = new Node();
            at.Branches["k"] = next;
            at = next;
        }
        at.Child = tail;
        return root;
    }

    private static Node Leaf(int depth, Action<Node> set)
    {
        var root = Chain(depth);
        var at = root;
        while (at.Child != null) at = at.Child;
        set(at);
        return root;
    }

    private static void AssertRefused(Node v, PbLimits? limits = null)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Pb.Marshal(v, limits ?? PbLimits.Default));
        Assert.Contains($"nesting depth exceeds MaxNestingDepth={(limits ?? PbLimits.Default).MaxNestingDepth}", e.Message);
    }

    [Fact]
    public void Submessages_AtTheBound_Written()
    {
        var bytes = Pb.Marshal(Chain(100));
        Pb.Unmarshal(bytes, new Node());
    }

    [Fact]
    public void Submessages_OnePast_Refused() => AssertRefused(Chain(101));

    [Fact]
    public void MessageValuedEntries_AtTheBound_Written()
    {
        var bytes = Pb.Marshal(Entries(50));
        Pb.Unmarshal(bytes, new Node());
    }

    [Fact]
    public void MessageValuedEntries_OnePast_Refused() => AssertRefused(Entries(50, new Node()));

    // A map entry is a descent whatever its value's type: a scalar map at
    // depth 99 puts its entries at 100, at depth 100 at 101.
    [Fact]
    public void ScalarValuedEntry_AtTheBound_Written()
    {
        var bytes = Pb.Marshal(Leaf(99, n => n.Tags["k"] = "v"));
        Pb.Unmarshal(bytes, new Node());
    }

    [Fact]
    public void ScalarValuedEntry_OnePast_Refused() => AssertRefused(Leaf(100, n => n.Tags["k"] = "v"));

    [Fact]
    public void TheLimitIsPerCall()
    {
        var limits = PbLimits.Default with { MaxNestingDepth = 101 };
        var bytes = Pb.Marshal(Chain(101), limits);
        // A reader at the default refuses it; one at the raised limit reads it.
        Assert.Throws<InvalidOperationException>(() => Pb.Unmarshal(bytes, new Node()));
        Pb.Unmarshal(bytes, new Node(), limits);

        AssertRefused(Chain(11), PbLimits.Default with { MaxNestingDepth = 10 });
        Pb.Marshal(Chain(10), PbLimits.Default with { MaxNestingDepth = 10 });
    }

    [Fact]
    public void ACycleIsRefusedNotOverflowed()
    {
        var a = new Node();
        a.Child = a;
        AssertRefused(a);
    }
}
