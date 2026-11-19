// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Protowire.Pxf.Tests.AnyReadBack;

namespace Protowire.Pxf.Tests;

// #120: Google.Protobuf 3.36.2 accepts wire type 6 or 7 inside a map entry of
// a generated message, and protobuf-go refuses it, as it refuses those wire
// types on any field (trendvidia/protowire#614). WireCheck reads the bytes by
// the descriptor first, wherever this port hands untrusted bytes to the stock
// parser: an Any's value in the encoder, and check-decode's pb-render and
// pb-text. Where a case says what protobuf-go does, that was run through
// protobuf-go v1.36.12 (proto.Unmarshal, with dynamicpb for the group
// message). The depth cases are about the walk, not about a verdict.
public class WireCheckTests
{
    private const string Reserved = "corrupt field 3 in map entry: proto: cannot parse reserved wire type";

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    private static byte[] Len(byte tag, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var o = new List<byte> { tag };
        int n = body.Length;
        while (n >= 0x80)
        {
            o.Add((byte)(n | 0x80));
            n >>= 7;
        }
        o.Add((byte)n);
        o.AddRange(body);
        return [.. o];
    }

    // Struct { fields { key: k, value: Value { struct_value: inner } } }:
    // a Struct three descents below its parent (entry, Value, Struct).
    private static byte[] StructIn(byte[] inner) =>
        Len(0x0a, Len(0x0a, System.Text.Encoding.UTF8.GetBytes("k")), Len(0x12, Len(0x2a, inner)));

    // protobuf-go refuses each of these.
    [Theory]
    [InlineData("0a011f")] // an entry holding field 3, wire type 7
    [InlineData("0a011e")] // the same with wire type 6
    [InlineData("0a031f0a00")] // the bad field, then a key
    [InlineData("0a030a001f")] // a key, then the bad field
    [InlineData("0a031b1f1c")] // a group in the entry holding wire type 7
    public void ReservedWireTypeInAMapEntry_IsRefused(string hex)
    {
        Assert.Equal(Reserved, WireCheck.Check(Struct.Descriptor, Hex(hex), 100));
    }

    // A map entry inside a message inside a message: Struct, entry, Value,
    // Struct, and the bad entry there. protobuf-go refuses it.
    [Fact]
    public void ReservedWireTypeInANestedMapEntry_IsRefused()
    {
        var bytes = StructIn(Hex("0a011f"));
        Assert.Equal(Reserved, WireCheck.Check(Struct.Descriptor, bytes, 100));
        Assert.Equal(Reserved, WireCheck.Check(Struct.Descriptor, StructIn(bytes), 100));
    }

    // What the stock parser does with the same bytes, so the day an upgrade
    // refuses them is seen here: then the check is no longer what stops them.
    [Theory]
    [InlineData("0a011f")]
    [InlineData("0a011e")]
    [InlineData("0a031f0a00")]
    public void StockParserTakesAReservedWireTypeInAMapEntry(string hex)
    {
        var s = Struct.Parser.ParseFrom(Hex(hex));
        Assert.Single(s.Fields);
        Struct.Parser.ParseFrom(StructIn(Hex(hex)));
    }

    // Only messages are read into. protobuf-go accepts each of these.
    [Theory]
    [InlineData("")]
    [InlineData("0a00")] // an empty entry
    [InlineData("0a090a016b12042a020a00")] // k: a Struct holding an empty entry
    [InlineData("0a0a0a016b12051a030a011f")] // k: the string "\n\x01\x1f", which would not parse as a Struct
    [InlineData("12030a011f")] // unknown field 2, whose bytes would not parse as a Struct
    [InlineData("0a020801")] // an entry's key sent as a varint: skipped as unknown
    public void WhatProtobufAccepts_Passes(string hex)
    {
        Assert.Null(WireCheck.Check(Struct.Descriptor, Hex(hex), 100));
    }

    // Other bytes protobuf-go refuses and Google.Protobuf 3.36.2 takes: a
    // stray end group, a varint whose tenth byte overflows, and a field number
    // past 2^29-1. The walk refuses them as it reads.
    [Fact]
    public void OtherBytesProtobufRefuses_AreRefused()
    {
        Assert.Equal("corrupt field 1: proto: mismatching end group marker",
            WireCheck.Check(Struct.Descriptor, Hex("0c"), 100));
        // The stock parser takes it from a stream, as the encoder and
        // pb-render read; from a byte array it refuses it.
        Struct.Parser.ParseFrom(new CodedInputStream(new MemoryStream(Hex("0c"))));
        Assert.Throws<InvalidProtocolBufferException>(() => Struct.Parser.ParseFrom(Hex("0c")));

        // Value { number_value as a varint whose tenth byte is 0x7f }: field 2
        // of Value is a double, so the varint is skipped as unknown, and the
        // skip refuses it.
        var value = Hex("10ffffffffffffffffff7f");
        Assert.Equal("corrupt field 2: proto: variable length integer overflow",
            WireCheck.Check(Value.Descriptor, value, 100));
        Value.Parser.ParseFrom(value); // the stock parser takes it

        // Field 2^29+1, wire type 0: past protobuf's largest field number.
        Assert.Equal("corrupt tag", WireCheck.Check(Value.Descriptor, Hex("888080801005"), 100));
        Assert.Equal("corrupt tag in map entry", WireCheck.Check(Struct.Descriptor, Hex("0a06888080801005"), 100));
    }

