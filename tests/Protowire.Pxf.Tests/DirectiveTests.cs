// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
//
// Parser-tier tests for the v1.0 directive grammar:
//   - `@<name> *(<prefix>) [{ ... }]`     (draft §3.4.2)
//   - `@entry  *(<prefix>) [{ ... }]`     (draft §3.4.3)
//   - `@dataset  <type> ( cols ) row*`    (draft §3.4.4)
//   - `@proto <body>` (4 shapes)          (draft §3.4.5)
//
// Exercises Parser.Parse directly and asserts on AST shape. Mirrors the
// Go reference at protowire-go/encoding/pxf/directive_test.go +
// directive_proto_test.go.

using Protowire.Pxf;

namespace Protowire.Pxf.Tests;

public class DirectiveTests
{
    // ---- Generic @<name> directive ----

    [Fact]
    public void BareDirective_NoPrefix_NoBody()
    {
        var doc = Parser.Parse("@frob\nname = \"x\"\n");
        Assert.Single(doc.Directives);
        var d = doc.Directives[0];
        Assert.Equal("frob", d.Name);
        Assert.Empty(d.Prefixes);
        Assert.False(d.HasBody);
        Assert.Equal(string.Empty, d.Type);
        Assert.Single(doc.Entries);
    }

    [Fact]
    public void SinglePrefix_PopulatesLegacyType()
    {
        var doc = Parser.Parse("@header chameleon.v1.LayerHeader { id = \"x\" }\nbody = \"z\"\n");
        var d = doc.Directives[0];
        Assert.Equal("header", d.Name);
        Assert.Equal(new[] { "chameleon.v1.LayerHeader" }, d.Prefixes);
        Assert.Equal("chameleon.v1.LayerHeader", d.Type);
        Assert.True(d.HasBody);
        var body = System.Text.Encoding.UTF8.GetString(d.Body);
        Assert.Contains("id = \"x\"", body);
    }

    [Fact]
    public void TwoPrefixes_LeaveTypeEmpty()
    {
        var doc = Parser.Parse("@entry mylabel pkg.MsgType { x = 1 }\nname = \"z\"\n");
        var d = doc.Directives[0];
        Assert.Equal(new[] { "mylabel", "pkg.MsgType" }, d.Prefixes);
        Assert.Equal(string.Empty, d.Type);
    }

    [Fact]
    public void PrefixLookahead_StopsAtBodyKey()
    {
        var doc = Parser.Parse("@foo BarType\nbody_key = \"x\"\n");
        var d = doc.Directives[0];
        Assert.Equal(new[] { "BarType" }, d.Prefixes);
        Assert.Single(doc.Entries);
    }

    [Fact]
    public void MultipleDirectives_InSourceOrder()
    {
        string src =
            "@type some.MsgType\n" +
            "@header pkg.Header { id = \"h1\" }\n" +
            "@frob alpha beta\n" +
            "name = \"z\"\n";
        var doc = Parser.Parse(src);
        Assert.Equal("some.MsgType", doc.TypeURL);
        var names = doc.Directives.Select(d => d.Name).ToArray();
        Assert.Equal(new[] { "header", "frob" }, names);
        Assert.Equal(new[] { "alpha", "beta" }, doc.Directives[1].Prefixes);
        Assert.True(doc.BodyOffset > 0);
    }

    [Fact]
    public void BodyOffset_MatchesEndOfLastDirective()
    {
        var doc = Parser.Parse("@frob alpha\nname = 1\n");
        // "alpha" starts at offset 6 (after "@frob ") and is length 5, so end = 11.
        Assert.Equal(11, doc.BodyOffset);
    }

    [Fact]
    public void BlockBody_PreservesRawBytes()
    {
        var doc = Parser.Parse("@hdr T { a = 1\n b = \"x\" }\nrest = 0\n");
        var d = doc.Directives[0];
        Assert.True(d.HasBody);
        var body = System.Text.Encoding.UTF8.GetString(d.Body);
        Assert.Contains("a = 1", body);
        Assert.Contains("b = \"x\"", body);
        Assert.DoesNotContain('}', body);
    }

    [Fact]
    public void NestedBracesInBody()
    {
        var doc = Parser.Parse("@nested T { inner { a = 1 } }\n");
        var body = System.Text.Encoding.UTF8.GetString(doc.Directives[0].Body);
        Assert.Contains("inner { a = 1 }", body);
    }

    [Fact]
    public void BracesInsideStrings_NotCounted()
    {
        var doc = Parser.Parse("@s T { a = \"}{\" }\n");
        Assert.True(doc.Directives[0].HasBody);
    }

    [Fact]
    public void LineCommentInsideBody()
    {
        var doc = Parser.Parse("@h T { a = 1 # trailing } comment\n  b = 2\n}\n");
        Assert.True(doc.Directives[0].HasBody);
    }

