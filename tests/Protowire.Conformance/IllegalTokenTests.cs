// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Protowire.Pxf;

namespace Protowire.Conformance;

/// <summary>
/// A token the lexer rejects is reported where protowire-go reports it, in its
/// words (#99): a value position gives the lexer's diagnostic alone, and a
/// position that expected something says what, naming the token as Go does.
/// Go is not uniform here (Parse and Unmarshal name the same token differently
/// at some sites), so each site follows its counterpart rather than one rule.
///
/// <para>
/// A sample of a differential run that inserted <c>$</c>, <c>5seconds</c>,
/// <c>"\z"</c> and <c>@</c> at every token boundary of the corpora. The expected
/// messages were printed by protowire-go f8a01b1's pxf.Parse and
/// UnmarshalFullDescriptor for the same documents; an empty one is accepted.
/// </para>
/// </summary>
public class IllegalTokenTests
{
    [Theory]
    [InlineData("test.v1.AllTypes", "dur_field = 5seconds\n",
            "1:13: invalid duration: 5seconds",
            "1:13: invalid duration: 5seconds")]
    [InlineData("test.v1.AllTypes", "repeated_string = [5seconds]\n",
            "1:20: invalid duration: 5seconds",
            "1:20: invalid duration: 5seconds")]
    [InlineData("test.v1.AllTypes", "string_map = { \"k\": 5seconds }\n",
            "1:21: invalid duration: 5seconds",
            "1:21: invalid duration: 5seconds")]
    [InlineData("test.v1.AllTypes", "string_field = $\n",
            "1:16: unexpected character '$'",
            "1:16: unexpected character '$'")]
    [InlineData("test.v1.AllTypes", "bytes_field = b\"!!!\"\n",
            "1:15: invalid base64 in bytes literal",
            "1:15: invalid base64 in bytes literal")]
    [InlineData("test.v1.AllTypes", "5seconds = 1\n",
            "1:1: expected identifier, string, integer, or bool, got invalid duration: 5seconds",
            "1:1: expected identifier, string, integer, or bool, got invalid duration: 5seconds")]
    [InlineData("test.v1.AllTypes", "string_map = { 5seconds: \"x\" }\n",
            "1:16: expected identifier, string, integer, or bool, got invalid duration: 5seconds",
            "1:16: expected map key, got invalid duration: 5seconds")]
    [InlineData("test.v1.AllTypes", "a = 1\n$\n",
            "2:1: expected identifier, string, integer, or bool, got unexpected character '$'",
            "1:1: unknown field \"a\" in test.v1.AllTypes")]
    [InlineData("test.v1.AllTypes", "string_field $\n",
            "1:14: expected '=', ':', or '{' after \"string_field\", got ILLEGAL",
            "1:14: expected '=', ':', or '{' after \"string_field\", got ILLEGAL")]
    [InlineData("test.v1.AllTypes", "@type $\n",
            "1:7: expected type name after @type, got ILLEGAL",
            "1:7: expected type name after @type, got unexpected character '$'")]
    [InlineData("test.v1.AllTypes", "@dataset test.v1.AllTypes ($)\n",
            "1:28: @dataset column list must contain at least one field name, got ILLEGAL",
            "1:28: @dataset column list must contain at least one field name, got unexpected character '$'")]
    [InlineData("test.v1.AllTypes", "@dataset test.v1.AllTypes (string_field)\n(\"a\" $)\n",
            "2:6: expected ',' or ')' in @dataset row, got ILLEGAL",
            "2:6: expected ',' or ')' in @dataset row, got unexpected character '$'")]
    [InlineData("test.v1.AllTypes", "repeated_string = [\"a\" $]\n",
            "1:24: unexpected character '$'",
            "1:24: unexpected character '$'")]
    [InlineData("test.v1.AllTypes", "string_map = { \"k\" $ }\n",
            "1:20: expected '=', ':', or '{' after \"k\", got ILLEGAL",
            "1:20: expected ':' after map key, got ILLEGAL")]
    [InlineData("test.v1.AllTypes", "nested_field { value = 1 $\n",
            "1:26: expected identifier, string, integer, or bool, got unexpected character '$'",
            "1:26: expected identifier, string, integer, or bool, got unexpected character '$'")]
    [InlineData("test.v1.AllTypes", "string_field = @x\n",
            "1:16: expected value, got @directive (\"x\")",
            "1:16: expected string for field \"string_field\"")]
    [InlineData("test.v1.AllTypes", ", = 1\n",
            "1:1: expected identifier, string, integer, or bool, got , (\",\")",
            "1:1: expected identifier, string, integer, or bool, got , (\",\")")]
    [InlineData("keyed.v1.Node", "children { $ { } }\n",
            "1:12: expected identifier, string, integer, or bool, got unexpected character '$'",
            "1:12: expected entry name (identifier or string) in keyed field \"children\", got unexpected character '$'")]
    [InlineData("keyed.v1.Node", "children { a $ }\n",
            "1:14: expected '=', ':', or '{' after \"a\", got ILLEGAL",
            "1:14: expected '{' or '=' after entry name \"a\" in keyed field \"children\", got ILLEGAL")]
    public void ReportsWhatTheReferenceReports(string message, string doc, string parseError, string decodeError)
    {
        Assert.Equal(parseError, Error(() => Parser.Parse(doc)));
        var msg = Corpus.ByName[message].Parser.ParseFrom(ByteString.Empty);
        Assert.Equal(decodeError, Error(() => new Decoder { Registry = Corpus.Registry }.UnmarshalFull(doc, msg)));
    }

    // A hand-written class has no counterpart in protowire-go, so it follows
    // the generated path: the diagnostic alone at a value, and the token named
    // where a name or '=' was expected.
    public sealed class Plain
    {
        public string Name { get; set; } = "";
        public List<int> Ns { get; set; } = [];
        public Dictionary<string, int> M { get; set; } = [];
    }

    [Theory]
    [InlineData("name = $\n", "1:8: unexpected character '$'")]
    [InlineData("ns = [1, 5seconds]\n", "1:10: invalid duration: 5seconds")]
    [InlineData("m = { \"k\": $ }\n", "1:12: unexpected character '$'")]
    [InlineData("m = { $: 1 }\n", "1:7: expected map key, got unexpected character '$'")]
    [InlineData("name $\n", "1:6: expected '=' or '{' after \"name\", got unexpected character '$'")]
    [InlineData("$ = 1\n", "1:1: expected identifier, string, integer, or bool, got unexpected character '$'")]
    public void AHandWrittenClassReportsAsTheGeneratedPathDoes(string doc, string want)
    {
        Assert.Equal(want, Error(() => new Decoder().Unmarshal(doc, new Plain())));
    }

    private static string Error(Action action)
    {
        try
        {
            action();
            return "";
        }
        catch (PxfException e)
        {
            return e.Message;
        }
    }
}
