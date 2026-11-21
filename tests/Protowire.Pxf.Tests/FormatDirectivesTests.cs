// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Text;
using Protowire.Pxf;

namespace Protowire.Pxf.Tests;

/// <summary>
/// <see cref="Format.FormatDocument"/> keeps every directive, with its dataset
/// rows, in document order (trendvidia/protowire#558, #105). The expected
/// texts are the ones protowire-go's <c>format_directives_test.go</c> holds.
/// </summary>
public class FormatDirectivesTests
{
    /// <summary>
    /// Formats <paramref name="src"/>, checks that the output parses to the
    /// same directives and is a fixed point of formatting, and returns it.
    /// </summary>
    private static string FormatRoundTrip(string src)
    {
        var doc = Parser.Parse(src);
        string output = Format.FormatDocument(doc);
        var again = Parser.Parse(output);
        Assert.Equal(DirectiveShape(doc), DirectiveShape(again));
        Assert.Equal(output, Format.FormatDocument(again));
        return output;
    }

    /// <summary>What a document's directives say, without positions.</summary>
    private static List<string> DirectiveShape(Document doc)
    {
        var output = new List<string> { doc.TypeURL };
        foreach (var d in doc.Directives)
        {
            output.Add("@" + d.Name);
            output.Add(string.Join(" ", d.Prefixes));
            output.Add(Encoding.UTF8.GetString(d.Body));
            output.Add(d.HasBody.ToString());
        }
        foreach (var d in doc.Datasets)
        {
            output.Add("@dataset");
            output.Add(d.Type);
            output.Add(string.Join(",", d.Columns));
            foreach (var r in d.Rows)
            {
                output.Add(string.Join("|", r.Cells.Select(c => c is null ? "<absent>" : Format.FormatValue(c))));
            }
        }
        foreach (var d in doc.Protos)
        {
            output.Add("@proto");
            output.Add(d.Shape.Name());
            output.Add(d.TypeName);
            output.Add(Convert.ToBase64String(d.Body));
        }
        output.Add(doc.Entries.Count.ToString());
        return output;
    }

    [Fact]
    public void Dataset()
    {
        // The issue's document A formatted to nothing.
        Assert.Equal(
            "@dataset trades.v1.Trade (id, px)\n(1, 2.5)\n(2, 3.5)\n",
            FormatRoundTrip("@dataset trades.v1.Trade (id, px)\n(1, 2.5)\n(2, 3.5)\n"));

        // An empty cell is an absent field and null a present one; both stay.
        Assert.Equal(
            "@dataset trades.v1.Trade (id, px, note)\n(1, 2.5, )\n(2, , null)\n",
            FormatRoundTrip("@dataset trades.v1.Trade (id, px, note)\n(1, 2.5, )\n(2, , null)\n"));
    }

    [Fact]
    public void EntryDirective()
    {
        // The issue's document B lost its @entry.
        const string Src = "@type a.v1.M\n@entry x { a = 1 }\n\nname = \"n\"\n";
        Assert.Equal(Src, FormatRoundTrip(Src));
    }

    [Fact]
    public void EntryAndNamedDirective()
    {
        const string Src = "@type a.v1.M\n@entry x { a = 1 }\n@header h.v1.H\n\nname = \"n\"\n";
        Assert.Equal(Src, FormatRoundTrip(Src));
    }

    [Fact]
    public void ProtoThenEntry()
    {
        // The issue's third measured case formatted to nothing.
        const string Src = "@proto a.v1.P { int32 b = 1; }\n@entry x { a = 1 }\n";
        Assert.Equal(Src, FormatRoundTrip(Src));
    }