    [Fact]
    public void BlockCommentInsideBody()
    {
        var doc = Parser.Parse("@h T { a = 1 /* not a } close */ b = 2 }\n");
        Assert.True(doc.Directives[0].HasBody);
    }

    [Fact]
    public void AtTypeWithoutIdent_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@type =\n"));
        Assert.Contains("expected type name after @type", ex.Message);
    }

    [Fact]
    public void BareAt_IsIllegal()
    {
        Assert.ThrowsAny<PxfException>(() => Parser.Parse("@\n"));
    }

    // ---- Future-reserved directive names (draft §3.4.6) ----

    [Theory]
    [InlineData("table")]
    [InlineData("datasource")]
    [InlineData("view")]
    [InlineData("procedure")]
    [InlineData("function")]
    [InlineData("permissions")]
    public void FutureReservedDirective_Rejected(string name)
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse($"@{name} foo\nx = 1\n"));
        Assert.Contains("spec-reserved", ex.Message);
        Assert.Contains($"@{name}", ex.Message);
    }

    [Fact]
    public void Schema_IsFutureReservedDirective()
    {
        Assert.True(Schema.IsFutureReservedDirective("table"));
        Assert.True(Schema.IsFutureReservedDirective("permissions"));
        Assert.False(Schema.IsFutureReservedDirective("header"));
        Assert.False(Schema.IsFutureReservedDirective("entry"));
        Assert.False(Schema.IsFutureReservedDirective("dataset"));
        Assert.False(Schema.IsFutureReservedDirective("proto"));
        Assert.False(Schema.IsFutureReservedDirective("type"));
    }

    // ---- @dataset directive ----

    [Fact]
    public void Dataset_BasicTwoColumnsTwoRows()
    {
        string src = "@dataset trades.v1.Trade ( px, qty )\n( 100, 5 )\n( 101, 7 )\n";
        var doc = Parser.Parse(src);
        Assert.Single(doc.Datasets);
        var t = doc.Datasets[0];
        Assert.Equal("trades.v1.Trade", t.Type);
        Assert.Equal(new[] { "px", "qty" }, t.Columns);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(2, t.Rows[0].Cells.Count);
    }

    [Fact]
    public void Dataset_EmptyCell_MeansAbsent()
    {
        var doc = Parser.Parse("@dataset x.Row ( a, b, c )\n( 1, , 3 )\n");
        var row = doc.Datasets[0].Rows[0];
        Assert.NotNull(row.Cells[0]);
        Assert.Null(row.Cells[1]);
        Assert.NotNull(row.Cells[2]);
    }

    [Fact]
    public void Dataset_NullCell_MeansPresentNull()
    {
        var doc = Parser.Parse("@dataset x.Row ( a, b )\n( 1, null )\n");
        var row = doc.Datasets[0].Rows[0];
        Assert.IsType<NullVal>(row.Cells[1]);
    }

    [Fact]
    public void Dataset_ZeroRows_Valid()
    {
        var doc = Parser.Parse("@dataset x.Row ( a, b )\n");
        Assert.Single(doc.Datasets);
        Assert.Empty(doc.Datasets[0].Rows);
    }

    [Fact]
    public void Dataset_ArityMismatch_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row ( a, b )\n( 1, 2, 3 )\n"));
        Assert.Contains("3 cells, expected 2", ex.Message);
    }

    [Fact]
    public void Dataset_DottedColumn_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row ( a.b )\n"));
        Assert.Contains("dotted column", ex.Message);
    }

    [Fact]
    public void Dataset_ListCell_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row ( a )\n( [1, 2] )\n"));
        Assert.Contains("list values", ex.Message);
    }

    [Fact]
    public void Dataset_BlockCell_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row ( a )\n( { x = 1 } )\n"));
        Assert.Contains("block values", ex.Message);
    }

    [Fact]
    public void Dataset_Standalone_RejectsCoexistingAtTypeBefore()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@type other\n@dataset x.Row ( a )\n( 1 )\n"));
        Assert.Contains("cannot coexist with @type", ex.Message);
    }

    [Fact]
    public void Dataset_Standalone_RejectsAtTypeAfterDataset()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row ( a )\n@type other\n"));
        Assert.Contains("cannot coexist with @type", ex.Message);
    }

    [Fact]
    public void Dataset_Standalone_RejectsCoexistingBodyEntries()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row ( a )\n( 1 )\nextra = 5\n"));
        Assert.Contains("cannot coexist with top-level field entries", ex.Message);
    }

    [Fact]
    public void Dataset_MissingType_IsPermissive()
    {
        // Type optional in v1 — binds to preceding anonymous @proto.
        var doc = Parser.Parse("@dataset ( a )\n");
        Assert.Single(doc.Datasets);
        Assert.Equal(string.Empty, doc.Datasets[0].Type);
    }

    [Fact]
    public void Dataset_MissingLParen_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row a, b\n"));
        Assert.Contains("expected '(' to start", ex.Message);
    }

    [Fact]
    public void Dataset_EmptyColumnList_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row ( )\n"));
        Assert.Contains("at least one field name", ex.Message);
    }

    [Fact]
    public void Dataset_BadColumnToken_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row ( a, 123 )\n"));
        Assert.Contains("expected column field name", ex.Message);
    }

    [Fact]
    public void Dataset_MissingCommaInColumns_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row ( a b )\n"));
        Assert.Contains("expected ',' or ')' in @dataset column list", ex.Message);
    }

    [Fact]
    public void Dataset_MissingCommaInRow_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@dataset x.Row ( a, b )\n( 1 2 )\n"));
        Assert.Contains("expected ',' or ')' in @dataset row", ex.Message);
    }

    // ---- @proto directive ----

    [Fact]
    public void Proto_Anonymous_CapturesRawBytes()
    {
        var doc = Parser.Parse("@proto { int32 id = 1; string name = 2; }\n");
        Assert.Single(doc.Protos);
        var p = doc.Protos[0];
        Assert.Equal(ProtoShape.Anonymous, p.Shape);
        Assert.Equal(string.Empty, p.TypeName);
        var body = System.Text.Encoding.UTF8.GetString(p.Body);
        Assert.Contains("int32 id = 1;", body);
        Assert.Contains("string name = 2;", body);
    }

    [Fact]
    public void Proto_Named_CapturesRawBytes()
    {
        var doc = Parser.Parse("@proto trades.v1.Trade { double px = 1; int64 qty = 2; }\n");
        var p = doc.Protos[0];
        Assert.Equal(ProtoShape.Named, p.Shape);
        Assert.Equal("trades.v1.Trade", p.TypeName);
        var body = System.Text.Encoding.UTF8.GetString(p.Body);
        Assert.Contains("double px = 1;", body);
    }

    [Fact]
    public void Proto_Source_TripleQuoted()
    {
        string src = "@proto \"\"\"\n  syntax = \"proto3\";\n  message M { int32 id = 1; }\n  \"\"\"\n";
        var doc = Parser.Parse(src);
        var p = doc.Protos[0];
        Assert.Equal(ProtoShape.Source, p.Shape);
        var body = System.Text.Encoding.UTF8.GetString(p.Body);
        Assert.Contains("syntax = \"proto3\";", body);
    }

    [Fact]
    public void Proto_Descriptor_Base64()
    {
        // Base64 of arbitrary bytes; we only check round-trip, not decoded shape.
        string b64 = Convert.ToBase64String(new byte[] { 0x0a, 0x05, 0x68, 0x65, 0x6c, 0x6c, 0x6f });
        var doc = Parser.Parse($"@proto b\"{b64}\"\n");
        var p = doc.Protos[0];
        Assert.Equal(ProtoShape.Descriptor, p.Shape);
        Assert.Equal(new byte[] { 0x0a, 0x05, 0x68, 0x65, 0x6c, 0x6c, 0x6f }, p.Body);
    }

    [Fact]
    public void Proto_NamedWithoutBrace_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@proto trades.v1.Trade\n"));
        Assert.Contains("expected '{'", ex.Message);
    }

    [Fact]
    public void Proto_BadShape_Rejected()
    {
        var ex = Assert.Throws<PxfException>(() => Parser.Parse("@proto =\n"));
        Assert.Contains("expected '{', dotted identifier", ex.Message);
    }

    [Fact]
    public void Proto_AnonymousBeforeDataset()
    {
        // Anonymous @proto can precede @dataset and the dataset Type may be empty.
        string src =
            "@proto { int32 id = 1; }\n" +
            "@dataset ( id )\n" +
            "( 7 )\n";
        var doc = Parser.Parse(src);
        Assert.Single(doc.Protos);
        Assert.Equal(ProtoShape.Anonymous, doc.Protos[0].Shape);
        Assert.Single(doc.Datasets);
        Assert.Equal(string.Empty, doc.Datasets[0].Type);
        Assert.Single(doc.Datasets[0].Rows);
    }

    [Fact]
    public void ProtoShapeNames_RoundTrip()
    {
        Assert.Equal("anonymous", ProtoShape.Anonymous.Name());
        Assert.Equal("named", ProtoShape.Named.Name());
        Assert.Equal("source", ProtoShape.Source.Name());
        Assert.Equal("descriptor", ProtoShape.Descriptor.Name());
    }
}
