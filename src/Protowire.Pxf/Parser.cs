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

    public PxfException(Position pos, string message, Exception inner) : base($"{pos.Line}:{pos.Column}: {message}", inner)
    {
        Pos = pos;
    }
}

public sealed class Parser
{
    private readonly Lexer _lex;
    private readonly int _maxDepth;
    private Token _current;
    private List<Comment> _comments = [];
    private int _depth;

    private Parser(string input, PxfLimits limits) : this(new Lexer(input, limits.MaxBytesLiteralLength), limits)
    {
    }

    private Parser(Lexer lex, PxfLimits limits)
    {
        _maxDepth = limits.MaxNestingDepth;
        _lex = lex;
        // Prime _current with an EOF sentinel so the nullable-reference-types
        // analysis is satisfied without `null!`; the very next Advance() call
        // overwrites it with the real first token.
        _current = new Token(TokenKind.EOF, string.Empty, Position.Empty);
        Advance();
    }

    /// <summary>
    /// A parser running on the decoder's own lexer, for the values the decoder
    /// reads as Parse reads them: a @dataset cell, a directive body, a value
    /// it discards. It stops on the token after the value.
    /// </summary>
    internal Parser(Lexer lex, Token current, int maxDepth, int depth)
    {
        _lex = lex;
        _current = current;
        _maxDepth = maxDepth;
        _depth = depth;
    }

    internal Token Current => _current;

    internal IValue ParseValueHere() => ParseValue();

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

    public static Document Parse(string input) => Parse(input, PxfLimits.Default);

    internal const string DatasetBesideEntries =
        "@dataset directive cannot coexist with top-level field entries; the document's payload is the @dataset rows (draft §3.4.4)";

    /// <summary>
    /// A parser over <c>input[start..end]</c> whose first character sits at
    /// document position <paramref name="at"/>: what it reports carries the
    /// positions Parse reports for the whole document. MaxMessageSize is the
    /// caller's to apply. Mirrors <c>newParserAt</c> in protowire-go.
    /// </summary>
    internal static Parser At(string input, int start, int end, Position at, PxfLimits limits) =>
        new(new Lexer(input, start, end, at, limits.MaxBytesLiteralLength), limits);

    /// <summary>Parses the input as a document, as <see cref="Parse(string, PxfLimits)"/> does.</summary>
    internal Document ParseDocumentHere() => ParseDocument();

    /// <summary>
    /// Parses the @dataset row the current '(' opens, stopping on its ')'
    /// rather than reading past it. Returns the row and the document offset
    /// just past the ')'.
    /// </summary>
    internal (DatasetRow, int) ParseRowHere(int expected) => ParseDatasetRow(expected, advancePastClose: false);

    /// <summary>Parses under the given limits.</summary>
    public static Document Parse(string input, PxfLimits limits)
    {
        Decoder.CheckMessageSize(input, limits);
        return new Parser(input, limits).ParseDocument();
    }

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
                throw new PxfException(_current.Pos, DatasetBesideEntries);
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
            int open = _current.Pos.Offset - _lex.Base;
            // Parse the block to validate inner well-formedness. ParseBlockVal
            // counts this `{` as the one descent it is.
            ParseBlockVal();
            int close = BraceScan.FindMatchingBrace(_lex.Input, open);
            if (close < 0)
            {
                throw new PxfException(atPos, $"directive @{name}: unmatched '{{'");
            }
            body = System.Text.Encoding.UTF8.GetBytes(_lex.Input[(open + 1)..close]);
            hasBody = true;
            endOffset = _lex.Base + close + 1;
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
        var columnPositions = new List<Position>();
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
            columns.Add(_current.Value);
            columnPositions.Add(_current.Pos);
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
        DatasetPaths.CheckHeader(columns, columnPositions);
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
    private (DatasetRow, int) ParseDatasetRow(int expected, bool advancePastClose = true)
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
        if (advancePastClose)
        {
            Advance(); // consume )
        }

