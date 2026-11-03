// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Unicode;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

// Streaming consumption for the @dataset directive (draft §3.4.4). Parser.Parse
// holds a whole document and every row in memory; a DatasetReader pulls bytes
// from a Stream as it needs them and yields one row per Next call, holding
// about one row at a time. Mirrors dataset_stream.go in protowire-go.
//
// The reader decides only where a row ends. It finds the boundary with the
// lexer Parse uses, which says itself when it looked past the end of what has
// been read (Lexer.SawEnd), and it reads the row with the parser Parse uses, at
// the row's document position. So a row, and an error, are the ones Parse
// gives for the same input, however the input arrives.

/// <summary>
/// Reads the rows of one <c>@dataset</c> directive from a <see cref="Stream"/>,
/// one at a time (draft §3.4.4 "Streaming consumption"), for a table too large
/// to read with <see cref="Parser.Parse(string)"/>.
/// </summary>
/// <remarks>
/// <para>
/// A reader is positioned at the first row once it is created. Call
/// <see cref="Next"/> until it returns null, or <see cref="Scan"/> until it
/// returns false. A row comes back exactly as <see cref="Parser.Parse(string)"/>
/// reads it, position included, and a malformed row, a row cut short by the end
/// of the stream included, throws the <see cref="PxfException"/> Parse throws
/// for the same input. Rows are checked as they are read, so rows before a
/// broken one are returned first.
/// </para>
/// <para>
/// The bytes held while a row boundary is sought are capped at the limits'
/// <see cref="PxfLimits.MaxMessageSize"/>, and the header must fit in 64 KiB.
/// For a document with several tables, read each to its end and create the
/// next reader on <see cref="Tail"/>. A reader is not safe for concurrent use.
/// </para>
/// </remarks>
public sealed class DatasetReader
{
    // The budget for the header, everything up to the ')' of the column list.
    // Real headers are tiny; the cap stops a reader pointed at a large document
    // with no @dataset from holding all of it while it looks for one.
    internal const int HeaderMaxBytes = 64 * 1024;

    // How many bytes a read asks for, at least. A row that spans reads asks for
    // as many again as it already holds, so a long row costs a few reads of a
    // stream that fills them rather than one per 4 KiB.
    internal const int PullSize = 4096;

    private readonly Stream _src;
    private readonly PxfLimits _lim;
    private readonly Decoder _binder;
    private readonly bool _chained;

    // The text read and not yet consumed is _buf[_off.._len]. Indices below are
    // into _buf; the consumed prefix is dropped when a read needs the room.
    // _base is the document position of _buf[_off]. The lexer reads a string,
    // which Text builds only when a scan or a parse needs one, so a read that
    // cannot end the token being waited on costs only its own length.
    private char[] _buf = new char[PullSize];
    private int _len;
    private int _off;
    private string? _text;
    private Position _base = new(1, 1, 0);
    // UTF-8 bytes of _buf[_off.._len], and bytes read that end in the middle
    // of a character, which wait for the rest of it.
    private long _pendingBytes;
    private byte[] _readBuf = new byte[PullSize];
    private int _carryLen;
    private bool _srcEof;

    private bool _finished;
    private ExceptionDispatchInfo? _err;

    // The scans resume where the previous read left them: _scanPos is the start
    // of the first token not yet known to be complete, _rowStart the current
    // row's '(' or -1 between rows.
    private int _scanPos;
    private int _rowStart = -1;
    private Lexer? _scanLex;

    // A token the input read so far cuts (waiting) is not lexed again until
    // the characters read since could end it (MayEndToken): _waitAt is its
    // start, _waitFrom the length already checked. Without this, one long
    // literal would be lexed again on every read.
    private bool _waiting;
    private int _waitAt;
    private int _waitFrom;

    // The characters the scans have lexed, for the tests that hold them linear
    // in the input however it is read.
    internal long Scanned { get; private set; }

    // The header scan: whether it has read @dataset, how far into an @proto
    // directive (1 after @proto, 2 after its type name), and the '{' of an
    // @proto body it is matching, which Parse skips rather than lexes.
    private bool _inDataset;
    private int _proto;
    private int _bodyOpen = -1;

    private string _type = string.Empty;
    private List<string> _columns = [];
    private List<Directive> _dirs = [];

