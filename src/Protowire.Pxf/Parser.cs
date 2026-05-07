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

        if (_current.Kind == TokenKind.AT_TYPE)
        {
            Advance();
            if (_current.Kind != TokenKind.IDENT)
            {
                throw new PxfException(_current.Pos, $"expected type name after @type, got {_current.Kind}");
            }
            typeUrl = _current.Value;
            Advance();
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
            LeadingComments = leadingComments,
            Entries = entries,
        };
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