        if (cells.Count != expected)
        {
            throw new PxfException(pos,
                $"@dataset row has {cells.Count} cells, expected {expected} (column count)");
        }
        return (new DatasetRow { Pos = pos, Cells = cells }, endOffset);
    }

    /// <summary>
    /// Consumes one cell of a @dataset row. Returns null for an empty
    /// cell (no value between two commas, or at row start/end). Any value
    /// is a cell, lists and blocks included (draft §3.4.4).
    /// </summary>
    private IValue? ParseRowCell()
    {
        if (_current.Kind is TokenKind.COMMA or TokenKind.RPAREN) return null;
        // Comments before the cell belong to no entry of the cell.
        var pending = _comments;
        _comments = [];
        var value = ParseValue();
        pending.AddRange(_comments);
        _comments = pending;
        return value;
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
                    int endOffset = _lex.Base + _lex.Pos;
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
                    if (!Base64Lenient.TryDecode(raw, out decoded))
                    {
                        throw new PxfException(_current.Pos, "@proto descriptor body: invalid base64");
                    }
                    int endOffset = _lex.Base + _lex.Pos;
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
        int open = _current.Pos.Offset - _lex.Base;
        int close = BraceScan.FindMatchingBrace(_lex.Input, open);
        if (close < 0)
        {
            throw new PxfException(_current.Pos, $"{label}: unmatched '{{'");
        }
        var body = System.Text.Encoding.UTF8.GetBytes(_lex.Input[(open + 1)..close]);
        _lex.RepositionTo(close + 1);
        Advance(); // prime current token past `}`
        return (body, _lex.Base + close + 1);
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

        if (_current.Kind is not (TokenKind.IDENT or TokenKind.STRING or TokenKind.INT or TokenKind.BOOL))
        {
            throw new PxfException(pos, $"expected identifier, string, integer, or bool, got {_current.Kind} (\"{_current.Value}\")");
        }
        var keyKind = _current.Kind;
        string key = _current.Value;
        string keyPrec = keyKind == TokenKind.INT ? _lex.Prec : "";
        Advance();

        switch (_current.Kind)
        {
            case TokenKind.EQUALS:
                // `=` takes a field_entry name: an identifier, or a string
                // (the quoted entry name of draft -01 "Keyed Repeated
                // Fields"; well-formed everywhere, and the schema layer
                // restricts it to keyed blocks). An integer key is only
                // valid with `:`.
                if (keyKind is TokenKind.INT or TokenKind.BOOL)
                {
                    throw NonNameAssignment(pos, keyKind, key);
                }
                Advance();
                var val = ParseValue();
                return new Assignment { Pos = pos, Key = key, KeyQuoted = keyKind == TokenKind.STRING, Value = val, LeadingComments = leading };

            case TokenKind.COLON:
                // Map entry. Only allowed inside a '{ ... }' block, never at
                // document top level.
                if (!allowMapEntry)
                {
                    throw TopLevelMapEntry(pos);
                }
                if (keyPrec.Length > 0)
                {
                    throw new PxfException(pos, $"map key {key}: a key cannot carry a precision suffix (p{keyPrec})");
                }
                Advance();
                var mapVal = ParseValue();
                return new MapEntry { Pos = pos, Key = key, KeyQuoted = keyKind == TokenKind.STRING, Value = mapVal, LeadingComments = leading };

            case TokenKind.LBRACE:
                // `{ ... }` is the block tail of a field_entry; same name
                // rule as `=`.
                if (keyKind is TokenKind.INT or TokenKind.BOOL)
                {
                    throw NonNameBlock(pos, keyKind, key);
                }
                if (_depth >= _maxDepth)
                {
                    throw new PxfException(_current.Pos, $"nesting depth exceeds MaxNestingDepth={_maxDepth}");
                }
                _depth++;
                try
                {
                    Advance();
                    var entries = ParseBody();
                    return new Block { Pos = pos, Name = key, NameQuoted = keyKind == TokenKind.STRING, Entries = entries, LeadingComments = leading };
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
                var i = new IntVal { Pos = pos, Raw = _current.Value, Prec = _lex.Prec };
                Advance();
                return i;

            case TokenKind.FLOAT:
                var f = new FloatVal { Pos = pos, Raw = _current.Value, Prec = _lex.Prec };
                Advance();
                return f;

            case TokenKind.BOOL:
                var b = new BoolVal { Pos = pos, Value = _current.Value == "true" };
                Advance();
                return b;

            case TokenKind.BYTES:
                if (!Base64Lenient.TryDecode(_current.Value, out var decoded))
                {
                    throw new PxfException(pos, $"invalid base64: {_current.Value}");
                }
                var bytes = new BytesVal { Pos = pos, Value = decoded };
                Advance();
                return bytes;

            case TokenKind.TIMESTAMP:
                if (!Rfc3339.TryParse(_current.Value, out long tsSeconds, out int tsNanos))
                {
                    throw new PxfException(pos, $"invalid timestamp \"{_current.Value}\"");
                }
                var ts = new TimestampVal { Pos = pos, Value = ToDateTime(tsSeconds, tsNanos), Raw = _current.Value };
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
        if (_depth >= _maxDepth)
        {
            throw new PxfException(_current.Pos, $"nesting depth exceeds MaxNestingDepth={_maxDepth}");
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
        if (_depth >= _maxDepth)
        {
            throw new PxfException(_current.Pos, $"nesting depth exceeds MaxNestingDepth={_maxDepth}");
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
            if (_current.Kind == TokenKind.AT_TYPE)
            {
                entries.Add(ParseTypeEntry(first: entries.Count == 0));
                continue;
            }
            entries.Add(ParseEntry());
        }
        if (_current.Kind != TokenKind.RBRACE)
        {
            throw new PxfException(_current.Pos, $"expected '}}', got {_current.Kind}");
        }
        Advance();
        return entries;
    }

    private TypeEntry ParseTypeEntry(bool first)
    {
        var leading = FlushComments();
        var pos = _current.Pos;
        if (!first) throw new PxfException(pos, Decoder.TypeEntryMisplaced);
        Advance();
        if (_current.Kind != TokenKind.EQUALS)
        {
            throw new PxfException(_current.Pos, $"expected '=' after @type, got {_current.Kind}");
        }
        Advance();
        if (_current.Kind != TokenKind.STRING)
        {
            throw new PxfException(_current.Pos, $"expected a string type URL after @type =, got {_current.Kind}");
        }
        string url = _current.Value;
        Advance();
        return new TypeEntry { Pos = pos, Url = url, LeadingComments = leading };
    }

    // DateTime stops at year 1; a timestamp before it keeps its Raw spelling.
    private static DateTime ToDateTime(long seconds, int nanos)
    {
        try
        {
            return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanos / 100);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    internal static PxfException NonNameAssignment(Position pos, TokenKind kind, string key) =>
        new(pos, $"field assignment with '=' requires an identifier or string key, got {kind} (\"{key}\"); use ':' for map entries");

    internal static PxfException NonNameBlock(Position pos, TokenKind kind, string key) =>
        new(pos, $"submessage block requires an identifier or string key, got {kind} (\"{key}\")");

    internal static PxfException TopLevelMapEntry(Position pos) =>
        new(pos, "map entry (':' form) is only allowed inside a '{ … }' block; use '=' for top-level field assignments");
}
