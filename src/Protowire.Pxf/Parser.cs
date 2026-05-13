// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pxf;

public class PxfException : Exception
{
    public Position Pos { get; }
    public PxfException(Position pos, string message) : base($"{pos.Line}:{pos.Column}: {message}")
    {
        Pos = pos;
    }
}

public sealed class Parser
{
    // HARDENING.md § Mandatory limits.
    private const int MaxNestingDepth = 100;

    private readonly Lexer _lex;
    private Token _current;
    private List<Comment> _comments = [];
    private int _depth;

    private Parser(string input)
    {
        _lex = new Lexer(input);
        // Prime _current with an EOF sentinel so the nullable-reference-types
        // analysis is satisfied without `null!`; the very next Advance() call
        // overwrites it with the real first token.
        _current = new Token(TokenKind.EOF, string.Empty, Position.Empty);
        Advance();
    }

    private void Advance()
    {
        while (true)
        {
            _current = _lex.Next();
            if (_current.Kind == TokenKind.ILLEGAL)
            {
                throw new PxfException(_current.Pos, _current.Value);
            }
            if (_current.Kind == TokenKind.NEWLINE) continue;
            if (_current.Kind == TokenKind.COMMENT)
            {
                _comments.Add(new Comment(_current.Pos, _current.Value));
                continue;
            }
            break;
        }
    }

    private List<Comment> FlushComments()
    {
        if (_comments.Count == 0) return new List<Comment>();
        var c = _comments;
        _comments = new List<Comment>();
        return c;
    }

    public static Document Parse(string input) => new Parser(input).ParseDocument();

    private Document ParseDocument()
    {
        var leadingComments = FlushComments();
        string typeUrl = string.Empty;
        var directives = new List<Directive>();
        var datasets = new List<DatasetDirective>();
        var protos = new List<ProtoDirective>();
        int bodyOffset = 0;

        // Top-of-document directives: @type, @<name>, @dataset, @proto may
        // interleave in any order. @type populates TypeURL; the others
        // append to their respective lists. bodyOffset tracks the byte
        // right after the last directive token (the closing `}` for
        // block-form, the last token otherwise).
        for (;;)
        {
            switch (_current.Kind)
            {
                case TokenKind.AT_TYPE:
                    Advance();
                    if (_current.Kind != TokenKind.IDENT)
                    {
                        throw new PxfException(_current.Pos, $"expected type name after @type, got {_current.Kind}");
                    }
                    typeUrl = _current.Value;
                    bodyOffset = _current.Pos.Offset + _current.Value.Length;
                    Advance();
                    continue;
                case TokenKind.AT_DIRECTIVE:
                    {
                        var (d, end) = ParseDirective();
                        directives.Add(d);
                        bodyOffset = end;
                        continue;
                    }
                case TokenKind.AT_DATASET:
                    {
                        var (ds, end) = ParseDatasetDirective();
                        datasets.Add(ds);
                        bodyOffset = end;
                        continue;
                    }
                case TokenKind.AT_PROTO:
                    {
                        var (pd, end) = ParseProtoDirective();
                        protos.Add(pd);
                        bodyOffset = end;
                        continue;
                    }
            }
            break;
        }

        // Standalone constraint (draft §3.4.4): a document containing any
        // @dataset directive MUST NOT also carry @type or top-level field
        // entries — the @dataset header IS the document's type declaration.
        if (datasets.Count > 0)
        {
            if (typeUrl.Length > 0)
            {
                throw new PxfException(datasets[0].Pos,
                    "@dataset directive cannot coexist with @type; the @dataset header declares the document's type (draft §3.4.4)");
            }
            if (_current.Kind != TokenKind.EOF)
            {
                throw new PxfException(_current.Pos,
                    "@dataset directive cannot coexist with top-level field entries; the document's payload is the @dataset rows (draft §3.4.4)");
            }
        }

        var entries = new List<IEntry>();
        while (_current.Kind != TokenKind.EOF)
        {
            // Top-level: only field_entry is allowed. The document
            // represents a proto message, never a map<K,V>; map_entry
            // (':' form) is reserved for the inside of a '{ ... }' block.
            entries.Add(ParseEntry(allowMapEntry: false));
        }
        return new Document
        {
            TypeURL = typeUrl,
            Directives = directives,
            Datasets = datasets,
            Protos = protos,
            BodyOffset = bodyOffset,
            LeadingComments = leadingComments,
            Entries = entries,
        };
    }

