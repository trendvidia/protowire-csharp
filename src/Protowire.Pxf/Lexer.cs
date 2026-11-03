// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Text;

namespace Protowire.Pxf;

internal class Lexer
{
    // HARDENING.md § UTF-8: a proto3 string is a sequence of valid UTF-8
    // bytes. PXF \xHH and \NNN byte escapes can produce invalid sequences;
    // the lexer assembles the literal as bytes and decodes strictly so the
    // U+FFFD substitution path of the default Encoding.UTF8 cannot fire.
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly string _input;
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    private readonly int _maxBytesLiteral;

    public Lexer(string input, int maxBytesLiteral = 64 << 20)
    {
        _input = input;
        _maxBytesLiteral = maxBytesLiteral;
    }

    /// <summary>
    /// The precision suffix of the last INT or FLOAT token, as written ("53"
    /// for 0.1p53), or "" for none (draft -01 § PXF Annotations). Read it only
    /// while that token is current.
    /// </summary>
    internal string Prec { get; private set; } = "";

    /// <summary>The raw input the lexer is scanning.</summary>
    internal string Input => _input;

    /// <summary>Current byte offset in <see cref="Input"/>.</summary>
    internal int Pos => _pos;

    /// <summary>Opaque lexer state snapshot for one-token lookahead.</summary>
    internal readonly record struct State(int Pos, int Line, int Col, string Prec);

    /// <summary>Captures the current lexer position for later restore.</summary>
    internal State Save() => new(_pos, _line, _col, Prec);

    /// <summary>Restores a previously-captured lexer position.</summary>
    internal void Restore(State s)
    {
        _pos = s.Pos;
        _line = s.Line;
        _col = s.Col;
        Prec = s.Prec;
    }

    /// <summary>
    /// Repositions the scanner to absolute offset <paramref name="target"/>.
    /// Used by <c>parseProtoDirective</c> after slicing a brace-bounded body
    /// out of the input directly: the interior is protobuf source rather
    /// than PXF, so the lexer must skip past it. Walks the input one byte
    /// at a time so line/col remain accurate.
    /// </summary>
    internal void RepositionTo(int target)
    {
        if (target < _pos)
        {
            throw new InvalidOperationException(
                $"lexer cannot reposition backwards (have {_pos}, want {target})");
        }
        while (_pos < target && _pos < _input.Length)
        {
            Advance();
        }
    }

    private char Peek() => _pos >= _input.Length ? '\0' : _input[_pos];

    private char PeekAt(int offset)
    {
        int i = _pos + offset;
        return i >= _input.Length ? '\0' : _input[i];
    }

    private char Advance()
    {
        if (_pos >= _input.Length) return '\0';
        char ch = _input[_pos++];
        if (ch == '\n')
        {
            _line++;
            _col = 1;
        }
        else
        {
            _col++;
        }
        return ch;
    }

    private Position CurrentPos => new(_line, _col, _pos);

    private void SkipSpaces()
    {
        while (_pos < _input.Length)
        {
            char ch = _input[_pos];
            if (ch == ' ' || ch == '\t' || ch == '\r')
            {
                Advance();
            }
            else
            {
                break;
            }
        }
    }

    public Token Next()
    {
        SkipSpaces();
        if (_pos >= _input.Length)
        {
            return new Token(TokenKind.EOF, string.Empty, CurrentPos);
        }

        var pos = CurrentPos;
        char ch = Peek();

        switch (ch)
        {
            case '\n':
                Advance();
                return new Token(TokenKind.NEWLINE, "\n", pos);

            case '#':
                return LexLineComment(pos);
            case '/' when PeekAt(1) == '/':
                return LexLineComment(pos);
            case '/' when PeekAt(1) == '*':
                return LexBlockComment(pos);

            case '"':
                if (PeekAt(1) == '"' && PeekAt(2) == '"')
                {
                    return LexTripleString(pos);
                }
                return LexString(pos);
            case 'b' when PeekAt(1) == '"':
                return LexBytes(pos);

            case '{':
                Advance();
                return new Token(TokenKind.LBRACE, "{", pos);
            case '}':
                Advance();
                return new Token(TokenKind.RBRACE, "}", pos);
            case '[':
                Advance();
                return new Token(TokenKind.LBRACKET, "[", pos);
            case ']':
                Advance();
                return new Token(TokenKind.RBRACKET, "]", pos);
            case '(':
                Advance();
                return new Token(TokenKind.LPAREN, "(", pos);
            case ')':
                Advance();
                return new Token(TokenKind.RPAREN, ")", pos);
            case '=':
                Advance();
                return new Token(TokenKind.EQUALS, "=", pos);
            case ':':
                Advance();
                return new Token(TokenKind.COLON, ":", pos);
            case ',':
                Advance();
                return new Token(TokenKind.COMMA, ",", pos);
            case '@':
                return LexDirective(pos);

            case '-':
            case '+':
            case var _ when char.IsAsciiDigit(ch):
                return LexNumber(pos);

            case var _ when IsIdentStart(ch):
                return LexIdent(pos);

            default:
                return LexIllegal(pos);
        }
    }