    internal DatasetReader(Stream source, PxfLimits limits, TypeRegistry registry)
    {
        _src = source;
        _lim = limits;
        // A row's bytes were capped as they were read, and the body it is
        // bound as may be written wider, so binding skips MaxMessageSize.
        _binder = new Decoder { Limits = limits with { MaxMessageSize = int.MaxValue }, Registry = registry };
        if (source is DatasetTail tail)
        {
            _base = tail.At;
            _chained = true;
        }
        ReadHeader();
    }

    /// <summary>
    /// Reads the leading directives and the <c>@dataset TYPE ( cols )</c>
    /// header from <paramref name="source"/> under the draft's default limits.
    /// See <see cref="Decoder.NewDatasetReader"/>.
    /// </summary>
    public static DatasetReader Create(Stream source) => new Decoder().NewDatasetReader(source);

    /// <summary>The row message type the header names, or "" when it names none.</summary>
    public string Type => _type;

    /// <summary>The header's column paths, in source order.</summary>
    public IReadOnlyList<string> Columns => _columns;

    /// <summary>
    /// The <c>@&lt;name&gt;</c> directives before the <c>@dataset</c> header,
    /// as Parse reads them (not <c>@type</c>, <c>@proto</c> or
    /// <c>@dataset</c>).
    /// </summary>
    public IReadOnlyList<Directive> Directives => _dirs;

    /// <summary>
    /// Reads the next row. Returns null when the table's rows are exhausted: at
    /// the end of the stream, or before the next directive, which a reader
    /// created on <see cref="Tail"/> reads.
    /// </summary>
    /// <exception cref="PxfException">
    /// What follows is not a well-formed row: the error Parse reports for the
    /// same input, at the same position. The reader then throws the same
    /// exception on every call, as it does a read failure.
    /// </exception>
    public DatasetRow? Next()
    {
        _err?.Throw();
        if (_finished) return null;
        try
        {
            return NextRow();
        }
        catch (Exception e)
        {
            _err = ExceptionDispatchInfo.Capture(e);
            throw;
        }
    }

    /// <summary>
    /// Reads the next row and binds it to <paramref name="msg"/> as
    /// <see cref="Decoder.BindRow"/> does, under the reader's limits except
    /// MaxMessageSize and with its registry. Returns false when the rows are
    /// exhausted. Bind each row into a new message.
    /// </summary>
    /// <exception cref="PxfException">The row is malformed, or does not bind.</exception>
    public bool Scan(IMessage msg)
    {
        var row = Next();
        if (row == null) return false;
        _binder.BindRow(msg, _columns, row);
        return true;
    }

    /// <summary>
    /// A stream of what this reader read and did not consume, followed by the
    /// rest of the source. Call it once <see cref="Next"/> has returned null,
    /// and create the next table's reader on it; that reader reports document
    /// positions, as Parse does.
    /// </summary>
    public Stream Tail()
    {
        var rest = Encoding.UTF8.GetBytes(_buf, _off, _len - _off);
        var held = new byte[rest.Length + _carryLen];
        rest.CopyTo(held, 0);
        Array.Copy(_readBuf, 0, held, rest.Length, _carryLen);
        return new DatasetTail(held, _src, _base);
    }

    private DatasetRow? NextRow()
    {
        if (TryWholeRow(out var whole, out int wholeEnd))
        {
            ConsumeTo(wholeEnd);
            return whole;
        }
        for (;;)
        {
            var (kind, start, end) = ScanRow();
            switch (kind)
            {
                case RowScan.Row:
                    {
                        // Consumed whatever the row says, as the reference does;
                        // an error is sticky either way.
                        var at = PosAt(start);
                        ConsumeTo(end);
                        return ParseRow(start, end, at);
                    }
                case RowScan.Bad:
                    // Parse the rest of the buffer, not just through the token
                    // that stopped the scan: the lexer may refuse a token before
                    // its end (an oversized bytes literal), and cut there, it
                    // would read as unterminated instead.
                    throw RowError(start);
                case RowScan.End:
                    // The newlines and comments before the directive stay, so
                    // Tail carries them to the next reader, as Parse gives them
                    // to that directive.
                    _finished = true;
                    return null;
                case RowScan.NotRow:
                    // Any token where a row could start, one the lexer refuses
                    // included, is an entry beside the dataset, as Parse and
                    // protowire-go's reader report it (#96).
                    throw new PxfException(PosAt(start), Parser.DatasetBesideEntries);
            }
            // NeedMore. At the end of the source only spaces and comments may
            // remain; anything else is a row cut short.
            if (_srcEof)
            {
                if (start >= 0) throw RowError(start);
                ConsumeTo(_len);
                _finished = true;
                return null;
            }
            Pull();
        }
    }

