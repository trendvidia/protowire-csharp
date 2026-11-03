// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Protowire.Conformance.Schemas.Carrier;
using Protowire.Conformance.Schemas.SettingsAnnotated;
using Protowire.Pxf;

namespace Protowire.Conformance;

/// <summary>
/// The 1327 annotation carrier and the bind-time schema checks. These need no
/// oracle: the carrier's bytes come from the spec's own compiled schema, and
/// each bad schema is built here, because a stock compiler or a second copy of
/// a well-known type could not be generated.
/// </summary>
public class CarrierTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "testdata", "annotations", name);

    /// <summary>
    /// The reader on what a v1.2 compiler actually wrote, which carries more
    /// than a stock compiler's stand-in does: settings_annotated.proto here is
    /// only evidence for what it shares with this.
    /// </summary>
    [Fact]
    public void Reader_TakesTheSpecsCompiledSchema()
    {
        var set = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(Fixture("settings_annotated.binpb")));
        var files = FileDescriptor.BuildFromByteStrings(set.File.Select(f => f.ToByteString()));
        var spec = files.Single(f => f.Package == "settings.annotated.v1").FindTypeByName<MessageDescriptor>("Settings");

        Assert.True(AnnotationsRuntime.IsRequired(spec.FindFieldByName("name")));
        Assert.Equal("3", AnnotationsRuntime.GetDefault(spec.FindFieldByName("retries")));
        Assert.Equal("us-east-1", AnnotationsRuntime.GetDefault(spec.FindFieldByName("region")));
        Assert.Equal("true", AnnotationsRuntime.GetDefault(spec.FindFieldByName("verbose")));
        Assert.False(AnnotationsRuntime.IsRequired(spec.FindFieldByName("region")));
    }

    /// <summary>The stand-in says what the compiled schema says, field by field.</summary>
    [Theory]
    [InlineData("name")]
    [InlineData("retries")]
    [InlineData("region")]
    [InlineData("verbose")]
    public void StandIn_AgreesWithTheCompiledSchema(string field)
    {
        var set = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(Fixture("settings_annotated.binpb")));
        var files = FileDescriptor.BuildFromByteStrings(set.File.Select(f => f.ToByteString()));
        var spec = files.Single(f => f.Package == "settings.annotated.v1").FindTypeByName<MessageDescriptor>("Settings");

        var want = AnnotationsRuntime.Options(spec.FindFieldByName(field));
        var got = AnnotationsRuntime.Options(Settings.Descriptor.FindFieldByName(field));
        Assert.Equal(want.Required, got.Required);
        Assert.Equal(want.Default, got.Default);
    }

    [Fact]
    public void Required_InTheCarrier_IsEnforced()
    {
        var e = Assert.Throws<PxfException>(() => new Decoder().UnmarshalFull("retries = 1\n", new Settings()));
        Assert.Contains("required field \"name\" is absent", e.Message);
    }

    [Fact]
    public void Default_InTheCarrier_IsApplied()
    {
        var got = new Settings();
        new Decoder().UnmarshalFull("name = \"n\"\n", got);
        Assert.Equal(3, got.Retries);
        Assert.Equal("us-east-1", got.Region);
        Assert.True(got.Verbose);
    }

    private static readonly ExtensionRegistry Registry = [AnnotationsExtensions.Required, AnnotationsExtensions.Default];

    private static FieldDescriptorProto StringField(string name, int number, FieldOptions? options = null, int? oneof = null)
    {
        var f = new FieldDescriptorProto
        {
            Name = name,
            Number = number,
            Type = FieldDescriptorProto.Types.Type.String,
            Label = FieldDescriptorProto.Types.Label.Optional,
        };
        if (options != null) f.Options = options;
        if (oneof != null) f.OneofIndex = oneof.Value;
        return f;
    }

    private static FileDescriptor Build(string package, params DescriptorProto[] messages)
    {
        var file = new FileDescriptorProto { Name = "bad.proto", Package = package, Syntax = "proto3" };
        file.MessageType.AddRange(messages);
        return FileDescriptor.BuildFromByteStrings([file.ToByteString()], Registry)[0];
    }

    private static FieldOptions Bracket(bool required = false, string? def = null)
    {
        var o = new FieldOptions();
        if (required) o.SetExtension(AnnotationsExtensions.Required, true);
        if (def != null) o.SetExtension(AnnotationsExtensions.Default, def);
        return o;
    }

    // Options holding the carrier as raw bytes, as a schema with no registered
    // extension arrives.
    private static FieldOptions Carried(string annotation, string? stringArg = null)
    {
        var entry = new Annotation { Name = annotation };
        if (stringArg != null) entry.Args.Add(new AnnotationArg { StringValue = stringArg });
        var list = new AnnotationList { Entries = { entry } };
        using var ms = new MemoryStream();
        using (var output = new CodedOutputStream(ms, leaveOpen: true))
        {
            output.WriteTag(1327, WireFormat.WireType.LengthDelimited);
            output.WriteBytes(list.ToByteString());
        }
        return FieldOptions.Parser.ParseFrom(ms.ToArray());
    }

    private static string Only(FileDescriptor file) => Assert.Single(Schema.ValidateFile(file)).ToString();

    [Fact]
    public void RequiredWithDefault_InTheCarrier_IsRefused()
    {
        var o = Carried("protowire.schema.v1.required");
        o.MergeFrom(Carried("protowire.schema.v1.default", "x"));
        var msg = new DescriptorProto { Name = "M", Field = { StringField("f", 1, o) } };
        Assert.Contains("@required and @default are exclusive", Only(Build("c.v1", msg)));
    }

    [Fact]
    public void TwoSpellingsThatDisagree_AreRefused()
    {
        var o = Bracket(def: "a");
        o.MergeFrom(Carried("protowire.schema.v1.default", "b"));
        var msg = new DescriptorProto { Name = "M", Field = { StringField("f", 1, o) } };
        Assert.Contains("must not be reconciled", Only(Build("c.v2", msg)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    public void ReservedFieldName_IsRefused(string name)
    {
        var msg = new DescriptorProto { Name = "M", Field = { StringField(name, 1) } };
        Assert.Contains("PXF-reserved name", Only(Build($"r.{name}x", msg)));
    }

    [Fact]
    public void ReservedEnumValue_IsRefused()
    {
        var e = new EnumDescriptorProto { Name = "E" };
        e.Value.Add(new EnumValueDescriptorProto { Name = "ZERO", Number = 0 });
        e.Value.Add(new EnumValueDescriptorProto { Name = "null", Number = 1 });
        var msg = new DescriptorProto { Name = "M", EnumType = { e } };
        Assert.Contains("PXF-reserved name", Only(Build("r.e", msg)));
    }

    [Fact]
    public void Default_OnRepeated_IsRefused()
    {
        var f = StringField("f", 1, Bracket(def: "x"));
        f.Label = FieldDescriptorProto.Types.Label.Repeated;
        var msg = new DescriptorProto { Name = "M", Field = { f } };
        Assert.Contains("not valid on repeated fields", Only(Build("d.r", msg)));
    }

    [Fact]
    public void Default_OnPlainMessage_IsRefused()
    {
        var inner = new DescriptorProto { Name = "I" };
        var f = new FieldDescriptorProto
        {
            Name = "f",
            Number = 1,
            Type = FieldDescriptorProto.Types.Type.Message,
            TypeName = ".d.m.I",
            Label = FieldDescriptorProto.Types.Label.Optional,
            Options = Bracket(def: "x"),
        };
        var msg = new DescriptorProto { Name = "M", Field = { f } };
        Assert.Contains("no PXF literal denotes it", Only(Build("d.m", inner, msg)));
    }

    [Fact]
    public void Required_OnAOneofMember_IsRefused()
    {
        var msg = new DescriptorProto
        {
            Name = "M",
            OneofDecl = { new OneofDescriptorProto { Name = "pick" } },
            Field = { StringField("a", 1, Bracket(required: true), oneof: 0), StringField("b", 2, oneof: 0) },
        };
        Assert.Contains("not valid on a member of oneof \"pick\"", Only(Build("o.r", msg)));
    }

    [Fact]
    public void TwoDefaultsInOneOneof_AreRefused()
    {
        var msg = new DescriptorProto
        {
            Name = "M",
            OneofDecl = { new OneofDescriptorProto { Name = "pick" } },
            Field = { StringField("a", 1, Bracket(def: "x"), oneof: 0), StringField("b", 2, Bracket(def: "y"), oneof: 0) },
        };
        var found = Schema.ValidateFile(Build("o.d", msg));
        Assert.Equal(2, found.Count);
        Assert.All(found, v => Assert.Contains("at most one member of oneof", v.ToString()));
    }

    [Fact]
    public void WellKnownName_WithAnotherShape_IsRefused()
    {
        var msg = new DescriptorProto { Name = "Secret", Field = { StringField("hint", 1) } };
        Assert.Contains("redefines a well-known type: it has no field \"value\"", Only(Build("pxf", msg)));
    }
}