    private Token LexIllegal(Position pos)
    {
        char ch = Advance();
        if (char.IsHighSurrogate(ch) && char.IsLowSurrogate(Peek()))
        {
            return new Token(TokenKind.ILLEGAL, $"unexpected character '{ch}{Advance()}'", pos);
        }
        return new Token(TokenKind.ILLEGAL, $"unexpected character '{ch}'", pos);
    }

    private Token LexLineComment(Position pos)
    {
        int start = _pos;
        while (_pos < _input.Length && _input[_pos] != '\n')
        {
            Advance();
        }
        return new Token(TokenKind.COMMENT, _input[start.._pos], pos);
    }

    private Token LexBlockComment(Position pos)
    {
        int start = _pos;
        Advance(); // /
        Advance(); // *
        while (_pos + 1 < _input.Length)
        {
            if (_input[_pos] == '*' && _input[_pos + 1] == '/')
            {
                Advance(); // *
                Advance(); // /
                return new Token(TokenKind.COMMENT, _input[start.._pos], pos);
            }
            Advance();
        }
        return new Token(TokenKind.ILLEGAL, "unterminated block comment", pos);
    }

    private Token LexString(Position pos)
    {
        Advance(); // opening "
        // Assemble as bytes (not UTF-16 chars) so \xHH / \NNN escapes preserve
        // their raw byte semantics. A StringBuilder would silently lift each
        // byte to a UTF-16 code unit and re-encode it as valid 2-byte UTF-8 on
        // ToString — masking byte sequences that were never valid UTF-8 to
        // begin with. See HARDENING.md § UTF-8.
        var buf = new List<byte>();
        Span<char> charBuf = stackalloc char[2];
        while (_pos < _input.Length)
        {
            if (Peek() == '\n')
            {
                // A simple string holds no raw line feed (draft § String Literals).
                return new Token(TokenKind.ILLEGAL,
                    "newline in string literal: write it as \\n, or use a \"\"\"triple-quoted\"\"\" string", pos);
            }
            char ch = Advance();
            if (ch == '"')
            {
                try
                {
                    var s = StrictUtf8.GetString(buf.ToArray());
                    return new Token(TokenKind.STRING, s, pos);
                }
                catch (DecoderFallbackException)
                {
                    return new Token(TokenKind.ILLEGAL,
                        "invalid UTF-8 in string literal (use b\"…\" for raw bytes)", pos);
                }
            }
            if (ch != '\\')
            {
                charBuf[0] = ch;
                int charLen = 1;
                if (char.IsHighSurrogate(ch) && _pos < _input.Length && char.IsLowSurrogate(_input[_pos]))
                {
                    charBuf[1] = Advance();
                    charLen = 2;
                }
                AppendChars(buf, charBuf[..charLen]);
                continue;
            }
            if (_pos >= _input.Length)
            {
                return new Token(TokenKind.ILLEGAL, "unterminated escape sequence", pos);
            }
            char esc = Advance();
            switch (esc)
            {
                case '"' or '\\' or '\'' or '?':
                    buf.Add((byte)esc); break;
                case 'a': buf.Add(0x07); break;
                case 'b': buf.Add(0x08); break;
                case 'f': buf.Add(0x0C); break;
                case 'n': buf.Add((byte)'\n'); break;
                case 'r': buf.Add((byte)'\r'); break;
                case 't': buf.Add((byte)'\t'); break;
                case 'v': buf.Add(0x0B); break;
                case 'x':
                {
                    if (ReadHexByte() is not int b)
                    {
                        return new Token(TokenKind.ILLEGAL, "invalid \\x escape: expected 2 hex digits", pos);
                    }
                    buf.Add((byte)b);
                    break;
                }
                case >= '0' and <= '3':
                {
                    if (ReadOctRest(esc) is not int b)
                    {
                        return new Token(TokenKind.ILLEGAL, "invalid octal escape: expected 3 octal digits", pos);
                    }
                    buf.Add((byte)b);
                    break;
                }
                case 'u':
                {
                    if (ReadHexRune(4) is not int r || !IsValidRune(r))
                    {
                        return new Token(TokenKind.ILLEGAL,
                            "invalid \\u escape: expected 4 hex digits forming a valid codepoint", pos);
                    }
                    AppendRune(buf, r);
                    break;
                }
                case 'U':
                {
                    if (ReadHexRune(8) is not int r || !IsValidRune(r))
                    {
                        return new Token(TokenKind.ILLEGAL,
                            "invalid \\U escape: expected 8 hex digits forming a valid codepoint", pos);
                    }
                    AppendRune(buf, r);
                    break;
                }
                default:
                    return new Token(TokenKind.ILLEGAL, $"unknown escape sequence \\{esc}", pos);
            }
        }
        return new Token(TokenKind.ILLEGAL, "unterminated string", pos);
    }

