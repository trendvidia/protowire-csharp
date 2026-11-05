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
    /// <remarks>
    /// It keeps every directive, with its dataset rows, in document order
    /// (trendvidia/protowire#558). <c>@type</c> is written first: it binds no
    /// anonymous <c>@proto</c>, so moving it changes nothing. The other
    /// directives follow in source order, each after its comments: a named
    /// directive's body and a <c>@proto</c> body as written, and a dataset as
    /// its header and then one row per line, an absent cell left empty. A
    /// document whose only directive is <c>@type</c> formats as it always has.
    /// </remarks>
    public static string FormatDocument(Document doc)
    {
        var sb = new StringBuilder();

        var others = TopDirectives(doc);
        if (others.Count == 0)
        {
            if (!string.IsNullOrEmpty(doc.TypeURL))
            {
                sb.Append("@type ").Append(doc.TypeURL).Append("\n\n");
            }

            WriteComments(sb, doc.LeadingComments, level: 0);
            FormatEntries(sb, doc.Entries, level: 0);

            return sb.ToString();
        }

        if (!string.IsNullOrEmpty(doc.TypeURL))
        {
            sb.Append("@type ").Append(doc.TypeURL).Append('\n');
        }
        WriteComments(sb, doc.LeadingComments, level: 0);
        foreach (var d in others)
        {
            switch (d)
            {
                case Directive nd:
                    WriteComments(sb, nd.LeadingComments, level: 0);
                    FormatNamedDirective(sb, nd);
                    break;
                case DatasetDirective ds:
                    WriteComments(sb, ds.LeadingComments, level: 0);
                    FormatDataset(sb, ds);
                    break;
                case ProtoDirective pd:
                    WriteComments(sb, pd.LeadingComments, level: 0);
                    FormatProto(sb, pd);
                    break;
            }
        }
        if (doc.Entries.Count > 0)
        {
            sb.Append('\n');
            FormatEntries(sb, doc.Entries, level: 0);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The document's directives other than <c>@type</c>, which the
    /// <see cref="Document"/> keeps in three lists, merged back into source
    /// order: an anonymous <c>@proto</c> binds the typed directive after it,
    /// so the order is part of the meaning. Each element is a
    /// <see cref="Directive"/>, <see cref="DatasetDirective"/> or
    /// <see cref="ProtoDirective"/>.
    /// </summary>
    private static List<object> TopDirectives(Document doc)
    {
        var all = new List<(int Offset, object D)>(
            doc.Directives.Count + doc.Datasets.Count + doc.Protos.Count);
        foreach (var d in doc.Directives) all.Add((d.Pos.Offset, d));
        foreach (var d in doc.Datasets) all.Add((d.Pos.Offset, d));
        foreach (var d in doc.Protos) all.Add((d.Pos.Offset, d));
        // OrderBy is a stable sort, as Go's slices.SortStableFunc is.
        return all.OrderBy(a => a.Offset).Select(a => a.D).ToList();
    }

    /// <summary>
    /// Writes <c>@&lt;name&gt; prefix* [{body}]</c>, the body as it was written:
    /// it is raw text, which the directive's consumer decodes.
    /// </summary>
    private static void FormatNamedDirective(StringBuilder sb, Directive d)
    {
        sb.Append('@').Append(d.Name);
        foreach (var p in d.Prefixes)
        {
            sb.Append(' ').Append(p);
        }
        if (d.HasBody)
        {
            sb.Append(" {").Append(Encoding.UTF8.GetString(d.Body)).Append('}');
        }
        sb.Append('\n');
    }

    /// <summary>
    /// Writes the header, then one row per line. A null cell is an absent
    /// field and is left empty; <c>null</c> (a <see cref="NullVal"/>) is written.
    /// </summary>
    private static void FormatDataset(StringBuilder sb, DatasetDirective d)
    {
        sb.Append("@dataset");
        if (!string.IsNullOrEmpty(d.Type))
        {
            sb.Append(' ').Append(d.Type);
        }
        sb.Append(" (").Append(string.Join(", ", d.Columns)).Append(")\n");
        foreach (var row in d.Rows)
        {
            sb.Append('(');
            for (int i = 0; i < row.Cells.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var cell = row.Cells[i];
                if (cell is not null)
                {
                    FormatValue(sb, cell, 0);
                }
            }
            sb.Append(")\n");
        }
    }

    /// <summary>Writes a <c>@proto</c> directive in the shape it was read in.</summary>
    private static void FormatProto(StringBuilder sb, ProtoDirective d)
    {
        sb.Append("@proto");
        switch (d.Shape)
        {
            case ProtoShape.Anonymous:
                sb.Append(" {").Append(Encoding.UTF8.GetString(d.Body)).Append('}');
                break;
            case ProtoShape.Named:
                sb.Append(' ').Append(d.TypeName)
                    .Append(" {").Append(Encoding.UTF8.GetString(d.Body)).Append('}');
                break;
            case ProtoShape.Source:
                sb.Append(" \"\"\"").Append(Encoding.UTF8.GetString(d.Body)).Append("\"\"\"");
                break;
            case ProtoShape.Descriptor:
                sb.Append(" b\"").Append(Convert.ToBase64String(d.Body)).Append('"');
                break;
        }
        sb.Append('\n');
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
