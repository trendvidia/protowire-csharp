// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Protowire.Pxf.Tests.KeyedFixtures;
using Protowire.Pxf.Tests.Presence;

namespace Protowire.Pxf.Tests;

/// <summary>Keyed-repeated-field behaviour the shared fixtures do not pin.</summary>
public class KeyedTests
{
    private static Node DecodeNode(string doc)
    {
        var node = new Node();
        new Decoder().Unmarshal(doc, node);
        return node;
    }

    private static Node NodeOf(params string[] keys)
    {
        var root = new Node();
        root.Children.Add(keys.Select(k => new Node { Id = k, Type = "T" }));
        return root;
    }

    [Fact]
    public void KeyFieldResolvesOnlyKeyedFields()
    {
        var node = Node.Descriptor;
        Assert.Equal("id", Keyed.KeyField(node.FindFieldByName("children"))?.Name);
        Assert.Null(Keyed.KeyField(node.FindFieldByName("id")));
        Assert.Null(Keyed.KeyField(Doc.Descriptor.FindFieldByName("items")));
    }

    [Theory]
    [InlineData("greeting", true)]
    [InlineData("user.name", true)]
    [InlineData("_x1", true)]
    [InlineData("us-east-1", false)]
    [InlineData("1abc", false)]
    [InlineData("a/b", false)]
    [InlineData("café", false)]
    [InlineData("", false)]
    [InlineData("true", false)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    public void IdentSafeEntryName(string name, bool safe) => Assert.Equal(safe, Keyed.IdentSafeEntryName(name));

    [Fact]
    public void KeywordKeysStayQuotedAndRoundTrip()
    {
        var root = NodeOf("true", "null");
        string text = new Encoder().Marshal(root);
        Assert.Contains("\"true\" {", text);
        Assert.Contains("\"null\" {", text);
        Assert.Equal(root, DecodeNode(text));
    }

    [Fact]
    public void AssignmentSpellingOfTheKeyedBlock()
    {
        const string Doc = "id = \"root\"\nchildren = {\n  greeting { type = \"Label\" }\n  counter_row = { type = \"HBox\" }\n}\n";
        Assert.Equal(["greeting", "counter_row"], DecodeNode(Doc).Children.Select(c => c.Id));

        var doc = Parser.Parse(Doc);
        Keyed.Canonicalize(doc, Node.Descriptor);
        string formatted = Format.FormatDocument(doc);
        Assert.Contains("children {\n", formatted);
        Assert.Contains("  greeting {\n", formatted);
        Assert.Contains("  counter_row {\n", formatted);
    }

    [Fact]
    public void NamedEntryNeedsABlockValue()
    {
        var e = Assert.Throws<PxfException>(() => DecodeNode("children { greeting = \"x\" }"));
        Assert.Contains("must have a block value", e.Message);
    }

    [Theory]
    [InlineData("children { 1 { } }")]
    [InlineData("children { true { } }")]
    [InlineData("children { a }")]
    [InlineData("children { a { }")]
    public void MalformedKeyedBlocksAreRejected(string doc) =>
        Assert.Throws<PxfException>(() => DecodeNode(doc));

    [Fact]
    public void BindingsConcatenateInDocumentOrder()
    {
        const string Doc = "children {\n  a { type = \"Label\" }\n}\nchildren = [ { id = \"b\"\n type = \"HBox\" } ]\nchildren {\n  c { type = \"VBox\" }\n}\n";
        Assert.Equal(["a", "b", "c"], DecodeNode(Doc).Children.Select(c => c.Id));
    }

    [Fact]
    public void DuplicateCheckIsPerBlock()
    {
        var node = DecodeNode("children { a { } }\nchildren { a { } }\n");
        Assert.Equal(2, node.Children.Count);
        Assert.Contains("children = [", new Encoder().Marshal(node));
    }

    [Fact]
    public void NestedBlocksDoNotInheritKeyChecks()
    {
        const string Doc = "id = \"root\"\nchildren {\n  outer {\n    children {\n      inner { type = \"Leaf\" }\n    }\n  }\n}\n";
        var root = DecodeNode(Doc);
        var outer = Assert.Single(root.Children);
        Assert.Equal("outer", outer.Id);
        Assert.Equal("inner", Assert.Single(outer.Children).Id);

        string text = new Encoder().Marshal(root);
        Assert.DoesNotContain("id = \"outer\"", text);
        Assert.DoesNotContain("id = \"inner\"", text);
        Assert.Contains("inner {", text);
        Assert.Equal(root, DecodeNode(text));
    }

    [Fact]
    public void EncoderPicksTheFormFromTheKeys()
    {
        Assert.Contains("children = [", new Encoder().Marshal(NodeOf("a", "")));
        Assert.Contains("children = [", new Encoder().Marshal(NodeOf("a", "a")));
        Assert.Equal(
            "children {\n  a {\n    type = \"T\"\n  }\n  b {\n    type = \"T\"\n  }\n}\n",
            new Encoder().Marshal(NodeOf("a", "b")));
    }

    [Fact]
    public void WireBytesDoNotDependOnTheSurfaceForm()
    {
        var keyed = DecodeNode("children { a { } }");
        var anonymous = DecodeNode("children = [ { id = \"a\" } ]");
        Assert.Equal(new Node { Children = { new Node { Id = "a" } } }.ToByteArray(), keyed.ToByteArray());
        Assert.Equal(keyed.ToByteArray(), anonymous.ToByteArray());
    }

    [Fact]
    public void QuotedNamesParseEverywhereAndFormatFaithfully()
    {
        const string Src = "regions {\n  \"us-east-1\" {\n    replicas = 3\n  }\n}\n";
        var doc = Parser.Parse(Src);
        var entry = Assert.IsType<Block>(Assert.IsType<Block>(doc.Entries[0]).Entries[0]);
        Assert.Equal("us-east-1", entry.Name);
        Assert.True(entry.NameQuoted);
        Assert.Equal(Src, Format.FormatDocument(doc));

        doc = Parser.Parse("regions {\n  \"us-east-1\" = {\n  }\n}\n");
        var assignment = Assert.IsType<Assignment>(Assert.IsType<Block>(doc.Entries[0]).Entries[0]);
        Assert.Equal("us-east-1", assignment.Key);
        Assert.True(assignment.KeyQuoted);
        Assert.Contains("\"us-east-1\" = {", Format.FormatDocument(doc));

        Assert.True(Assert.IsType<Assignment>(Parser.Parse("\"a\" = 1\n").Entries[0]).KeyQuoted);
        Assert.False(Assert.IsType<Assignment>(Parser.Parse("a = 1\n").Entries[0]).KeyQuoted);
    }

    [Theory]
    [InlineData("m {\n  1 = \"x\"\n}")]
    [InlineData("m {\n  1 { }\n}")]
    public void IntegerKeysStillTakeOnlyTheMapTail(string doc)
    {
        var e = Assert.Throws<PxfException>(() => Parser.Parse(doc));
        Assert.Contains("requires an identifier or string key", e.Message);
    }

    [Theory]
    [InlineData("\"id\" = \"x\"")]                 // document level, assignment tail
    [InlineData("\"children\" { }")]               // document level, block tail
    [InlineData("children { a { \"id\" = \"x\" } }")] // inside an element body
    public void QuotedNameOutsideAKeyedBlockIsRejected(string doc)
    {
        var e = Assert.Throws<KeyedException>(() => DecodeNode(doc));
        Assert.Equal(KeyedErrorKind.QuotedNameUnkeyed, e.Kind);
    }

    [Fact]
    public void CanonicalizeLeavesInvalidBindingsAlone()
    {
        const string Src = "children {\n  greeting {\n    id = \"farewell\"\n  }\n}\n";
        var doc = Parser.Parse(Src);
        Keyed.Canonicalize(doc, Node.Descriptor);
        Assert.Equal(Src, Format.FormatDocument(doc));
    }

    [Fact]
    public void CanonicalizeMovesADroppedKeysCommentToTheNextEntry()
    {
        var doc = Parser.Parse("children {\n  a {\n    # why\n    id = \"a\"\n    type = \"T\"\n  }\n}\n");
        Keyed.Canonicalize(doc, Node.Descriptor);
        Assert.Equal("children {\n  a {\n    # why\n    type = \"T\"\n  }\n}\n", Format.FormatDocument(doc));
    }

    [Theory]
    [InlineData("id = \"root\"\nitems { a { id = \"a\" } }\n")]
    [InlineData("id = \"root\"\nitems = [ { id = \"a\" } ]\n")]
    public void ElementFieldsHaveElementPaths(string doc)
    {
        var result = new Decoder().UnmarshalFull(doc, new KeyedParent());
        Assert.True(result.IsSet("id"));
        Assert.True(result.IsSet("items[0].id"));
    }

    [Theory]
    [InlineData("items { a { id = \"a\" } }\n")]
    [InlineData("items = [ { id = \"a\" } ]\n")]
    public void AnElementKeyDoesNotSatisfyTheParentsRequiredField(string doc)
    {
        var e = Assert.Throws<PxfException>(() => new Decoder().UnmarshalFull(doc, new KeyedParent()));
        Assert.Contains("required field \"id\" is absent", e.Message);
    }

    [Fact]
    public void AnElementNullIsReportedButStaysOutOfTheFieldMask()
    {
        var msg = new KeyedParent();
        var result = new Decoder().UnmarshalFull("id = \"root\"\nitems { a { note = null } }\n", msg);
        Assert.True(result.IsNull("items[0].note"));
        Assert.Null(msg.Null);
    }

    [Fact]
    public void AGoodSchemaHasNoViolations() => Assert.Empty(Schema.ValidateDescriptor(Node.Descriptor));

    [Fact]
    public void DecodersRefuseAMisplacedKey()
    {
        var e = Assert.Throws<PxfException>(() => new Decoder().Unmarshal("id = \"x\"\n", new Misplaced()));
        Assert.Contains("(pxf.key) is valid only on repeated message-typed fields", e.Message);
        Assert.Throws<PxfException>(() => new Decoder().UnmarshalFull("id = \"x\"\n", new Misplaced()));
    }

    [Theory]
    [InlineData("scalar", "(pxf.key) is valid only on repeated message-typed fields")]
    [InlineData("singular", "(pxf.key) is valid only on repeated message-typed fields")]
    [InlineData("map", "(pxf.key) is valid only on repeated message-typed fields")]
    [InlineData("missing", "element message place.v1.Elem has no field \"nope\"")]
    [InlineData("int_key", "key field place.v1.Elem.num must be a singular string field")]
    [InlineData("list_key", "key field place.v1.Elem.names must be a singular string field")]
    public void PlacementRules(string field, string detail)
    {
        var violation = Assert.Single(
            Schema.ValidateFile(Build(Placements())[0]),
            v => v.Element == "place.v1.Holder." + field);
        Assert.Equal(detail, violation.Detail);
        Assert.Equal("place.proto", violation.File);
    }

    [Fact]
    public void AValidPlacementIsNotAViolation() =>
        Assert.DoesNotContain(Schema.ValidateFile(Build(Placements())[0]), v => v.Element == "place.v1.Holder.good");

    [Fact]
    public void TheCheckCoversTheImportClosureOncePerFile()
    {
        // top imports left and right, both of which import place.proto.
        var place = Placements();
        var left = new FileDescriptorProto { Name = "left.proto", Dependency = { "place.proto" } };
        var right = new FileDescriptorProto { Name = "right.proto", Dependency = { "place.proto" } };
        var top = new FileDescriptorProto { Name = "top.proto", Dependency = { "left.proto", "right.proto" } };

        var violations = Schema.ValidateFile(Build(place, left, right, top)[3]);

        Assert.Equal(6, violations.Count);
        Assert.All(violations, v => Assert.Equal("place.proto", v.File));
        Assert.Equal(violations.Count, violations.Select(v => v.Element).Distinct().Count());
    }

    private static IReadOnlyList<FileDescriptor> Build(params FileDescriptorProto[] files)
    {
        var registry = new ExtensionRegistry { AnnotationsExtensions.Key };
        return FileDescriptor.BuildFromByteStrings(files.Select(f => f.ToByteString()), registry);
    }

    private static FileDescriptorProto Placements()
    {
        static FieldOptions Key(string name)
        {
            var options = new FieldOptions();
            options.SetExtension(AnnotationsExtensions.Key, name);
            return options;
        }

        static FieldDescriptorProto Field(string name, int number, FieldDescriptorProto.Types.Type type, bool repeated, string? typeName = null, FieldOptions? options = null)
        {
            var f = new FieldDescriptorProto
            {
                Name = name,
                Number = number,
                Type = type,
                Label = repeated ? FieldDescriptorProto.Types.Label.Repeated : FieldDescriptorProto.Types.Label.Optional,
            };
            if (typeName != null) f.TypeName = typeName;
            if (options != null) f.Options = options;
            return f;
        }

        const FieldDescriptorProto.Types.Type Str = FieldDescriptorProto.Types.Type.String;
        const FieldDescriptorProto.Types.Type Int = FieldDescriptorProto.Types.Type.Int32;
        const FieldDescriptorProto.Types.Type Msg = FieldDescriptorProto.Types.Type.Message;
        const string Elem = ".place.v1.Elem";

        var holder = new DescriptorProto
        {
            Name = "Holder",
            Field =
            {
                Field("scalar", 1, Str, repeated: false, options: Key("id")),
                Field("singular", 2, Msg, repeated: false, Elem, Key("id")),
                Field("map", 3, Msg, repeated: true, ".place.v1.Holder.MapEntry", Key("key")),
                Field("missing", 4, Msg, repeated: true, Elem, Key("nope")),
                Field("int_key", 5, Msg, repeated: true, Elem, Key("num")),
                Field("list_key", 6, Msg, repeated: true, Elem, Key("names")),
                Field("good", 7, Msg, repeated: true, Elem, Key("id")),
            },
            NestedType =
            {
                new DescriptorProto
                {
                    Name = "MapEntry",
                    Options = new MessageOptions { MapEntry = true },
                    Field = { Field("key", 1, Str, repeated: false), Field("value", 2, Str, repeated: false) },
                },
            },
        };
        var elem = new DescriptorProto
        {
            Name = "Elem",
            Field =
            {
                Field("id", 1, Str, repeated: false),
                Field("num", 2, Int, repeated: false),
                Field("names", 3, Str, repeated: true),
            },
        };
        return new FileDescriptorProto
        {
            Name = "place.proto",
            Package = "place.v1",
            Syntax = "proto3",
            MessageType = { holder, elem },
        };
    }
}
