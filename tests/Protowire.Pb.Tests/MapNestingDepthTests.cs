// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;

namespace Protowire.Pb.Tests;

// HARDENING.md § Recursion: a PB map entry is a descent whatever its value's
// type. The documents are the shapes of protowire's corpus rows
// pb/deep-map-100 / -101 and pb/deep-scalar-map-100 / -101.
public class MapNestingDepthTests
{
    public class Node
    {
        [Protowire(1)] public Node? Child { get; set; }
        [Protowire(2)] public Dictionary<string, Node> Branches { get; set; } = new();
        [Protowire(3)] public Dictionary<string, string> Tags { get; set; } = new();
    }

    private static byte[] Delimited(int field, byte[] payload)
    {
        using var ms = new MemoryStream();
        using var output = new CodedOutputStream(ms);
        output.WriteTag(field, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(payload));
        output.Flush();
        return ms.ToArray();
    }

    private static byte[] Wrap(int submessages, byte[] data)
    {
        for (int i = 0; i < submessages; i++) data = Delimited(1, data);
        return data;
    }

    // n levels of map entry + value submessage: 2n descents.
    private static byte[] Entries(int n, byte[] inner)
    {
        byte[] key = Delimited(1, "k"u8.ToArray());
        for (int i = 0; i < n; i++) inner = Delimited(2, [.. key, .. Delimited(2, inner)]);
        return inner;
    }

    // A map entry holding k = "k" and v = "v".
    private static byte[] ScalarEntry() =>
        Delimited(3, [.. Delimited(1, "k"u8.ToArray()), .. Delimited(2, "v"u8.ToArray())]);

    private static void AssertRejected(byte[] doc)
    {
        var e = Assert.Throws<InvalidOperationException>(() => Pb.Unmarshal(doc, new Node()));
        Assert.Contains("nesting depth exceeds 100", e.Message);
    }

    [Fact]
    public void MessageValuedEntries_AtTheBound_Accepted()
    {
        var got = new Node();
        Pb.Unmarshal(Entries(50, []), got);

        int depth = 0;
        for (var n = got; n.Branches.Count > 0; n = n.Branches["k"]) depth++;
        Assert.Equal(50, depth);
    }

    [Fact]
    public void MessageValuedEntries_OnePast_Rejected() => AssertRejected(Entries(50, Wrap(1, [])));

    [Fact]
    public void ScalarValuedEntry_AtTheBound_Accepted()
    {
        var got = new Node();
        Pb.Unmarshal(Wrap(99, ScalarEntry()), got);

        for (int i = 0; i < 99; i++) got = got.Child!;
        Assert.Equal("v", got.Tags["k"]);
    }

    [Fact]
    public void ScalarValuedEntry_OnePast_Rejected() => AssertRejected(Wrap(100, ScalarEntry()));
}