    // The fast path for a row already read: the row parser runs on the buffer
    // directly, so the row is lexed once. If it reads a whole row, every token
    // in it ended before its ')', so it is the row Parse reads. Anything else
    // (a row cut by a read, a malformed row, no row) falls to ScanRow, which
    // finds the boundary with the lexer. Next calls it only between rows.
    private bool TryWholeRow(out DatasetRow row, out int end)
    {
        row = null!;
        end = 0;
        var at = PosAt(_scanPos);
        try
        {
            var p = Parser.At(Text, _scanPos, _len, at, _lim);
            if (p.Current.Kind != TokenKind.LPAREN) return false;
            (row, int endOffset) = p.ParseRowHere(_columns.Count);
            end = _scanPos + (endOffset - at.Offset);
            return true;
        }
        catch (PxfException)
        {
            return false;
        }
    }

    // Parses _buf[start..end] as one row with the parser Parse uses, at its
    // document position.
    private DatasetRow ParseRow(int start, int end, Position at)
    {
        var p = Parser.At(Text, start, end, at, _lim);
        if (p.Current.Kind != TokenKind.LPAREN)
        {
            throw new PxfException(p.Current.Pos, Parser.DatasetBesideEntries);
        }
        return p.ParseRowHere(_columns.Count).Item1;
    }

    // Why the row at _buf[start] is malformed. The scan has seen every
    // token from its '(' to the one that stopped it, and no ')' that closes it,
    // so the row parser fails on the rest of the buffer at the token where
    // Parse fails on the whole document: the tokens before it are the same.
    private Exception RowError(int start)
    {
        try
        {
            ParseRow(start, _len, PosAt(start));
        }
        catch (PxfException e)
        {
            return e;
        }
        // Unreachable: the text has no ')' that closes the row.
        return new InvalidOperationException("pxf: @dataset row has no closing ')'");
    }

    private enum RowScan { NeedMore, Row, Bad, End, NotRow }

    // Lexes the token at _buf[from..]. ok is false when more input could still
    // change it (the lexer looked past what has been read), or there is no
    // token yet. Offsets are indices into _buf.
    private (Token Tok, int End, bool Ok) NextToken(int from)
    {
        if (_scanLex == null || !ReferenceEquals(_scanLex.Input, _text))
        {
            _scanLex = new Lexer(Text, 0, _len, new Position(1, 1, 0), _lim.MaxBytesLiteralLength);
        }
        _scanLex.ResetTo(from);
        var tok = _scanLex.Next();
        Scanned += _scanLex.Pos - from;
        bool ok = _srcEof || (tok.Kind != TokenKind.EOF && !_scanLex.SawEnd);
        return (tok, _scanLex.Pos, ok);
    }

    // Finds the next row with the lexer, resuming at _scanPos, so a token known
    // complete is not lexed again. Between rows it skips newlines and comments;
    // '(' starts a row, a directive ends the table, anything else is not a row.
    // Inside a row it stops at the ')' that closes it, at a '(' (no value holds
    // one), or at a token the lexer refuses.
    private (RowScan Kind, int Start, int End) ScanRow()
    {
        if (!Resume()) return (RowScan.NeedMore, _rowStart, 0);
        for (;;)
        {
            var (tok, tokEnd, ok) = NextToken(_scanPos);
            if (!ok)
            {
                Wait(tok);
                return (RowScan.NeedMore, _rowStart, 0);
            }
            if (tok.Kind == TokenKind.EOF)
            {
                // Only at the end of the source (NextToken).
                return (RowScan.NeedMore, _rowStart, 0);
            }
            if (tok.Kind is TokenKind.NEWLINE or TokenKind.COMMENT)
            {
                _scanPos = tokEnd;
                continue;
            }
            int at = tok.Pos.Offset;
            if (_rowStart < 0)
            {
                switch (tok.Kind)
                {
                    case TokenKind.LPAREN:
                        _rowStart = at;
                        _scanPos = tokEnd;
                        continue;
                    case TokenKind.AT_TYPE or TokenKind.AT_DIRECTIVE or TokenKind.AT_DATASET or TokenKind.AT_PROTO:
                        return (RowScan.End, at, 0);
                    default:
                        return (RowScan.NotRow, at, 0);
                }
            }
            switch (tok.Kind)
            {
                case TokenKind.RPAREN:
                    {
                        int start = _rowStart;
                        _rowStart = -1;
                        _scanPos = tokEnd;
                        return (RowScan.Row, start, tokEnd);
                    }
                case TokenKind.LPAREN or TokenKind.ILLEGAL:
                    return (RowScan.Bad, _rowStart, tokEnd);
            }
            _scanPos = tokEnd;
        }
    }

