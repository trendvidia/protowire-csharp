// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

// Binding @dataset rows to messages. A row is written back as the body it
// stands for, with a dotted column's cells grouped into one block, and that
// body goes through the decoder: a cell means exactly what `c = <value>`
// means in a body of the row type. Mirrors dataset_bind.go in protowire-go.
public partial class Decoder
{
    /// <summary>
    /// Binds <paramref name="row"/>'s cells to the fields of <paramref name="msg"/>
    /// its columns name, a dotted column reaching into sub-messages. An empty
    /// cell leaves its field absent, so a default applies and a required field
    /// fails. A null cell is present-but-null. Bind each row into a new message:
    /// the message is not reset first.
    /// </summary>
    /// <exception cref="PxfException">
    /// A column names no field, or goes through a repeated, map or scalar field
    /// or into a literal type; or a cell does not match its field. Either way
    /// the error carries the row's position and names the column.
    /// </exception>
    public void BindRow(IMessage msg, IReadOnlyList<string> columns, DatasetRow row)
    {
        if (columns.Count != row.Cells.Count)
        {
            throw new ArgumentException($"BindRow: {columns.Count} columns vs {row.Cells.Count} cells");
        }
        if (DatasetPaths.CheckColumns(msg.Descriptor, columns) is { } problem)
        {
            throw new PxfException(row.Pos, problem);
        }
        var writer = new RowBodyWriter(row);
        writer.Write(columns);
        try
        {
            UnmarshalFull(writer.Body, msg);
        }
        catch (PxfException e)
        {
            throw writer.AtRow(e, columns);
        }
    }

    /// <summary>
    /// Reads the leading directives and the <c>@dataset TYPE ( cols )</c>
    /// header from <paramref name="source"/>, and returns a reader positioned
    /// at the first row, which yields the rows one at a time rather than
    /// holding the document. The header and every row are read under
    /// <see cref="Limits"/>, the bytes held while a row boundary is sought
    /// being capped at its MaxMessageSize, and <see cref="DatasetReader.Scan"/>
    /// binds with <see cref="Registry"/>.
    /// </summary>
    /// <exception cref="NoDatasetException">The stream is a well-formed document with no @dataset.</exception>
    /// <exception cref="PxfException">
    /// The header is malformed, a header cut short included: the error Parse
    /// reports for it. A read failure is thrown as it is.
    /// </exception>
    public DatasetReader NewDatasetReader(Stream source) => new(source, _limits, Registry);

    // The body opens with an empty line, so an error reported at 1:1, a
    // missing required field, falls in no column.
    private sealed class RowBodyWriter(DatasetRow row)
    {
        private readonly StringBuilder _sb = new("\n");
        private readonly List<(int Column, int First, int Last)> _spans = [];
        private int _line = 2;

        public string Body => _sb.ToString();

        private sealed class Node(string name, int column)
        {
            public string Name { get; } = name;
            public int Column { get; } = column; // -1 for a block
            public List<Node> Children { get; } = [];
        }

        public void Write(IReadOnlyList<string> columns)
        {
            var root = new Node("", -1);
            for (int i = 0; i < columns.Count; i++)
            {
                if (row.Cells[i] == null) continue;
                var node = root;
                var segments = columns[i].Split('.');
                foreach (string segment in segments[..^1])
                {
                    var next = node.Children.Find(c => c.Column < 0 && c.Name == segment);
                    if (next == null)
                    {
                        next = new Node(segment, -1);
                        node.Children.Add(next);
                    }
                    node = next;
                }
                node.Children.Add(new Node(segments[^1], i));
            }
            WriteBlock(root);
        }

        private void WriteBlock(Node node)
        {
            foreach (var child in node.Children)
            {
                if (child.Column >= 0)
                {
                    WriteCell(child.Name, child.Column);
                    continue;
                }
                // An error on the block's own lines is laid at the first
                // column in it. Its span comes after its cells', so a cell's wins.
                int first = _line;
                _sb.Append(child.Name).Append(" {\n");
                _line++;
                WriteBlock(child);
                _sb.Append("}\n");
                _spans.Add((FirstColumn(child), first, _line));
                _line++;
            }
        }

        private static int FirstColumn(Node node)
        {
            while (node.Column < 0) node = node.Children[0];
            return node.Column;
        }

        private void WriteCell(string name, int column)
        {
            string text = $"{name} = {Format.FormatValue(row.Cells[column]!)}\n";
            _sb.Append(text);
            int last = _line + text.Count(c => c == '\n') - 1;
            _spans.Add((column, _line, last));
            _line = last + 1;
        }

        // The decoder read a synthetic body, so its positions are lines of
        // that body, and the spans say which column each belongs to.
        public PxfException AtRow(PxfException e, IReadOnlyList<string> columns)
        {
            string message = e.Message[(e.Message.IndexOf(": ", StringComparison.Ordinal) + 2)..];
            foreach (var (column, first, last) in _spans)
            {
                if (e.Pos.Line >= first && e.Pos.Line <= last)
                {
                    message = $"column {columns[column]}: {message}";
                    break;
                }
            }
            return new PxfException(row.Pos, message);
        }
    }
}