    /// <summary>
    /// Parses <c>@&lt;name&gt; *(&lt;prefix-id&gt;) [{ ... }]</c>. The
    /// AT_DIRECTIVE token is current on entry. Returns the directive and
    /// the byte offset immediately after its last token.
    /// </summary>
    private (Directive, int) ParseDirective()
    {
        var leading = FlushComments();
        var atPos = _current.Pos;
        string name = _current.Value;
        if (Schema.IsFutureReservedDirective(name))
        {
            throw new PxfException(atPos,
                $"@{name} is a spec-reserved directive name with no v1 semantics (draft §3.4.6)");
        }
        var prefixes = new List<string>();
        int endOffset = atPos.Offset + 1 + name.Length; // `@` + name
        Advance(); // consume AT_DIRECTIVE

        // Zero-or-more prefix identifiers. PXF is whitespace-insignificant,
        // so we can't end the prefix run at a newline. One-token lookahead
        // disambiguates: an IDENT followed by `=` or `:` is a body field
        // key, not a directive prefix.
        while (_current.Kind == TokenKind.IDENT)
        {
            var pk = PeekKind();
            if (pk == TokenKind.EQUALS || pk == TokenKind.COLON)
            {
                break;
            }
            prefixes.Add(_current.Value);
            endOffset = _current.Pos.Offset + _current.Value.Length;
            Advance();
        }

        byte[] body = [];
        bool hasBody = false;
        if (_current.Kind == TokenKind.LBRACE)
        {
            int open = _current.Pos.Offset;
            if (_depth >= MaxNestingDepth)
            {
                throw new PxfException(_current.Pos, $"nesting depth exceeds {MaxNestingDepth}");
            }
            _depth++;
            try
            {
                // Parse the block to validate inner well-formedness.
                ParseBlockVal();
            }
            finally
            {
                _depth--;
            }
            int close = BraceScan.FindMatchingBrace(_lex.Input, open);
            if (close < 0)
            {
                throw new PxfException(atPos, $"directive @{name}: unmatched '{{'");
            }
            body = System.Text.Encoding.UTF8.GetBytes(_lex.Input[(open + 1)..close]);
            hasBody = true;
            endOffset = close + 1;
        }

        string typeField = prefixes.Count == 1 ? prefixes[0] : string.Empty;
        var d = new Directive
        {
            Pos = atPos,
            Name = name,
            Prefixes = prefixes,
            Type = typeField,
            Body = body,
            HasBody = hasBody,
            LeadingComments = leading,
        };
        return (d, endOffset);
    }

    /// <summary>
    /// Parses <c>@dataset &lt;type&gt; ( col1, col2, ... ) row*</c> per
    /// draft §3.4.4. AT_DATASET is current on entry.
    /// </summary>
    private (DatasetDirective, int) ParseDatasetDirective()
    {
        var leading = FlushComments();
        var atPos = _current.Pos;
        Advance(); // consume @dataset

        // Optional row message type (dotted identifier). MAY be omitted
        // when an anonymous @proto precedes the @dataset in document order.
        string type = string.Empty;
        if (_current.Kind == TokenKind.IDENT)
        {
            type = _current.Value;
            Advance();
        }

        if (_current.Kind != TokenKind.LPAREN)
        {
            throw new PxfException(_current.Pos,
                $"expected '(' to start @dataset column list, got {_current.Kind}");
        }
        Advance(); // consume (

        var columns = new List<string>();
        if (_current.Kind != TokenKind.IDENT)
        {
            throw new PxfException(_current.Pos,
                $"@dataset column list must contain at least one field name, got {_current.Kind}");
        }
        for (;;)
        {
            if (_current.Kind != TokenKind.IDENT)
            {
                throw new PxfException(_current.Pos, $"expected column field name, got {_current.Kind}");
            }
            string colName = _current.Value;
            if (colName.Contains('.'))
            {
                throw new PxfException(_current.Pos,
                    $"@dataset column \"{colName}\": dotted column paths are not supported in v1 (draft §3.4.4)");
            }
            columns.Add(colName);
            Advance();
            if (_current.Kind == TokenKind.COMMA)
            {
                Advance();
                continue;
            }
            if (_current.Kind == TokenKind.RPAREN)
            {
                break;
            }
            throw new PxfException(_current.Pos,
                $"expected ',' or ')' in @dataset column list, got {_current.Kind}");
        }
        int endOffset = _current.Pos.Offset + 1; // past `)`
        Advance(); // consume )

        var rows = new List<DatasetRow>();
        while (_current.Kind == TokenKind.LPAREN)
        {
            var (row, rowEnd) = ParseDatasetRow(columns.Count);
            rows.Add(row);
            endOffset = rowEnd;
        }

        var ds = new DatasetDirective
        {
            Pos = atPos,
            Type = type,
            Columns = columns,
            Rows = rows,
            LeadingComments = leading,
        };
        return (ds, endOffset);
    }

