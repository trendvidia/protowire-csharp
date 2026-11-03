// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Protowire.Pxf.Tests.AnyReadBack;

namespace Protowire.Pxf.Tests;

// trendvidia/protowire#501, draft -01 "Any Blocks" (#81): the encoder writes
// an Any as `@type` and a body only when reading that body gives back the
// Any's value, byte for byte. Anything else stays as the Any's own fields,
// type_url and value. A reader compares an Any by its bytes, so a body that
// drops or reorders them would change the message.
//
// Ported from protowire-go encoding/pxf/any_body_readback_test.go and
// marshal_depth_test.go; each case says which expectation it carries.
public class AnyReadBackTests
{
    private static readonly TypeRegistry Registry = TypeRegistry.FromFiles(PxfAnyReadbackTestReflection.Descriptor);

    // ---- wire helpers: hand-built bytes, so a case can hold what a message
    // object would never serialize (unknown fields, order, explicit zeros).

    private static byte[] Varint(ulong v)
    {
        var o = new List<byte>();
        while (v >= 0x80)
        {
            o.Add((byte)(v | 0x80));
            v >>= 7;
        }
        o.Add((byte)v);
        return [.. o];
    }

    private static byte[] VarintField(int num, ulong v) => Cat(Varint((ulong)num << 3), Varint(v));

    private static byte[] LenField(int num, byte[] body) => Cat(Varint(((ulong)num << 3) | 2), Varint((ulong)body.Length), body);