    // Reads until the ')' that closes the column list, then parses everything
    // before it as a document with no rows yet, as Parse parses it, for the
    // type, the columns and the directives before them.
    private void ReadHeader()
    {
        for (;;)
        {
            var (end, found, bad) = ScanHeader();
            if (bad != null)
            {
                // The lexer refused a token, or an @proto body can never
                // close, before the header ended. Every token before it is
                // complete, so Parse fails at or before it.
                ParseHeader(_len);
                // Unreachable: Parse reads what the scan read.
                throw new PxfException(PosAt(bad.Pos.Offset), bad.Value);
            }
            if (found)
            {
                var doc = ParseHeader(end);
                if (doc.Datasets.Count == 0)
                {
                    // Unreachable: the scan found an @dataset.
                    throw new NoDatasetException();
                }
                var ds = doc.Datasets[0];
                _type = ds.Type;
                _columns = ds.Columns;
                if (_chained && doc.LeadingComments.Count > 0 && doc.Directives.Count > 0
                    && doc.Directives[0].Pos.Offset < ds.Pos.Offset)
                {
                    // A reader on Tail starts mid-document, where the comments
                    // before a directive are its own, as Parse files them; at a
                    // document's start they are the document's.
                    doc.Directives[0].LeadingComments.InsertRange(0, doc.LeadingComments);
                }
                _dirs = doc.Directives;
                ConsumeTo(end);
                return;
            }
            if (_srcEof)
            {
                // The input ended without a complete header. A malformed
                // document reports what Parse reports for it; only a
                // well-formed one is a document without a dataset.
                ParseHeader(_len);
                throw new NoDatasetException();
            }
            if (HeldBytes >= HeaderMaxBytes)
            {
                throw new PxfException(_base,
                    $"@dataset header exceeds {HeaderMaxBytes} bytes; check that the input begins with `@dataset TYPE (cols)`");
            }
            Pull();
        }
    }

    // Parses _buf[_off..end] as a document, at its document position.
    private Document ParseHeader(int end)
    {
        // A copy of its own, so an @proto or directive body is matched within it.
        string text = new(_buf, _off, end - _off);
        return Parser.At(text, 0, text.Length, _base, _lim).ParseDocumentHere();
    }

    // Finds the end of the first `@dataset TYPE ( cols )` header with the lexer:
    // the index just past the first ')' after an @dataset token. found is false
    // when the input read so far ends first; bad is a token the lexer refused,
    // or an @proto body that can never close, before the header ended.
    private (int End, bool Found, Token? Bad) ScanHeader()
    {
        if (_bodyOpen < 0 && !Resume()) return (0, false, null);
        for (;;)
        {
            if (_bodyOpen >= 0)
            {
                // Matched from its '{' again on each read: a header is small.
                int close = BraceScan.FindMatchingBrace(Text, _bodyOpen, out bool failed);
                if (close < 0)
                {
                    return failed
                        ? (0, false, new Token(TokenKind.ILLEGAL, "@proto: unmatched '{'", new Position(0, 0, _bodyOpen)))
                        : (0, false, null);
                }
                _bodyOpen = -1;
                _scanPos = close + 1;
                continue;
            }
            var (tok, tokEnd, ok) = NextToken(_scanPos);
            if (!ok)
            {
                Wait(tok);
                return (0, false, null);
            }
            if (tok.Kind == TokenKind.EOF) return (0, false, null);
            switch (tok.Kind)
            {
                case TokenKind.ILLEGAL:
                    return (0, false, tok);
                case TokenKind.NEWLINE or TokenKind.COMMENT:
                    // Parse skips these between @proto, its name and '{'.
                    break;
                case var _ when _inDataset:
                    if (tok.Kind == TokenKind.RPAREN)
                    {
                        _scanPos = tokEnd;
                        return (tokEnd, true, null);
                    }
                    break;
                case TokenKind.AT_DATASET:
                    _inDataset = true;
                    _proto = 0;
                    break;
                case TokenKind.AT_PROTO:
                    _proto = 1;
                    break;
                case TokenKind.IDENT when _proto == 1:
                    _proto = 2;
                    break;
                case TokenKind.LBRACE when _proto > 0:
                    _proto = 0;
                    _bodyOpen = tok.Pos.Offset;
                    break;
                default:
                    _proto = 0;
                    break;
            }
            _scanPos = tokEnd;
        }
    }