    /// <summary>
    /// Parses <c>( cell ( ',' cell )* )</c> with an arity check against
    /// <paramref name="expected"/>. LPAREN is current on entry.
    /// </summary>
    private (DatasetRow, int) ParseDatasetRow(int expected)
    {
        var pos = _current.Pos;
        Advance(); // consume (

        var cells = new List<IValue?>();
        cells.Add(ParseRowCell());
        while (_current.Kind == TokenKind.COMMA)
        {
            Advance();
            cells.Add(ParseRowCell());
        }
        if (_current.Kind != TokenKind.RPAREN)
        {
            throw new PxfException(_current.Pos,
                $"expected ',' or ')' in @dataset row, got {_current.Kind}");
        }
        int endOffset = _current.Pos.Offset + 1;
        Advance(); // consume )

        if (cells.Count != expected)
        {
            throw new PxfException(pos,
                $"@dataset row has {cells.Count} cells, expected {expected} (column count)");
        }
        return (new DatasetRow { Pos = pos, Cells = cells }, endOffset);
    }

    /// <summary>
    /// Consumes one cell of a @dataset row. Returns null for an empty
    /// cell (no value between two commas, or at row start/end). Rejects
    /// list and block values per v1 cell-grammar (draft §3.4.4).
    /// </summary>
    private IValue? ParseRowCell()
    {
        switch (_current.Kind)
        {
            case TokenKind.COMMA:
            case TokenKind.RPAREN:
                return null;
            case TokenKind.LBRACKET:
                throw new PxfException(_current.Pos,
                    "@dataset cells cannot contain list values in v1 (draft §3.4.4)");
            case TokenKind.LBRACE:
                throw new PxfException(_current.Pos,
                    "@dataset cells cannot contain block values in v1 (draft §3.4.4)");
        }
        return ParseValue();
    }

    /// <summary>
    /// Parses <c>@proto &lt;body&gt;</c>. AT_PROTO is current on entry.
    /// Distinguishes four lexically-determined shapes (draft §3.4.5):
    /// anonymous, named, source, descriptor.
    /// </summary>
    private (ProtoDirective, int) ParseProtoDirective()
    {
        var leading = FlushComments();
        var atPos = _current.Pos;
        Advance(); // consume @proto

        switch (_current.Kind)
        {
            case TokenKind.LBRACE:
                {
                    var (body, end) = CaptureBraceBody("@proto (anonymous form)");
                    return (new ProtoDirective
                    {
                        Pos = atPos,
                        Shape = ProtoShape.Anonymous,
                        Body = body,
                        LeadingComments = leading,
                    }, end);
                }
            case TokenKind.IDENT:
                {
                    string typeName = _current.Value;
                    Advance();
                    if (_current.Kind != TokenKind.LBRACE)
                    {
                        throw new PxfException(_current.Pos,
                            $"expected '{{' after @proto {typeName}, got {_current.Kind}");
                    }
                    var (body, end) = CaptureBraceBody("@proto " + typeName);
                    return (new ProtoDirective
                    {
                        Pos = atPos,
                        Shape = ProtoShape.Named,
                        TypeName = typeName,
                        Body = body,
                        LeadingComments = leading,
                    }, end);
                }
            case TokenKind.STRING:
                {
                    // Lexer already applied triple-quote dedent and unescaped.
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(_current.Value);
                    int endOffset = _lex.Pos;
                    Advance();
                    return (new ProtoDirective
                    {
                        Pos = atPos,
                        Shape = ProtoShape.Source,
                        Body = bytes,
                        LeadingComments = leading,
                    }, endOffset);
                }
            case TokenKind.BYTES:
                {
                    string raw = _current.Value;
                    byte[] decoded;
                    try
                    {
                        decoded = Convert.FromBase64String(raw);
                    }
                    catch (FormatException)
                    {
                        // Try URL-safe alphabet (allowed per draft §3.7).
                        try
                        {
                            string padded = raw.Replace('-', '+').Replace('_', '/');
                            int rem = padded.Length % 4;
                            if (rem != 0) padded = padded.PadRight(padded.Length + (4 - rem), '=');
                            decoded = Convert.FromBase64String(padded);
                        }
                        catch (FormatException)
                        {
                            throw new PxfException(_current.Pos,
                                $"@proto descriptor body: invalid base64");
                        }
                    }
                    int endOffset = _lex.Pos;
                    Advance();
                    return (new ProtoDirective
                    {
                        Pos = atPos,
                        Shape = ProtoShape.Descriptor,
                        Body = decoded,
                        LeadingComments = leading,
                    }, endOffset);
                }
            default:
                throw new PxfException(_current.Pos,
                    $"expected '{{', dotted identifier, triple-quoted string, or b\"...\" after @proto, got {_current.Kind}");
        }
    }