    /// <summary>Encodes the given UTF-16 chars as UTF-8 bytes into <paramref name="buf"/>.</summary>
    private static void AppendChars(List<byte> buf, ReadOnlySpan<char> chars)
    {
        int n = StrictUtf8.GetByteCount(chars);
        Span<byte> bytes = stackalloc byte[n];
        StrictUtf8.GetBytes(chars, bytes);
        for (int i = 0; i < n; i++) buf.Add(bytes[i]);
    }

    /// <summary>Encodes the given codepoint as UTF-8 bytes into <paramref name="buf"/>.</summary>
    private static void AppendRune(List<byte> buf, int rune)
    {
        var s = char.ConvertFromUtf32(rune);
        AppendChars(buf, s.AsSpan());
    }

    /// <summary>Reads exactly 2 hex digits and returns the assembled byte, or null on error.</summary>
    private int? ReadHexByte()
    {
        if (_pos + 1 >= _input.Length) return null;
        if (HexVal(_input[_pos]) is not int hi) return null;
        if (HexVal(_input[_pos + 1]) is not int lo) return null;
        Advance(); Advance();
        return (hi << 4) | lo;
    }

    /// <summary>Reads exactly N hex digits and returns the assembled codepoint, or null on error.</summary>
    private int? ReadHexRune(int n)
    {
        if (_pos + n > _input.Length) return null;
        int r = 0;
        for (int i = 0; i < n; i++)
        {
            if (HexVal(_input[_pos]) is not int v) return null;
            r = (r << 4) | v;
            Advance();
        }
        return r;
    }

    /// <summary>
    /// Reads two more octal digits after the leading one already consumed
    /// (\nnn — exactly 3 octal digits total). The caller has restricted
    /// `first` to '0'-'3' so the value fits in a byte.
    /// </summary>
    private int? ReadOctRest(char first)
    {
        if (_pos + 1 >= _input.Length) return null;
        if (OctVal(_input[_pos]) is not int d1) return null;
        if (OctVal(_input[_pos + 1]) is not int d2) return null;
        Advance(); Advance();
        return ((first - '0') << 6) | (d1 << 3) | d2;
    }

    private static int? HexVal(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'a' and <= 'f' => ch - 'a' + 10,
        >= 'A' and <= 'F' => ch - 'A' + 10,
        _ => null,
    };

    private static int? OctVal(char ch) => ch is >= '0' and <= '7' ? ch - '0' : null;

    /// <summary>Mirrors Go's utf8.ValidRune: rejects > U+10FFFF and the surrogate range.</summary>
    private static bool IsValidRune(int r) => r is >= 0 and <= 0x10FFFF and (< 0xD800 or > 0xDFFF);

    private Token LexTripleString(Position pos)
    {
        Advance(); // "
        Advance(); // "
        Advance(); // "
        int start = _pos;
        while (_pos + 2 < _input.Length)
        {
            if (_input[_pos] == '"' && _input[_pos + 1] == '"' && _input[_pos + 2] == '"')
            {
                string raw = _input[start.._pos];
                Advance(); // "
                Advance(); // "
                Advance(); // "
                return new Token(TokenKind.STRING, Dedent(raw), pos);
            }
            Advance();
        }
        return new Token(TokenKind.ILLEGAL, "unterminated triple-quoted string", pos);
    }