    private long HeldBytes => _pendingBytes + _carryLen;

    private string Text => _text ??= new string(_buf, 0, _len);

    // The document position of _buf[i].
    private Position PosAt(int i)
    {
        int line = _base.Line, col = _base.Column;
        for (int k = _off; k < i; k++)
        {
            if (_buf[k] == '\n')
            {
                line++;
                col = 1;
            }
            else
            {
                col++;
            }
        }
        return new Position(line, col, _base.Offset + (i - _off));
    }

    // Consumes _buf[_off..i].
    private void ConsumeTo(int i)
    {
        _base = PosAt(i);
        _pendingBytes -= Encoding.UTF8.GetByteCount(_buf.AsSpan(_off, i - _off));
        _off = i;
        if (_scanPos < i) _scanPos = i;
        if (_rowStart >= 0 && _rowStart < i) _rowStart = -1;
    }

    // Reads once from the source and appends what it returned. Bytes that end
    // in the middle of a character wait for the rest of it, so text decodes as
    // Encoding.UTF8 decodes the whole input, an invalid sequence included.
    //
    // The scans pull only when what is held has no boundary yet, so holding
    // MaxMessageSize bytes here is the error. A read never takes more than
    // fits under the cap, so the verdict does not depend on how large the
    // source's reads are.
    private void Pull()
    {
        if (_srcEof) return;
        if (HeldBytes >= _lim.MaxMessageSize)
        {
            throw new PxfException(_base,
                $"dataset input holds {HeldBytes} bytes without a row boundary, MaxMessageSize={_lim.MaxMessageSize}");
        }
        int want = (int)Math.Min(Math.Max(PullSize, HeldBytes), _lim.MaxMessageSize - HeldBytes);
        if (_readBuf.Length < _carryLen + want)
        {
            Array.Resize(ref _readBuf, _carryLen + want);
        }
        int read = _src.Read(_readBuf, _carryLen, want);
        if (read == 0) _srcEof = true;
        int total = _carryLen + read;
        if (_off > 0 && (_len + total > _buf.Length || _off >= _len / 2))
        {
            // Drop the consumed prefix rather than grow past it, or once it is
            // most of what Text would copy.
            Array.Copy(_buf, _off, _buf, 0, _len - _off);
            _len -= _off;
            _scanPos -= _off;
            if (_rowStart >= 0) _rowStart -= _off;
            if (_bodyOpen >= 0) _bodyOpen -= _off;
            _waitAt -= _off;
            _waitFrom -= _off;
            _off = 0;
            _text = null;
        }
        if (_len + total > _buf.Length)
        {
            Array.Resize(ref _buf, Math.Max(_len + total, _buf.Length * 2));
        }
        Utf8.ToUtf16(_readBuf.AsSpan(0, total), _buf.AsSpan(_len), out int bytesRead, out int written,
            replaceInvalidSequences: true, isFinalBlock: _srcEof);
        _carryLen = total - bytesRead;
        Array.Copy(_readBuf, bytesRead, _readBuf, 0, _carryLen);
        if (written > 0)
        {
            _pendingBytes += Encoding.UTF8.GetByteCount(_buf.AsSpan(_len, written));
            _len += written;
            _text = null;
        }
    }

    // Whether a scan may lex on: no token is waiting, the source has ended, or
    // the characters read since the waiting token was last lexed could end it.
    private bool Resume()
    {
        if (_waiting && !_srcEof && !MayEndToken())
        {
            _waitFrom = _len;
            return false;
        }
        _waiting = false;
        return true;
    }

