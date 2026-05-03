using System.Text;

namespace Protowire.Pxf;

/// <summary>
/// Comment-preserving formatter: AST <see cref="Document"/> → PXF text.
///
/// <para>
/// Unlike <see cref="Encoder"/> (which works from a proto message and loses
/// comments), this formats directly from a parsed AST. The natural pairing
/// is <see cref="Parser.Parse(string)"/> on the way in.
/// </para>
///
/// <para>
/// String values use the same escape set the lexer accepts: `\"` `\\` `\n`
/// `\r` `\t` plus `\xHH` for control bytes &lt; 0x20. Code units &gt;= 0x20
/// pass through literally so valid UTF-8 stays UTF-8.
/// </para>
///
/// <para>Mirrors <c>protowire-go/encoding/pxf/format.go</c>.</para>
/// </summary>
public static class Format
{
    /// <summary>Pretty-prints a parsed <see cref="Document"/>, preserving comments.</summary>
    public static string FormatDocument(Document doc)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(doc.TypeURL))
        {
            sb.Append("@type ").Append(doc.TypeURL).Append("\n\n");
        }

        WriteComments(sb, doc.LeadingComments, level: 0);
        FormatEntries(sb, doc.Entries, level: 0);

        return sb.ToString();
    }

    private static void FormatEntries(StringBuilder sb, IReadOnlyList<IEntry> entries, int level)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case Assignment a:
                    WriteComments(sb, a.LeadingComments, level);
                    WriteIndent(sb, level);
                    sb.Append(a.Key).Append(" = ");
                    FormatValue(sb, a.Value, level);
                    if (!string.IsNullOrEmpty(a.TrailingComment))
                    {
                        sb.Append(' ').Append(a.TrailingComment);
                    }
                    sb.Append('\n');
                    break;
                case MapEntry m:
                    WriteComments(sb, m.LeadingComments, level);
                    WriteIndent(sb, level);
                    if (NeedsQuoting(m.Key))
                    {
                        Encoder.WriteQuotedString(sb, m.Key);
                    }
                    else
                    {
                        sb.Append(m.Key);
                    }
                    sb.Append(": ");
                    FormatValue(sb, m.Value, level);
                    if (!string.IsNullOrEmpty(m.TrailingComment))
                    {
                        sb.Append(' ').Append(m.TrailingComment);
                    }
                    sb.Append('\n');
                    break;
                case Block b:
                    WriteComments(sb, b.LeadingComments, level);
                    WriteIndent(sb, level);
                    sb.Append(b.Name).Append(" {\n");
                    FormatEntries(sb, b.Entries, level + 1);
                    WriteIndent(sb, level);
                    sb.Append("}\n");
                    break;
            }
        }
    }

    private static void FormatValue(StringBuilder sb, IValue val, int level)
    {
        switch (val)
        {
            case StringVal s:
                Encoder.WriteQuotedString(sb, s.Value);
                break;
            case IntVal i:
                sb.Append(i.Raw);
                break;
            case FloatVal f:
                sb.Append(f.Raw);
                break;
            case BoolVal b:
                sb.Append(b.Value ? "true" : "false");
                break;
            case BytesVal bv:
                sb.Append("b\"").Append(Convert.ToBase64String(bv.Value)).Append('"');
                break;
            case NullVal:
                sb.Append("null");
                break;
            case IdentVal id:
                sb.Append(id.Name);
                break;
            case TimestampVal ts:
                sb.Append(ts.Raw);
                break;
            case DurationVal d:
                sb.Append(d.Raw);
                break;
            case ListVal lv:
                sb.Append("[\n");
                for (int i = 0; i < lv.Elements.Count; i++)
                {
                    WriteIndent(sb, level + 1);
                    FormatValue(sb, lv.Elements[i], level + 1);
                    if (i + 1 < lv.Elements.Count) sb.Append(',');
                    sb.Append('\n');
                }
                WriteIndent(sb, level);
                sb.Append(']');
                break;
            case BlockVal bv2:
                sb.Append("{\n");
                FormatEntries(sb, bv2.Entries, level + 1);
                WriteIndent(sb, level);
                sb.Append('}');
                break;
        }
    }

    private static void WriteIndent(StringBuilder sb, int level)
    {
        for (int i = 0; i < level; i++) sb.Append("  ");
    }

    private static void WriteComments(StringBuilder sb, IReadOnlyList<Comment> comments, int level)
    {
        foreach (var c in comments)
        {
            WriteIndent(sb, level);
            sb.Append(c.Text).Append('\n');
        }
    }

    /// <summary>
    /// Map keys must be quoted unless they look like a plain identifier
    /// (letter or `_`, then letters / digits / `_`). Empty strings are
    /// always quoted.
    /// </summary>
    private static bool NeedsQuoting(string s)
    {
        if (s.Length == 0) return true;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            bool ok = (ch is >= 'a' and <= 'z')
                   || (ch is >= 'A' and <= 'Z')
                   || ch == '_'
                   || (i > 0 && ch is >= '0' and <= '9');
            if (!ok) return true;
        }
        return false;
    }
}