    private static byte[] Str(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static Holder AnyHolder(string typeName, byte[] value) =>
        new() { Payload = new Any { TypeUrl = typeName, Value = ByteString.CopyFrom(value) } };

    /// <summary>
    /// Marshals <paramref name="m"/>, reads the document back with Unmarshal
    /// and UnmarshalFull, and asserts both give the same message, which for
    /// an Any means the same type_url and the same value bytes. Returns the
    /// document.
    /// </summary>
    private static string AssertReadsBack<TMsg>(TMsg m, Encoder? enc = null, Decoder? dec = null)
        where TMsg : IMessage<TMsg>, new()
    {
        string doc = (enc ?? new Encoder { Registry = Registry }).Marshal(m);
        dec ??= new Decoder { Registry = Registry };

        var back = new TMsg();
        dec.Unmarshal(doc, back);
        Assert.True(m.Equals(back), "Unmarshal changed the message:\n" + Head(doc));

        var full = new TMsg();
        dec.UnmarshalFull(doc, full);
        Assert.True(m.Equals(full), "UnmarshalFull changed the message:\n" + Head(doc));
        return doc;
    }

    private static string Head(string doc) => doc.Length > 2000 ? doc[..2000] : doc;

    private static readonly byte[] TagT = LenField(1, Str("t"));
    private static readonly byte[] N1 = VarintField(2, 1);
    private static readonly byte[] Unknown = VarintField(101, 1);

    private static byte[] Entry(string k, ulong v) => LenField(4, Cat(LenField(1, Str(k)), VarintField(2, v)));

    // any_body_readback_test.go:134-181, case for case.
    public static TheoryData<string, string, byte[], bool> Cases => new()
    {
        // What a body can say keeps the body form.
        { "canonical", "anyback.v1.Leaf", Cat(TagT, N1), true },
        { "empty", "anyback.v1.Leaf", [], true },
        { "timestamp", "anyback.v1.Rich", LenField(1, VarintField(1, 1700000000)), true },
        { "wrapper", "anyback.v1.Rich", LenField(2, VarintField(1, 7)), true },
        { "wrapper holding zero", "anyback.v1.Rich", LenField(2, []), true },
        { "sorted map", "anyback.v1.Rich", Cat(Entry("a", 1), Entry("b", 2)), true },
        { "packed list", "anyback.v1.Rich", LenField(5, [1, 2]), true },
        { "optional zero", "anyback.v1.Rich", VarintField(6, 0), true },
        { "default field set", "anyback.v1.Defaulted", LenField(1, Str("x")), true },
        { "required field set", "anyback.v1.Required", VarintField(1, 3), true },

        // Unknown fields, and fields out of order.
        { "unknown field", "anyback.v1.Leaf", Cat(TagT, Unknown), false },
        { "fields out of order", "anyback.v1.Leaf", Cat(N1, TagT), false },

        // The same loss one level down, where re-encoding the parsed value
        // gives the same bytes.
        { "unknown in submessage", "anyback.v1.Rich", LenField(3, Cat(TagT, Unknown)), false },
        { "unknown in list element", "anyback.v1.Rich", LenField(8, Cat(TagT, Unknown)), false },
        { "unknown in wrapper", "anyback.v1.Rich", LenField(2, Cat(VarintField(1, 7), Unknown)), false },
        { "unknown in timestamp", "anyback.v1.Rich", LenField(1, Cat(VarintField(1, 5), Unknown)), false },

        // Bytes a decoder would not write back.
        { "explicit zero", "anyback.v1.Leaf", VarintField(2, 0), false },
        { "long varint", "anyback.v1.Leaf", [0x10, 0x81, 0x00], false },
        { "unpacked list", "anyback.v1.Rich", Cat(VarintField(5, 1), VarintField(5, 2)), false },
        { "map out of order", "anyback.v1.Rich", Cat(Entry("b", 2), Entry("a", 1)), false },
        { "repeated singular field", "anyback.v1.Leaf", Cat(VarintField(2, 1), VarintField(2, 2)), false },

        // A literal cannot hold it.
        { "timestamp past year 9999", "anyback.v1.Rich", LenField(1, VarintField(1, 1UL << 40)), false },

        // A reader's annotation rules would change or refuse the body.
        { "default unset", "anyback.v1.Defaulted", VarintField(2, 1), false },
        { "required unset", "anyback.v1.Required", LenField(2, Str("s")), false },

        // Never unpacked at all.
        { "unresolved", "anyback.v1.Nope", TagT, false },
        { "unparsable", "anyback.v1.Leaf", [0xff], false },
        // A map entry whose key field has the wrong wire type (in Go it
        // panicked proto.Unmarshal).
        { "map key with the wrong wire type", "anyback.v1.Rich", LenField(4, Cat(LenField(1, Str("a")), VarintField(1, 5))), false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void WrittenOnlyWhenItReadsBack(string name, string typeName, byte[] value, bool body)
    {
        var m = AnyHolder(typeName, value);
        string doc = AssertReadsBack(m);

        var back = new Holder();
        new Decoder { Registry = Registry }.Unmarshal(doc, back);
        Assert.Equal(value, back.Payload.Value.ToByteArray());
        Assert.True(body == doc.Contains("@type"), $"{name}: want body={body}, got:\n{Head(doc)}");
        if (!body)
        {
            Assert.Contains("type_url", doc);
        }
    }

    // A message packed the ordinary way reads back as a body, by type URL.
    [Fact]
    public void PackedMessage_IsWrittenAsABody()
    {
        var m = new Holder { Payload = Any.Pack(new Rich { Leaf = new Leaf { Tag = "t", N = 3 }, R = { 1, 2 } }) };
        string doc = AssertReadsBack(m);
        Assert.Contains("@type = \"type.googleapis.com/anyback.v1.Rich\"", doc);
        Assert.DoesNotContain("type_url", doc);
    }

    // holderChain nests n Anys, each holding a Holder, around leaf bytes.
    private static Holder HolderChain(int n)
    {
        var m = AnyHolder("anyback.v1.Leaf", LenField(1, Str("t")));
        for (int i = 0; i < n - 1; i++)
        {
            m = AnyHolder("anyback.v1.Holder", m.ToByteArray());
        }
        return m;
    }

    private static int Count(string doc, string what)
    {
        int n = 0;
        for (int i = doc.IndexOf(what, StringComparison.Ordinal); i >= 0; i = doc.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // any_body_readback_test.go:204. A chain of Anys costs its own bytes, not
    // MaxNestingDepth, on the wire: past the limit it comes out raw rather
    // than as a document no reader accepts. The 2000 row stands in for
    // TestAnyBody_ChainPastTheLimitIsNotWalked, whose lookup counter has no
    // C# equivalent (TypeRegistry is sealed).
    [Theory]
    [InlineData(1, 1)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    [InlineData(101, 99)] // the 100th Any's body needs the 101st's raw block
    [InlineData(300, 99)]
    [InlineData(2000, 99)]
    public void ChainStopsAtTheDepthLimit(int n, int bodies)
    {
        string doc = AssertReadsBack(HolderChain(n));
        Assert.Equal(bodies, Count(doc, "@type"));
    }

    // any_body_readback_test.go:247. A body is measured where it lands: the
    // same value that fits near the root does not fit near the limit, and
    // only the Any there stays raw.
    [Theory]
    [InlineData(0, true)]
    [InlineData(98, true)]
    [InlineData(99, false)]
    public void SubmessageDepthCounts(int levels, bool body)
    {
        var m = AnyHolder("anyback.v1.Rich", LenField(3, LenField(1, Str("t"))));
        for (int i = 0; i < levels; i++) m = new Holder { Next = m };
        string doc = AssertReadsBack(m);
        Assert.Equal(body, doc.Contains("@type"));
    }

    // any_body_readback_test.go:274. EmitDefaults writes an unset field out,
    // which sets it for a reader, so a body that has one stays raw.
    [Fact]
    public void EmitDefaults_ReadsBack()
    {
        var m = AnyHolder("anyback.v1.Rich", LenField(3, []));
        string doc = new Encoder { Registry = Registry, EmitDefaults = true }.Marshal(m);
        var back = new Holder();
        new Decoder { Registry = Registry }.Unmarshal(doc, back);
        Assert.True(m.Equals(back), doc);
    }

    // deepT wraps m in n singular fields.
    private static T DeepT(T m, int n)
    {
        for (int i = 0; i < n; i++) m = new T { Next = m };
        return m;
    }

    private static T TAny(T value) =>
        new() { Payload = new Any { TypeUrl = "anyback.v1.T", Value = value.ToByteString() } };

    // marshal_depth_test.go:171. An Any is not refused for what its bytes
    // hold: when a body would be too deep, the Any stays raw.
    [Fact]
    public void AnyTooDeep_StaysRaw()
    {
        var m = TAny(DeepT(new T(), 150));
        string doc = AssertReadsBack(m);
        Assert.DoesNotContain("@type", doc);
    }

    // marshal_depth_test.go:192. An Any that sits past the limit itself has
    // no room even for its raw block.
    [Fact]
    public void AnyPastTheLimit_IsRefused()
    {
        var m = DeepT(TAny(new T { V = 1 }), PxfLimits.Default.MaxNestingDepth);
        var e = Assert.ThrowsAny<PxfException>(() => new Encoder { Registry = Registry }.Marshal(m));
        Assert.Contains("nesting depth exceeds MaxNestingDepth=100", e.Message);
    }
}