    private static string Dedent(string s)
    {
        if (s.Length > 0 && s[0] == '\n')
        {
            s = s[1..];
        }
        var lines = s.Split('\n');
        if (lines.Length == 0) return string.Empty;

        string last = lines[^1];
        if (string.IsNullOrWhiteSpace(last))
        {
            string indent = last;
            var resultLines = lines[..^1];
            for (int i = 0; i < resultLines.Length; i++)
            {
                if (resultLines[i].StartsWith(indent))
                {
                    resultLines[i] = resultLines[i][indent.Length..];
                }
            }
            return string.Join("\n", resultLines);
        }
        return string.Join("\n", lines);
    }

    private Token LexBytes(Position pos)
    {
        Advance(); // b
        if (_pos >= _input.Length || _input[_pos] != '"')
        {
            return new Token(TokenKind.ILLEGAL, "expected '\"' after b", pos);
        }
        Advance(); // opening "
        int start = _pos;
        while (_pos < _input.Length)
        {
            char ch = _input[_pos];
            // Judged from the length, three bytes per four characters, so the
            // literal is neither scanned to its end nor decoded to refuse it.
            if ((_pos - start) / 4 * 3 > _maxBytesLiteral)
            {
                return new Token(TokenKind.ILLEGAL,
                    $"bytes literal decodes to more than MaxBytesLiteralLength={_maxBytesLiteral} bytes", pos);
            }
            if (ch == '"')
            {
                string raw = _input[start.._pos];
                Advance(); // closing "
                if (!Base64Lenient.TryDecode(raw, out _))
                {
                    return new Token(TokenKind.ILLEGAL, "invalid base64 in bytes literal", pos);
                }
                return new Token(TokenKind.BYTES, raw, pos);
            }
            if (ch == '\n')
            {
                return new Token(TokenKind.ILLEGAL, "unterminated bytes literal", pos);
            }
            Advance();
        }
        return new Token(TokenKind.ILLEGAL, "unterminated bytes literal", pos);
    }

    private Token LexDirective(Position pos)
    {
        Advance(); // @
        int start = _pos;
        while (_pos < _input.Length && IsIdentPart(_input[_pos]))
        {
            Advance();
        }
        string name = _input[start.._pos];
        if (name.Length == 0)
        {
            return new Token(TokenKind.ILLEGAL, "\"@\" must be followed by a directive name", pos);
        }
        return name switch
        {
            "type" => new Token(TokenKind.AT_TYPE, "@type", pos),
            "dataset" => new Token(TokenKind.AT_DATASET, "@dataset", pos),
            "proto" => new Token(TokenKind.AT_PROTO, "@proto", pos),
            _ => new Token(TokenKind.AT_DIRECTIVE, name, pos),
        };
    }

    private Token LexNumber(Position pos)
    {
        int start = _pos;
        bool neg = false;
        if (Peek() is '-' or '+')
        {
            char sign = Peek();
            Advance();
            // "+inf" and "-inf" are the only signed identifier literals.
            if (HasIdent("inf"))
            {
                Advance(); Advance(); Advance();
                Prec = "";
                return new Token(TokenKind.FLOAT, _input[start.._pos], pos);
            }
            if (sign == '+')
            {
                return new Token(TokenKind.ILLEGAL, "\"+\" is valid only in \"+inf\"", pos);
            }
            if (_pos >= _input.Length || !char.IsAsciiDigit(Peek()))
            {
                return new Token(TokenKind.ILLEGAL, "\"-\" must be followed by a digit or \"inf\"", pos);
            }
            neg = true;
        }

        int digitStart = _pos;
        while (_pos < _input.Length && char.IsAsciiDigit(Peek()))
        {
            Advance();
        }
        int digitCount = _pos - digitStart;

        // Timestamp: exactly 4 digits followed by '-', only non-negative
        if (!neg && digitCount == 4 && _pos < _input.Length && Peek() == '-')
        {
            return LexTimestamp(pos, start);
        }
        // A fraction is '.' and at least one digit. Floats and durations both
        // take one, so it is consumed first and what follows tells them apart.
        bool frac = false;
        if (Peek() == '.' && char.IsAsciiDigit(PeekAt(1)))
        {
            frac = true;
            Advance();
            while (_pos < _input.Length && char.IsAsciiDigit(Peek()))
            {
                Advance();
            }
        }
        // Before the float branch, so "1.5ms" is one token and not FLOAT then IDENT.
        if (_pos < _input.Length && IsDurationUnit(Peek()))
        {
            return LexDuration(pos, start);
        }
        // Float: fraction, or 'e'/'E' exponent, or a bare trailing '.'
        if (frac || (_pos < _input.Length && Peek() is '.' or 'e' or 'E'))
        {
            return LexFloat(pos, start);
        }

        string value = _input[start.._pos];
        LexPrecision();
        return new Token(TokenKind.INT, value, pos);
    }