    [Theory]
    [InlineData("@proto { string id = 1; }\n@dataset (id)\n(\"a\")\n", "")]
    [InlineData("@proto a.v1.R { string id = 1; }\n@dataset a.v1.R (id)\n(\"a\")\n", "")]
    // The triple quotes go on lines of their own (protowire-go#303).
    [InlineData("@proto \"\"\"syntax = \"proto3\"; message R { string id = 1; }\"\"\"\n@dataset R (id)\n(\"a\")\n",
        "@proto \"\"\"\nsyntax = \"proto3\"; message R { string id = 1; }\n\"\"\"\n@dataset R (id)\n(\"a\")\n")]
    // @type moves to the front: it binds no @proto, so this changes nothing.
    [InlineData("@proto b\"CgA=\"\n@type a.v1.M\n\nx = 1\n", "@type a.v1.M\n@proto b\"CgA=\"\n\nx = 1\n")]
    public void ProtoShapes(string src, string want)
    {
        Assert.Equal(want == "" ? src : want, FormatRoundTrip(src));
    }

    /// <summary>
    /// A triple-quoted @proto is written with its delimiters on lines of their
    /// own, and its body reads back byte for byte. Ports protowire-go's
    /// <c>TestFormatDocument_ProtoSourceLayout</c> (protowire-go#303).
    /// </summary>
    [Fact]
    public void ProtoSourceLayoutKeepsLineBreaks()
    {
        // protowire-go#303's document: the line breaks around the body stay.
        const string Src = "@proto \"\"\"\n  syntax = \"proto3\";\n  package fmt.v1;\n  message Doc { string name = 1; }\n\"\"\"\n@type fmt.v1.Doc\n";
        Assert.Equal(
            "@type fmt.v1.Doc\n@proto \"\"\"\n  syntax = \"proto3\";\n  package fmt.v1;\n  message Doc { string name = 1; }\n\"\"\"\n",
            FormatRoundTrip(Src));
    }

    [Theory]
    // Written on the closing delimiter's line, the last quote would open the
    // closing """ early, and the output would not parse.
    [InlineData("@proto \"\"\"\n  syntax = \"proto3\";\n  import \"google/protobuf/empty.proto\"\n  \"\"\"\n",
        "syntax = \"proto3\";\nimport \"google/protobuf/empty.proto\"")]
    // Written on the closing delimiter's line, a last line of only whitespace
    // would be taken for the delimiter's indent and dropped.
    [InlineData("@proto \"\"\"syntax = \"proto3\";\n  \n\"\"\"\n", "syntax = \"proto3\";\n  ")]
    [InlineData("@proto \"\"\"\n\nsyntax = \"proto3\";\"\"\"\n", "\nsyntax = \"proto3\";")]
    [InlineData("@proto \"\"\"syntax = \"proto3\";\n\n\"\"\"\n", "syntax = \"proto3\";\n")]
    [InlineData("@proto \"\"\"\"\"\"\n", "")]
    public void ProtoSourceBodyReadsBack(string src, string body)
    {
        var doc = Parser.Parse(src);
        Assert.Single(doc.Protos);
        Assert.Equal(body, Encoding.UTF8.GetString(doc.Protos[0].Body));
        Assert.Equal("@proto \"\"\"\n" + body + "\n\"\"\"\n", FormatRoundTrip(src));
    }

    /// <summary>
    /// An anonymous @proto binds the next typed directive, so a @proto
    /// written after a dataset in the source stays after it.
    /// </summary>
    [Fact]
    public void DirectivesKeepSourceOrder()
    {
        const string Src = "@dataset t.v1.T (id)\n(1)\n@proto { string id = 1; }\n@dataset (id)\n(\"a\")\n";
        Assert.Equal(Src, FormatRoundTrip(Src));
    }

    [Fact]
    public void DirectiveComments()
    {
        string output = FormatRoundTrip("# about the rows\n@dataset t.v1.T (id)\n(1)\n# second\n@dataset t.v1.U (id)\n(2)\n");
        Assert.Contains("# about the rows\n@dataset t.v1.T (id)", output);
        Assert.Contains("# second\n@dataset t.v1.U (id)", output);
    }

    /// <summary>A document whose only directive is @type formats as it always has.</summary>
    [Fact]
    public void OnlyTypeUnchanged()
    {
        var doc = Parser.Parse("# c\n@type a.v1.M\n\nx = 1\n");
        Assert.Equal("@type a.v1.M\n\n# c\nx = 1\n", Format.FormatDocument(doc));
    }
}