    // The walk counts a descent as Google.Protobuf does, one for each
    // submessage and map entry, and stops at the parser's limit: deeper bytes
    // are the parser's to refuse. Here the bad entry is the fourth descent.
    [Fact]
    public void Depth_IsTheParsersLimit()
    {
        var bytes = StructIn(Hex("0a011f"));
        Assert.Equal(Reserved, WireCheck.Check(Struct.Descriptor, bytes, 4));
        Assert.Null(WireCheck.Check(Struct.Descriptor, bytes, 3));

        // At 3 the parser refuses the same shape with a valid entry, so
        // what the walk leaves unread is refused anyway; at 4 it takes it.
        Assert.Throws<InvalidProtocolBufferException>(() => Parse(StructIn(Hex("0a00")), 3));
        Parse(StructIn(Hex("0a00")), 4);

        static Struct Parse(byte[] b, int limit) =>
            Struct.Parser.ParseFrom(CodedInputStream.CreateWithLimits(new MemoryStream(b), int.MaxValue, limit));
    }

    // The walk keeps its open messages on a stack of its own, not the
    // thread's: 200000 nested Holders, at a limit that lets it go all the
    // way, with field 3, wire type 7, at the bottom.
    [Fact]
    public void DeepInput_DoesNotRecurse()
    {
        Assert.Null(WireCheck.Check(Holder.Descriptor, Nested(200_000, []), int.MaxValue));
        Assert.Equal("corrupt field 3: proto: cannot parse reserved wire type",
            WireCheck.Check(Holder.Descriptor, Nested(200_000, [0x1f]), int.MaxValue));
    }

    // n Holders nested through next (field 2) around inner, built from the
    // inside out in reverse.
    private static byte[] Nested(int n, byte[] inner)
    {
        var rev = new List<byte>(inner.Reverse());
        var len = new List<byte>();
        for (int i = 0; i < n; i++)
        {
            len.Clear();
            int v = rev.Count;
            while (v >= 0x80)
            {
                len.Add((byte)(v | 0x80));
                v >>= 7;
            }
            len.Add((byte)v);
            for (int j = len.Count - 1; j >= 0; j--) rev.Add(len[j]);
            rev.Add(0x12);
        }
        rev.Reverse();
        return [.. rev];
    }

    // A group is read into as a message: a map entry inside it is checked.
    // proto2: message G { optional group Grp = 1 { map<string, string> m = 1; } }
    private static readonly MessageDescriptor G = BuildG();

    private static MessageDescriptor BuildG()
    {
        var entry = new DescriptorProto
        {
            Name = "MEntry",
            Options = new MessageOptions { MapEntry = true },
            Field =
            {
                new FieldDescriptorProto { Name = "key", Number = 1, Label = FieldDescriptorProto.Types.Label.Optional, Type = FieldDescriptorProto.Types.Type.String },
                new FieldDescriptorProto { Name = "value", Number = 2, Label = FieldDescriptorProto.Types.Label.Optional, Type = FieldDescriptorProto.Types.Type.String },
            },
        };
        var grp = new DescriptorProto
        {
            Name = "Grp",
            NestedType = { entry },
            Field =
            {
                new FieldDescriptorProto { Name = "m", Number = 1, Label = FieldDescriptorProto.Types.Label.Repeated, Type = FieldDescriptorProto.Types.Type.Message, TypeName = ".wirecheck.v1.G.Grp.MEntry" },
            },
        };
        var g = new DescriptorProto
        {
            Name = "G",
            NestedType = { grp },
            Field =
            {
                new FieldDescriptorProto { Name = "grp", Number = 1, Label = FieldDescriptorProto.Types.Label.Optional, Type = FieldDescriptorProto.Types.Type.Group, TypeName = ".wirecheck.v1.G.Grp" },
            },
        };
        var file = new FileDescriptorProto { Name = "wirecheck.proto", Package = "wirecheck.v1", Syntax = "proto2", MessageType = { g } };
        return FileDescriptor.BuildFromByteStrings([file.ToByteString()], [])[0].MessageTypes[0];
    }

    [Theory]
    [InlineData("0b0a011f0c", Reserved)] // protobuf-go: refused
    [InlineData("0b0a020a000c", null)] // an entry with an empty key: accepted
    [InlineData("0b0c", null)] // an empty group: accepted
    [InlineData("0b0a00", "corrupt field 1: unexpected EOF")] // no end marker: refused
    [InlineData("0b0a0014", "corrupt field 2: proto: mismatching end group marker")] // the wrong end marker: refused
    public void GroupIsReadAsAMessage(string hex, string? want)
    {
        Assert.Equal(want, WireCheck.Check(G, Hex(hex), 100));
    }

    // The encoder's Any: the value is checked before the stock parser reads
    // it, so a value holding a reserved wire type does not unpack.
    [Theory]
    [InlineData("0a011f")]
    [InlineData("0a011e")]
    [InlineData("0a031f0a00")]
    public void AnyValueWithAReservedWireType_DoesNotUnpack(string hex)
    {
        Assert.Null(Encoder.UnpackAny(Struct.Descriptor, ByteString.CopyFrom(Hex(hex)), 100));
        Assert.NotNull(Encoder.UnpackAny(Struct.Descriptor, ByteString.CopyFrom(Hex("0a00")), 100));
    }

    // What a caller sees: the Any keeps its own fields. This held before #120
    // too, because a body is written only when it reads back as the same
    // bytes, and no writer produces a reserved wire type.
    [Fact]
    public void AnyValueWithAReservedWireType_StaysRaw()
    {
        var registry = TypeRegistry.FromFiles(StructReflection.Descriptor);
        var m = new Holder { Payload = new Any { TypeUrl = "type.googleapis.com/google.protobuf.Struct", Value = ByteString.CopyFrom(Hex("0a011f")) } };
        string doc = new Encoder { Registry = registry }.Marshal(m);
        Assert.DoesNotContain("@type", doc);
        Assert.Contains("value = b\"CgEf\"", doc);
    }
}