    // Records tok, which NextToken could not decide, as the waiting token. The
    // scan resumes at its start: the spaces before it are decided.
    private void Wait(Token tok)
    {
        _waiting = true;
        _waitAt = tok.Pos.Offset;
        _waitFrom = _len;
        _scanPos = tok.Pos.Offset;
    }

    // Under this length a waiting token is lexed again whatever arrives: what
    // may still change a token this short is a character or two of the
    // lexer's lookahead (`""` opening `"""`, `b` opening `b"`, `/` opening a
    // comment, `-in` reading `-inf`, no token at all yet), and lexing a few
    // characters again costs nothing.
    private const int ShortWait = 8;

    // Whether the characters read since the waiting token was last lexed could
    // end it: a closing quote (not escaped by an odd run of backslashes) or a
    // newline for a simple string, `"""` for a triple-quoted one, a quote or a
    // newline for a bytes literal, or enough characters that the lexer refuses
    // it (MaxBytesLiteralLength), a newline for a line comment, `*/` for a
    // block comment, a character that ends a timestamp, and for any other
    // token one that cannot continue an identifier, a number or a duration.
    // Each of those does end the token, so a token is lexed again only once it
    // is short or decided. It decides only when the lexer looks again, never
    // what the token is; ')' always counts, so a complete row never waits for
    // input. Mirrors mayEndToken in protowire-go.
    private bool MayEndToken()
    {
        int at = _waitAt, from = _waitFrom;
        if (from - at < ShortWait) return true;
        var buf = _buf.AsSpan(0, _len);
        var tok = buf[at..];
        var fresh = buf[from..];
        if (tok.StartsWith("\"\"\""))
        {
            return buf[Math.Max(from - 2, at + 3)..].IndexOf("\"\"\"") >= 0;
        }
        if (tok[0] == '"')
        {
            for (int i = 0; i < fresh.Length; i++)
            {
                if (fresh[i] == '\n') return true; // a simple string cannot hold one
                if (fresh[i] != '"') continue;
                int n = 0;
                for (int j = from + i - 1; j > at && buf[j] == '\\'; j--) n++;
                if (n % 2 == 0) return true;
            }
            return false;
        }
        if (tok.StartsWith("b\""))
        {
            // The lexer judges the cap from the characters past the quote.
            return fresh.IndexOfAny('"', '\n') >= 0 || (long)(_len - (at + 2)) / 4 * 3 > _lim.MaxBytesLiteralLength;
        }
        if (tok[0] == '#' || tok.StartsWith("//"))
        {
            return fresh.IndexOf('\n') >= 0;
        }
        if (tok.StartsWith("/*"))
        {
            return buf[Math.Max(from - 1, at + 2)..].IndexOf("*/") >= 0;
        }
        if (char.IsAsciiDigit(tok[0]) && char.IsAsciiDigit(tok[1]) && char.IsAsciiDigit(tok[2])
            && char.IsAsciiDigit(tok[3]) && tok[4] == '-')
        {
            // A timestamp runs to a character that ends it, or to `//` or
            // `/*`, whose '/' may be the last character checked before.
            for (int i = Math.Max(from - 1, at); i < buf.Length; i++)
            {
                char ch = buf[i];
                if (ch is ' ' or '\n' or '\t' or '\r' or ',' or ']' or '}' or ')' or '#') return true;
                if (ch == '/' && i + 1 < buf.Length && buf[i + 1] is '/' or '*') return true;
            }
            return false;
        }
        foreach (char ch in fresh)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or '+' or '-' || ch >= 0x80)) return true;
        }
        return false;
    }

    // The stream Tail returns. It carries the document position of its first
    // byte, so the next table's rows and errors carry Parse's positions.
    private sealed class DatasetTail(byte[] held, Stream source, Pxf.Position at) : Stream
    {
        private int _heldPos;

        public Pxf.Position At { get; } = at;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_heldPos < held.Length)
            {
                int n = Math.Min(count, held.Length - _heldPos);
                Array.Copy(held, _heldPos, buffer, offset, n);
                _heldPos += n;
                return n;
            }
            return source.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// The stream holds no <c>@dataset</c> directive: it is a well-formed document
/// without one, or the rest of a document whose last table has been read.
/// </summary>
public sealed class NoDatasetException() : Exception("no @dataset directive in stream");