    /// <summary>
    /// Slices the raw bytes between <c>{</c> and the matching <c>}</c>
    /// (both exclusive) without decoding the contents as PXF. The
    /// current token must be LBRACE on entry. Repositions the lexer to
    /// the byte right after the closing <c>}</c> and primes the parser
    /// to that token.
    /// </summary>
    private (byte[], int) CaptureBraceBody(string label)
    {
        int open = _current.Pos.Offset;
        int close = BraceScan.FindMatchingBrace(_lex.Input, open);
        if (close < 0)
        {
            throw new PxfException(_current.Pos, $"{label}: unmatched '{{'");
        }
        var body = System.Text.Encoding.UTF8.GetBytes(_lex.Input[(open + 1)..close]);
        _lex.RepositionTo(close + 1);
        Advance(); // prime current token past `}`
        return (body, close + 1);
    }

    /// <summary>
    /// One-token lookahead that skips newlines/comments without
    /// disturbing pending-comment accumulation. Used by
    /// <see cref="ParseDirective"/> to disambiguate "this IDENT is a
    /// directive prefix" from "this IDENT is a body field key".
    /// </summary>
    private TokenKind PeekKind()
    {
        var lexState = _lex.Save();
        var savedCurrent = _current;
        int savedCommentCount = _comments.Count;
        Advance();
        var peeked = _current.Kind;
        _lex.Restore(lexState);
        _current = savedCurrent;
        if (_comments.Count > savedCommentCount)
        {
            _comments.RemoveRange(savedCommentCount, _comments.Count - savedCommentCount);
        }
        return peeked;
    }

    /// <summary>
    /// `allowMapEntry` gates the `:` (map-entry) form: false at document
    /// top level, true inside any '{ ... }' block.
    /// </summary>
    private IEntry ParseEntry(bool allowMapEntry = true)
    {
        var leading = FlushComments();
        var pos = _current.Pos;

        if (_current.Kind != TokenKind.IDENT && _current.Kind != TokenKind.STRING && _current.Kind != TokenKind.INT)
        {
            throw new PxfException(pos, $"expected identifier, string, or integer, got {_current.Kind} (\"{_current.Value}\")");
        }
        var keyKind = _current.Kind;
        string key = _current.Value;
        Advance();

        switch (_current.Kind)
        {
            case TokenKind.EQUALS:
                // `=` denotes a field assignment on a proto message; the key
                // must be an identifier. Map-style keys (string / integer) are
                // only valid with `:`.
                if (keyKind != TokenKind.IDENT)
                {
                    throw new PxfException(pos, $"field assignment with '=' requires an identifier key, got {keyKind} (\"{key}\"); use ':' for map entries");
                }
                Advance();
                var val = ParseValue();
                return new Assignment { Pos = pos, Key = key, Value = val, LeadingComments = leading };

            case TokenKind.COLON:
                // Map entry. Only allowed inside a '{ ... }' block, never at
                // document top level.
                if (!allowMapEntry)
                {
                    throw new PxfException(pos, "map entry (':' form) is only allowed inside a '{ … }' block; use '=' for top-level field assignments");
                }
                Advance();
                var mapVal = ParseValue();
                return new MapEntry { Pos = pos, Key = key, Value = mapVal, LeadingComments = leading };

            case TokenKind.LBRACE:
                // `{ ... }` denotes a submessage field; same identifier-only
                // rule as `=` applies.
                if (keyKind != TokenKind.IDENT)
                {
                    throw new PxfException(pos, $"submessage block requires an identifier key, got {keyKind} (\"{key}\")");
                }
                if (_depth >= MaxNestingDepth)
                {
                    throw new PxfException(_current.Pos, $"nesting depth exceeds {MaxNestingDepth}");
                }
                _depth++;
                try
                {
                    Advance();
                    var entries = ParseBody();
                    return new Block { Pos = pos, Name = key, Entries = entries, LeadingComments = leading };
                }
                finally
                {
                    _depth--;
                }

            default:
                throw new PxfException(_current.Pos, $"expected '=', ':', or '{{' after \"{key}\", got {_current.Kind}");
        }
    }

