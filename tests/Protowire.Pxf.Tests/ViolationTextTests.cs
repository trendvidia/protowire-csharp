// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Xunit;

namespace Protowire.Pxf.Tests;

/// <summary>
/// A violation names its element and prints as protowire-go's does (#88). The
/// expected strings were printed by protowire-go f8a01b1's ValidateFile for
/// the same two files, compiled with protocompile:
/// <code>
/// package r.v1;
/// message M { int32 null = 1; oneof true { int32 a = 2; } enum Inner { INNER_ZERO = 0; false = 1; } }
/// enum Top { TOP_ZERO = 0; null = 1; }
/// </code>
/// and a google/protobuf/timestamp.proto declaring Timestamp with only seconds.
/// </summary>
public class ViolationTextTests
{
    private static FieldDescriptorProto Int32(string name, int number, int? oneof = null)
    {
        var f = new FieldDescriptorProto
        {
            Name = name, Number = number, JsonName = name,
            Label = FieldDescriptorProto.Types.Label.Optional,
            Type = FieldDescriptorProto.Types.Type.Int32,
        };
        if (oneof is int i) f.OneofIndex = i;
        return f;
    }

    private static EnumDescriptorProto Enum(string name, string zero, string reserved) => new()
    {
        Name = name,
        Value =
        {
            new EnumValueDescriptorProto { Name = zero, Number = 0 },
            new EnumValueDescriptorProto { Name = reserved, Number = 1 },
        },
    };

    private static FileDescriptor Build(FileDescriptorProto fdp) =>
        FileDescriptor.BuildFromByteStrings([fdp.ToByteString()], [])[0];

    [Fact]
    public void ReservedNamesPrintAsTheReferenceDoes()
    {
        var file = Build(new FileDescriptorProto
        {
            Name = "r.proto",
            Package = "r.v1",
            Syntax = "proto3",
            MessageType =
            {
                new DescriptorProto
                {
                    Name = "M",
                    Field = { Int32("null", 1), Int32("a", 2, oneof: 0) },
                    OneofDecl = { new OneofDescriptorProto { Name = "true" } },
                    EnumType = { Enum("Inner", "INNER_ZERO", "false") },
                },
            },
            EnumType = { Enum("Top", "TOP_ZERO", "null") },
        });

        var got = Schema.ValidateFile(file);
        Assert.Equal(
            ["r.v1.M.false", "r.v1.M.null", "r.v1.M.true", "r.v1.null"],
            got.Select(v => v.Element));
        Assert.Equal(
            [
                "r.proto: enum value \"r.v1.M.false\" uses PXF-reserved name \"false\" (draft §3.13)",
                "r.proto: message field \"r.v1.M.null\" uses PXF-reserved name \"null\" (draft §3.13)",
                "r.proto: oneof \"r.v1.M.true\" uses PXF-reserved name \"true\" (draft §3.13)",
                "r.proto: enum value \"r.v1.null\" uses PXF-reserved name \"null\" (draft §3.13)",
            ],
            got.Select(v => v.ToString()));
    }

    [Fact]
    public void AWellKnownShapePrintsAsTheReferenceDoes()
    {
        var file = Build(new FileDescriptorProto
        {
            Name = "google/protobuf/timestamp.proto",
            Package = "google.protobuf",
            Syntax = "proto3",
            MessageType =
            {
                new DescriptorProto
                {
                    Name = "Timestamp",
                    Field =
                    {
                        new FieldDescriptorProto
                        {
                            Name = "seconds", Number = 1, JsonName = "seconds",
                            Label = FieldDescriptorProto.Types.Label.Optional,
                            Type = FieldDescriptorProto.Types.Type.Int64,
                        },
                    },
                },
            },
        });

        var v = Assert.Single(Schema.ValidateFile(file));
        Assert.Equal("google.protobuf.Timestamp", v.Element);
        Assert.Equal(
            "google/protobuf/timestamp.proto: message \"google.protobuf.Timestamp\" redefines a well-known type: it has no field \"nanos\" (draft -01 §wkt-shapes)",
            v.ToString());
    }
}