    private Token LexFloat(Position pos, int start)
    {
        if (Peek() == '.')
        {
            Advance();
            while (_pos < _input.Length && char.IsAsciiDigit(Peek()))
            {
                Advance();
            }
        }
        if (_pos < _input.Length && Peek() is 'e' or 'E')
        {
            Advance();
            if (_pos < _input.Length && Peek() is '+' or '-')
            {
                Advance();
            }
            while (_pos < _input.Length && char.IsAsciiDigit(Peek()))
            {
                Advance();
            }
        }
        string value = _input[start.._pos];
        LexPrecision();
        return new Token(TokenKind.FLOAT, value, pos);
    }

    // A number's precision suffix, "p" and one or more digits. Any field but a
    // pxf.BigFloat rejects one; that is the decoder's call, not the lexer's.
    private void LexPrecision()
    {
        Prec = "";
        if (Peek() != 'p' || !char.IsAsciiDigit(PeekAt(1))) return;
        Advance();
        int start = _pos;
        while (_pos < _input.Length && char.IsAsciiDigit(Peek()))
        {
            Advance();
        }
        Prec = _input[start.._pos];
    }

    private bool HasIdent(string word)
    {
        int end = _pos + word.Length;
        if (end > _input.Length || string.CompareOrdinal(_input, _pos, word, 0, word.Length) != 0) return false;
        return end == _input.Length || !IsIdentPart(_input[end]);
    }

    private Token LexTimestamp(Position pos, int start)
    {
        while (_pos < _input.Length)
        {
            char ch = Peek();
            if (ch is ' ' or '\n' or '\t' or '\r' or ',' or ']' or '}' or ')' or '#')
            {
                break;
            }
            if (ch == '/' && (PeekAt(1) == '/' || PeekAt(1) == '*'))
            {
                break;
            }
            Advance();
        }
        string raw = _input[start.._pos];
        if (!Rfc3339.TryParse(raw, out _, out _))
        {
            return new Token(TokenKind.ILLEGAL, "invalid timestamp: " + raw, pos);
        }
        return new Token(TokenKind.TIMESTAMP, raw, pos);
    }

    // The scan is loose, so "5min" is reported as one invalid duration and not
    // as a duration and an identifier; TryParseNanos decides what it was.
    private Token LexDuration(Position pos, int start)
    {
        while (_pos < _input.Length)
        {
            char ch = Peek();
            bool part = char.IsAsciiDigit(ch) || ch is >= 'a' and <= 'z' or MicroSign
                || (ch == '.' && char.IsAsciiDigit(PeekAt(1)));
            if (!part) break;
            Advance();
        }
        string raw = _input[start.._pos];
        if (!DurationParser.TryParseNanos(raw, out _))
        {
            return new Token(TokenKind.ILLEGAL, "invalid duration: " + raw, pos);
        }
        return new Token(TokenKind.DURATION, raw, pos);
    }

    private Token LexIdent(Position pos)
    {
        int start = _pos;
        while (_pos < _input.Length && IsIdentPart(_input[_pos]))
        {
            Advance();
        }
        string val = _input[start.._pos];
        if (val == "true" || val == "false")
        {
            return new Token(TokenKind.BOOL, val, pos);
        }
        if (val == "null")
        {
            return new Token(TokenKind.NULL, val, pos);
        }
        return new Token(TokenKind.IDENT, val, pos);
    }

    private const char MicroSign = '\u00B5'; // U+03BC GREEK SMALL LETTER MU is not a unit
    private static bool IsIdentStart(char ch) => char.IsAsciiLetter(ch) || ch == '_';
    private static bool IsIdentPart(char ch) => IsIdentStart(ch) || char.IsAsciiDigit(ch) || ch == '.';
    private static bool IsDurationUnit(char ch) => ch is 'h' or 'm' or 's' or 'n' or 'u' or MicroSign;
}