    private IValue ParseValue()
    {
        var pos = _current.Pos;

        switch (_current.Kind)
        {
            case TokenKind.STRING:
                var s = new StringVal { Pos = pos, Value = _current.Value };
                Advance();
                return s;

            case TokenKind.INT:
                var i = new IntVal { Pos = pos, Raw = _current.Value };
                Advance();
                return i;

            case TokenKind.FLOAT:
                var f = new FloatVal { Pos = pos, Raw = _current.Value };
                Advance();
                return f;

            case TokenKind.BOOL:
                var b = new BoolVal { Pos = pos, Value = _current.Value == "true" };
                Advance();
                return b;

            case TokenKind.BYTES:
                byte[] decoded;
                try
                {
                    decoded = Convert.FromBase64String(_current.Value);
                }
                catch
                {
                    throw new PxfException(pos, $"invalid base64: {_current.Value}");
                }
                var bytes = new BytesVal { Pos = pos, Value = decoded };
                Advance();
                return bytes;

            case TokenKind.TIMESTAMP:
                if (!DateTime.TryParse(_current.Value, out var dt))
                {
                    throw new PxfException(pos, $"invalid timestamp \"{_current.Value}\"");
                }
                var ts = new TimestampVal { Pos = pos, Value = dt, Raw = _current.Value };
                Advance();
                return ts;

            case TokenKind.DURATION:
                var durVal = DurationParser.Parse(_current.Value);
                var dur = new DurationVal { Pos = pos, Value = durVal, Raw = _current.Value };
                Advance();
                return dur;

            case TokenKind.NULL:
                var n = new NullVal { Pos = pos };
                Advance();
                return n;

            case TokenKind.IDENT:
                var id = new IdentVal { Pos = pos, Name = _current.Value };
                Advance();
                return id;

            case TokenKind.LBRACKET:
                return ParseList();

            case TokenKind.LBRACE:
                return ParseBlockVal();

            default:
                throw new PxfException(pos, $"expected value, got {_current.Kind} (\"{_current.Value}\")");
        }
    }

    private IValue ParseList()
    {
        if (_depth >= MaxNestingDepth)
        {
            throw new PxfException(_current.Pos, $"nesting depth exceeds {MaxNestingDepth}");
        }
        _depth++;
        try
        {
            var pos = _current.Pos;
            Advance(); // [

            var elems = new List<IValue>();
            while (_current.Kind != TokenKind.RBRACKET && _current.Kind != TokenKind.EOF)
            {
                elems.Add(ParseValue());
                if (_current.Kind == TokenKind.COMMA)
                {
                    Advance();
                }
            }
            if (_current.Kind != TokenKind.RBRACKET)
            {
                throw new PxfException(_current.Pos, $"expected ']', got {_current.Kind}");
            }
            Advance();
            return new ListVal { Pos = pos, Elements = elems };
        }
        finally
        {
            _depth--;
        }
    }

    private IValue ParseBlockVal()
    {
        if (_depth >= MaxNestingDepth)
        {
            throw new PxfException(_current.Pos, $"nesting depth exceeds {MaxNestingDepth}");
        }
        _depth++;
        try
        {
            var pos = _current.Pos;
            Advance(); // {
            var entries = ParseBody();
            return new BlockVal { Pos = pos, Entries = entries };
        }
        finally
        {
            _depth--;
        }
    }

    private List<IEntry> ParseBody()
    {
        var entries = new List<IEntry>();
        while (_current.Kind != TokenKind.RBRACE && _current.Kind != TokenKind.EOF)
        {
            entries.Add(ParseEntry());
        }
        if (_current.Kind != TokenKind.RBRACE)
        {
            throw new PxfException(_current.Pos, $"expected '}}', got {_current.Kind}");
        }
        Advance();
        return entries;
    }
}
