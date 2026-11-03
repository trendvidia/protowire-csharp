// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
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
/// <para>String values are quoted as Go's strconv.Quote does.</para>
///
/// <para>
/// This pass has no schema, so quoted entry names stay quoted. The
/// schema-aware <c>pxf fmt</c> pipeline runs
/// <see cref="Keyed.Canonicalize"/> first.
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

    /// <summary>
    /// A value as its PXF source literal, the text the formatter writes for it.
    /// A multi-line value has two-space indents and no trailing newline.
    /// </summary>
    public static string FormatValue(IValue value)
    {
        var sb = new StringBuilder();
        FormatValue(sb, value, 0);
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
                    WriteEntryName(sb, a.Key, a.KeyQuoted);
                    sb.Append(" = ");
                    FormatValue(sb, a.Value, level);
                    if (!string.IsNullOrEmpty(a.TrailingComment))
                    {
                        sb.Append(' ').Append(a.TrailingComment);
                    }
                    sb.Append('\n');
                    break;
                case TypeEntry t:
                    WriteComments(sb, t.LeadingComments, level);
                    WriteIndent(sb, level);
                    sb.Append("@type = ");
                    GoFormat.Quote(sb, t.Url);
                    sb.Append('\n');
                    break;
                case MapEntry m:
                    WriteComments(sb, m.LeadingComments, level);
                    WriteIndent(sb, level);
                    if (MapKeyQuoted(m))
                    {
                        GoFormat.Quote(sb, m.Key);
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
                    WriteEntryName(sb, b.Name, b.NameQuoted);
                    sb.Append(" {\n");
                    FormatEntries(sb, b.Entries, level + 1);
                    WriteIndent(sb, level);
                    sb.Append("}\n");
                    break;
            }
        }
    }

    /// <summary>
    /// Writes a field-entry name as the source spelled it. Canonicalizing
    /// the spelling needs the schema: see <see cref="Keyed.Canonicalize"/>.
    /// </summary>
    private static void WriteEntryName(StringBuilder sb, string name, bool quoted)
    {
        if (quoted)
        {
            GoFormat.Quote(sb, name);
        }
        else
        {
            sb.Append(name);
        }
    }

    private static void FormatValue(StringBuilder sb, IValue val, int level)
    {
        switch (val)
        {
            case StringVal s:
                GoFormat.Quote(sb, s.Value);
                break;
            case IntVal i:
                sb.Append(i.Raw);
                if (i.Prec.Length > 0) sb.Append('p').Append(i.Prec);
                break;
            case FloatVal f:
                sb.Append(f.Raw);
                if (f.Prec.Length > 0) sb.Append('p').Append(f.Prec);
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
    /// The spelling of a map key on the way out (draft -01 § Entries and Keys).
    /// A key the document quoted keeps its quotes unless it is identifier-safe.
    /// A key it wrote bare stays bare, because bare <c>true</c> and <c>123</c>
    /// denote a bool and an integer key. An entry built in code has no spelling
    /// to keep: it is bare only if it reads back as one bare key.
    /// </summary>
    private static bool MapKeyQuoted(MapEntry e) =>
        e.KeyQuoted ? !Keyed.IdentSafeEntryName(e.Key) : !LexesAsBareMapKey(e.Key);

    private static bool LexesAsBareMapKey(string s)
    {
        var lexer = new Lexer(s);
        var token = lexer.Next();
        return token.Kind is TokenKind.IDENT or TokenKind.INT or TokenKind.BOOL
            && token.Value == s
            && lexer.Next().Kind == TokenKind.EOF;
    }
}
