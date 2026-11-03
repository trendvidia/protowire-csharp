// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Xunit;
using FileOptions = Google.Protobuf.Reflection.FileOptions;

namespace Protowire.Pxf.Tests;

/// <summary>
/// Options at the extension numbers protowire retired in v1.12.0 (STABILITY.md
/// promise 3, #76). Mirrors <c>protowire-go/encoding/pxf/retired_test.go</c>.
/// </summary>
public class RetiredNumberTests
{
    /// <summary>The unknown bytes to put on each Options message; null means no options.</summary>
    private sealed record Stale(
        byte[]? File = null, byte[]? Msg = null, byte[]? Field = null, byte[]? Oneof = null,
        byte[]? Enum = null, byte[]? EnumValue = null, byte[]? Service = null, byte[]? Method = null);

    private static byte[] Varint(int number, ulong value) => Write(o =>
    {
        o.WriteTag(number, WireFormat.WireType.Varint);
        o.WriteUInt64(value);
    });

    private static byte[] Bytes(int number, byte[] value) => Write(o =>
    {
        o.WriteTag(number, WireFormat.WireType.LengthDelimited);
        o.WriteBytes(ByteString.CopyFrom(value));
    });

    private static byte[] Write(Action<CodedOutputStream> write)
    {
        using var ms = new MemoryStream();
        using (var o = new CodedOutputStream(ms, leaveOpen: true)) write(o);
        return ms.ToArray();
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static readonly byte[] CarrierBytes = Bytes(50400, []);

    /// <summary>
    /// stale.proto: one message with a oneof member, one enum, optionally one
    /// service, each carrying the given bytes. Each import is an empty file of
    /// that name, since the name is what the gate reads.
    /// </summary>
    private static FileDescriptorProto StaleProto(string[] deps, Stale o)
    {
        var fdp = new FileDescriptorProto
        {
            Name = "stale.proto",
            Package = "stale.v1",
            Syntax = "proto3",
            Dependency = { deps },
            MessageType =
            {
                new DescriptorProto
                {
                    Name = "M",
                    Field =
                    {
                        new FieldDescriptorProto
                        {
                            Name = "x", Number = 1, JsonName = "x", OneofIndex = 0,
                            Label = FieldDescriptorProto.Types.Label.Optional,
                            Type = FieldDescriptorProto.Types.Type.String,
                        },
                    },
                    OneofDecl = { new OneofDescriptorProto { Name = "o" } },
                },
            },
            EnumType =
            {
                new EnumDescriptorProto { Name = "E", Value = { new EnumValueDescriptorProto { Name = "E_ZERO", Number = 0 } } },
            },
        };
        if (o.File != null) fdp.Options = FileOptions.Parser.ParseFrom(o.File);
        if (o.Msg != null) fdp.MessageType[0].Options = MessageOptions.Parser.ParseFrom(o.Msg);
        if (o.Field != null) fdp.MessageType[0].Field[0].Options = FieldOptions.Parser.ParseFrom(o.Field);
        if (o.Oneof != null) fdp.MessageType[0].OneofDecl[0].Options = OneofOptions.Parser.ParseFrom(o.Oneof);
        if (o.Enum != null) fdp.EnumType[0].Options = EnumOptions.Parser.ParseFrom(o.Enum);
        if (o.EnumValue != null) fdp.EnumType[0].Value[0].Options = EnumValueOptions.Parser.ParseFrom(o.EnumValue);
        if (o.Service != null || o.Method != null)
        {
            var method = new MethodDescriptorProto { Name = "Call", InputType = ".stale.v1.M", OutputType = ".stale.v1.M" };
            if (o.Method != null) method.Options = MethodOptions.Parser.ParseFrom(o.Method);
            var service = new ServiceDescriptorProto { Name = "S", Method = { method } };
            if (o.Service != null) service.Options = ServiceOptions.Parser.ParseFrom(o.Service);
            fdp.Service.Add(service);
        }
        return fdp;
    }

    private static IReadOnlyList<FileDescriptor> Build(IEnumerable<FileDescriptorProto> files, ExtensionRegistry? registry = null) =>
        FileDescriptor.BuildFromByteStrings(files.Select(f => f.ToByteString()), registry ?? []);

    private static IEnumerable<FileDescriptorProto> Stubs(string[] deps) =>
        deps.Select(d => new FileDescriptorProto { Name = d });

    private static FileDescriptor StaleFile(string[] deps, Stale o) =>
        Build(Stubs(deps).Append(StaleProto(deps, o))).Last();

    /// <summary>
    /// Every retired number, on the Options kind it was allocated on, in a file
    /// that imports protowire's annotations, is a violation naming the number,
    /// what it was, what it is now, and that the descriptor must be recompiled.
    /// </summary>
    [Fact]
    public void RetiredNumbersAreDiagnosed()
    {
        var fd = StaleFile(["pxf/annotations.proto"], new Stale(
            File: Cat(Varint(50100, 9001), Varint(50101, 0), Bytes(50404, [])),
            Msg: Cat(Varint(50200, 7), CarrierBytes),
            Field: Cat(Varint(50000, 1), Bytes(50001, "42"u8.ToArray()), CarrierBytes, Varint(51000, 1)),
            Oneof: CarrierBytes,
            Enum: CarrierBytes,
            EnumValue: CarrierBytes));

        var vs = Schema.ValidateFile(fd);
        var byNumber = vs.ToDictionary(v => v.Element + "#" + v.Name);
        var want = new Dictionary<string, string>
        {
            ["stale.proto#50100"] = "(sbe.schema_id) before protowire v1.12.0 and is 1319 now",
            ["stale.proto#50101"] = "(sbe.version) before protowire v1.12.0 and is 1320 now",
            ["stale.proto#50404"] = "source_map carrier before protowire v1.12.0 and is 1331 now",
            ["stale.v1.M#50200"] = "(sbe.template_id) before protowire v1.12.0 and is 1321 now",
            ["stale.v1.M#50400"] = "message_annotations carrier before protowire v1.12.0 and is 1327 now",
            ["stale.v1.M.x#50000"] = "(pxf.required) before protowire v1.12.0 and is 1314 now",
            ["stale.v1.M.x#50001"] = "(pxf.default) before protowire v1.12.0 and is 1315 now",
            ["stale.v1.M.x#50400"] = "field_annotations carrier before protowire v1.12.0 and is 1327 now",
            ["stale.v1.M.x#51000"] = "protocheck field constraint before protowire v1.12.0 and is 1347 now",
            ["stale.v1.M.o#50400"] = "oneof_annotations carrier",
            ["stale.v1.E#50400"] = "*_annotations carrier",
            ["stale.v1.E_ZERO#50400"] = "*_annotations carrier",
        };
        foreach (var (key, detail) in want)
        {
            Assert.True(byNumber.TryGetValue(key, out var v), $"expected a violation for {key}; got {string.Join(", ", byNumber.Keys)}");
            Assert.Equal("stale.proto", v!.File);
            Assert.Contains(detail, v.Detail);
            Assert.Contains("must be recompiled", v.Detail);
            Assert.Contains("STABILITY.md promise 3", v.ToString());
        }
        Assert.Equal(want.Count, vs.Count);

        // The rendering, whole, for one of them.
        Assert.Equal(
            "stale.proto: \"stale.v1.M.x\": option number 50000 on FieldOptions was (pxf.required) before protowire v1.12.0 " +
            "and is 1314 now; the descriptor predates the registered extension block and must be recompiled against the " +
            "current pxf/annotations.proto (STABILITY.md promise 3)",
            byNumber["stale.v1.M.x#50000"].ToString());
    }

    /// <summary>
    /// The violation is a bind failure, which is the point: the alternative was a
    /// schema that silently stopped enforcing (pxf.required).
    /// </summary>
    [Fact]
    public void ARetiredNumberRefusesTheBind()
    {
        var fd = StaleFile(["pxf/annotations.proto"], new Stale(Field: Varint(50000, 1)));
        var e = Assert.Throws<PxfException>(() => Schema.EnsureBindable(fd.FindTypeByName<MessageDescriptor>("M")));
        Assert.Contains("50000", e.Message);
    }

    /// <summary>
    /// Services and methods carried only the annotation carrier; the reference
    /// walks them too.
    /// </summary>
    [Fact]
    public void ServicesAndMethodsAreChecked()
    {
        var fd = StaleFile(["sbe/annotations.proto"], new Stale(
            Service: Cat(CarrierBytes, Varint(50000, 1)),
            Method: CarrierBytes));
        var vs = Schema.ValidateFile(fd);
        Assert.Equal(["stale.v1.S#50400", "stale.v1.S.Call#50400"], vs.Select(v => v.Element + "#" + v.Name));
        Assert.Contains("option number 50400 on ServiceOptions was the *_annotations carrier", vs[0].Detail);
        Assert.Contains("option number 50400 on MethodOptions was the *_annotations carrier", vs[1].Detail);
    }

    /// <summary>
    /// The gate: the same bytes in a file importing none of protowire's
    /// annotation files are a third party's options, in the range everybody
    /// squats. Likewise a number in the range that was never allocated, a number
    /// on an Options kind it was not allocated on, and a clean file.
    /// </summary>
    [Theory]
    [InlineData("no protowire import")]
    [InlineData("no import at all")]
    [InlineData("unallocated numbers")]
    [InlineData("registered numbers as unknown bytes")]
    [InlineData("a number on another Options kind")]
    [InlineData("clean")]
    public void RetiredNumbersNeedProtowireImport(string name)
    {
        var stale = new Stale(File: Varint(50100, 9001), Field: Varint(50000, 1), Msg: Bytes(50400, []));
        string[] pxf = ["pxf/annotations.proto"];
        var (deps, opts) = name switch
        {
            "no protowire import" => (["google/protobuf/timestamp.proto"], stale),
            "no import at all" => ([], stale),
            "unallocated numbers" => (pxf, new Stale(Field: Varint(50050, 1), File: Varint(50999, 1))),
            "registered numbers as unknown bytes" => (pxf, new Stale(Field: Varint(1314, 1))),
            "a number on another Options kind" => (pxf, new Stale(Msg: Varint(50000, 1), Enum: Varint(51000, 1), File: Varint(50200, 1))),
            "clean" => (pxf, new Stale()),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        Assert.Empty(Schema.ValidateFile(StaleFile(deps, opts)));
    }

    /// <summary>
    /// The check runs over the import closure like every other bind-time check,
    /// so a stale imported file is diagnosed through the file that imports it.
    /// </summary>
    [Fact]
    public void RetiredNumbersInAnImport()
    {
        string[] deps = ["pxf/annotations.proto"];
        var uses = new FileDescriptorProto
        {
            Name = "uses.proto",
            Package = "uses.v1",
            Syntax = "proto3",
            Dependency = { "stale.proto" },
            MessageType =
            {
                new DescriptorProto
                {
                    Name = "U",
                    Field =
                    {
                        new FieldDescriptorProto
                        {
                            Name = "m", Number = 1, JsonName = "m",
                            Label = FieldDescriptorProto.Types.Label.Optional,
                            Type = FieldDescriptorProto.Types.Type.Message,
                            TypeName = ".stale.v1.M",
                        },
                    },
                },
            },
        };
        var files = Build(Stubs(deps).Append(StaleProto(deps, new Stale(Field: Varint(50000, 1)))).Append(uses));
        var v = Assert.Single(Schema.ValidateDescriptor(files.Last().FindTypeByName<MessageDescriptor>("U")));
        Assert.Equal("stale.proto", v.File);
        Assert.Equal("50000", v.Name);
    }

    /// <summary>
    /// The reference reads only unknown bytes, so an option some imported file
    /// declares as an extension at a retired number — a third party squatting
    /// the range in a schema that also uses protowire — is that extension's
    /// value, not a retired option.
    /// </summary>
    [Fact]
    public void ADeclaredExtensionAtARetiredNumberIsNotRetired()
    {
        var squat = new FileDescriptorProto
        {
            Name = "squat.proto",
            Package = "squat",
            Dependency = { "google/protobuf/descriptor.proto" },
            Extension =
            {
                new FieldDescriptorProto
                {
                    Name = "squat", Number = 50000, JsonName = "squat", Extendee = ".google.protobuf.FieldOptions",
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    Type = FieldDescriptorProto.Types.Type.Bool,
                },
            },
        };
        var ext = new Extension<FieldOptions, bool>(50000, FieldCodec.ForBool(WireFormat.MakeTag(50000, WireFormat.WireType.Varint)));
        string[] deps = ["pxf/annotations.proto", "squat.proto"];
        var stale = StaleProto(deps, new Stale(Field: Varint(50000, 1)));
        var files = Build(
            [FileDescriptorProto.Parser.ParseFrom(DescriptorReflection.Descriptor.SerializedData), new FileDescriptorProto { Name = deps[0] }, squat, stale],
            [ext]);

        var fd = files.Last();
        Assert.True(fd.FindTypeByName<MessageDescriptor>("M").FindFieldByName("x").GetOptions().GetExtension(ext));
        Assert.Empty(Schema.ValidateFile(fd));
    }
}
