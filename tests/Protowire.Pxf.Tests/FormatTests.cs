using Protowire.Pxf;

namespace Protowire.Pxf.Tests;

/// <summary>
/// Tests for the comment-preserving AST formatter
/// (<see cref="Format.FormatDocument"/>).
/// </summary>
public class FormatTests
{
    [Fact]
    public void EmitsTypeDirective()
    {
        const string Src = "@type test.v1.Foo\n\nx = 1\n";
        var doc = Parser.Parse(Src);
        Assert.Equal("test.v1.Foo", doc.TypeURL);

        string formatted = Format.FormatDocument(doc);
        Assert.StartsWith("@type test.v1.Foo\n\n", formatted);

        // Re-parse must round-trip the type URL.
        var doc2 = Parser.Parse(formatted);
        Assert.Equal("test.v1.Foo", doc2.TypeURL);
    }

    [Fact]
    public void PreservesComments()
    {
        const string Src =
            "# leading\n" +
            "name = \"Alice\"\n" +
            "# block comment for nested\n" +
            "nested {\n" +
            "  inner = 42\n" +
            "}\n";

        var doc = Parser.Parse(Src);
        string formatted = Format.FormatDocument(doc);

        Assert.Contains("# leading", formatted);
        Assert.Contains("# block comment for nested", formatted);

        // Re-parsing the formatted output must succeed.
        Parser.Parse(formatted);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("with space")]
    [InlineData("embedded \" quote")]
    [InlineData("back\\slash")]
    [InlineData("tab\there")]
    [InlineData("newline\nin\nstring")]
    [InlineData("control \x01 byte")]   // ← \xHH escape exercise
    [InlineData("café 日本 \U0001F600")]
    public void RoundTripsStringValues(string value)
    {
        // Build input PXF by quoting the value with the same escape rules
        // the lexer + encoder both accept.
        string src = $"string_field = {Quote(value)}\n";

        var doc = Parser.Parse(src);
        var assignment = (Assignment)doc.Entries[0];
        var sv = (StringVal)assignment.Value;
        Assert.Equal(value, sv.Value);

        string formatted = Format.FormatDocument(doc);

        var doc2 = Parser.Parse(formatted);
        var assignment2 = (Assignment)doc2.Entries[0];
        var sv2 = (StringVal)assignment2.Value;
        Assert.Equal(value, sv2.Value);
    }

    /// <summary>Mirrors Format.WriteQuotedString so tests can build inputs.</summary>
    private static string Quote(string s)
    {
        const string Hex = "0123456789abcdef";
        var sb = new System.Text.StringBuilder();
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\x").Append(Hex[(c >> 4) & 0xF]).Append(Hex[c & 0xF]);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
