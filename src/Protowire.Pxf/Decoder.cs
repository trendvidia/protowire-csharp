// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Collections;
using System.Globalization;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

/// <summary>
/// Provides methods for decoding PXF (Protowire Exchange Format) text into C# objects.
/// </summary>
public partial class Decoder
{
    private Lexer _lex = null!;
    private Token _current = null!;
    private Result? _result;
    private string _pathPrefix = "";
    private IMessage? _rootMsg;
    private FieldDescriptor? _nullMaskFd;
    private int _depth;
    // How many list elements enclose the current body. A FieldMask path
    // names fields, not elements, so a null inside one stays out of `_null`.
    private int _elemDepth;
    // Set for exactly the next body: the immediate entries of one element
    // of a keyed repeated field. Consumed there, so nested submessages do
    // not inherit the key-field checks.
    private KeyedElemState? _keyedElem;
    private PxfLimits _limits = PxfLimits.Default;
    public TypeRegistry Registry { get; set; } = TypeRegistry.Empty;

    /// <summary>The limits of each decode; the draft's values by default.</summary>
    public PxfLimits Limits { get => _limits; set => _limits = value; }

    /// <summary>
    /// Skip a field the schema of a generated message does not declare, rather
    /// than reject the document. A hand-written class always skips.
    /// </summary>
    public bool DiscardUnknown { get; set; }

    /// <summary>
    /// Unmarshals a PXF-formatted string into the specified object.
    /// </summary>
    /// <param name="data">The PXF-formatted string.</param>
    /// <param name="obj">The object to unmarshal into.</param>
    public void Unmarshal(string data, object obj)
    {
        ResetState();
        ParseInto(data, obj);
        // POCO path: validate (pxf.required) / apply (pxf.default) read from C# attributes.
        // For IMessage, the descriptor-driven equivalent runs only in UnmarshalFull,
        // matching the Go reference (`Unmarshal` does no validation; `UnmarshalFull` does).
        if (obj is not IMessage)
        {
            ApplyDefaultsAndValidatePoco(obj);
        }
    }

    /// <summary>
    /// Unmarshals a PXF-formatted string into <paramref name="msg"/> and returns
    /// per-field presence metadata. Validates <c>(pxf.required)</c>, applies
    /// <c>(pxf.default)</c>, and writes null paths into the message's <c>_null</c>
    /// FieldMask (if present).
    /// </summary>
    /// <param name="data">The PXF-formatted string.</param>
    /// <param name="msg">The protobuf message to unmarshal into.</param>
    /// <returns>A <see cref="Result"/> with set/null/absent path information.</returns>
    public Result UnmarshalFull(string data, IMessage msg)
    {
        ResetState();
        _result = new Result();
        _rootMsg = msg;
        _nullMaskFd = AnnotationsRuntime.FindNullMaskField(msg.Descriptor);
        ParseInto(data, msg);
        PostDecode(msg, "", _nullMaskFd);
        return _result;
    }

    private void ResetState()
    {
        _result = null;
        _pathPrefix = "";
        _rootMsg = null;
        _nullMaskFd = null;
        _depth = 0;
        _elemDepth = 0;
        _keyedElem = null;
        _mapKeys = new(ReferenceEqualityComparer.Instance);
    }

    internal static void CheckMessageSize(string data, PxfLimits limits)
    {
        // Each UTF-16 unit is at least one byte of the input, so the common
        // case needs no count.
        if (data.Length * 3L <= limits.MaxMessageSize) return;
        int size = System.Text.Encoding.UTF8.GetByteCount(data);
        if (size > limits.MaxMessageSize)
        {
            throw new PxfException(new Position(1, 1, 0), $"input of {size} bytes exceeds MaxMessageSize={limits.MaxMessageSize}");
        }
    }

    private void ParseInto(string data, object obj)
    {
        // The schema is refused here, before any document is read, so the
        // outcome does not depend on which fields the document supplies.
        if (obj is IMessage bound)
        {
            Schema.EnsureBindable(bound.Descriptor);
        }
        else
        {
            Schema.EnsureBindable(obj.GetType());
        }
        CheckMessageSize(data, _limits);
        _lex = new Lexer(data, _limits.MaxBytesLiteralLength);
        Advance();

        ConsumeDirectives();

        if (obj is IMessage message)
        {
            DecodeMessage(message, false);
        }
        else
        {
            DecodeFields(obj, false);
        }
    }

    /// <summary>
    /// Drains any leading <c>@type</c> / <c>@&lt;name&gt;</c> /
    /// <c>@dataset</c> / <c>@proto</c> directives at document root. The
    /// AST-aware accessors land on <see cref="_result"/> when running
    /// under <c>UnmarshalFull</c>; otherwise the directives are simply
    /// consumed. Enforces the @dataset standalone constraint
    /// (draft §3.4.4).
    /// </summary>
    private void ConsumeDirectives()
    {
        bool sawType = false;
        bool hasDataset = false;
        Position firstDatasetPos = Position.Empty;
        for (; ; )
        {
            switch (_current.Kind)
            {
                case TokenKind.AT_TYPE:
                    if (hasDataset)
                    {
                        throw new PxfException(_current.Pos,
                            "@dataset directive cannot coexist with @type (draft §3.4.4)");
                    }
                    sawType = true;
                    Advance();
                    if (_current.Kind != TokenKind.IDENT)
                    {
                        throw new PxfException(_current.Pos,
                            $"expected type name after @type, got {_current.Kind}");
                    }
                    Advance();
                    continue;
                case TokenKind.AT_DIRECTIVE:
                    {
                        var dir = ConsumeDirective();
                        _result?.AddDirective(dir);
                        continue;
                    }
                case TokenKind.AT_DATASET:
                    {
                        if (sawType)
                        {
                            throw new PxfException(_current.Pos,
                                "@dataset directive cannot coexist with @type (draft §3.4.4)");
                        }
                        var ds = ConsumeDatasetDirective();
                        if (!hasDataset)
                        {
                            firstDatasetPos = ds.Pos;
                            hasDataset = true;
                        }
                        _result?.AddDataset(ds);
                        continue;
                    }
                case TokenKind.AT_PROTO:
                    {
                        var pd = ConsumeProtoDirective();
                        _result?.AddProto(pd);
                        continue;
                    }
            }
            if (hasDataset && _current.Kind != TokenKind.EOF)
            {
                throw new PxfException(firstDatasetPos,
                    "@dataset directive cannot coexist with top-level field entries (draft §3.4.4)");
            }
            return;
        }
    }

    /// <summary>Mirrors <see cref="Parser.ParseDirective"/>.</summary>
    private Directive ConsumeDirective()
    {
        var atPos = _current.Pos;
        string name = _current.Value;
        if (Schema.IsFutureReservedDirective(name))
        {
            throw new PxfException(atPos,
                $"@{name} is a spec-reserved directive name with no v1 semantics (draft §3.4.6)");
        }
        var prefixes = new List<string>();
        Advance();
        while (_current.Kind == TokenKind.IDENT)
        {
            var pk = PeekKind();
            if (pk == TokenKind.EQUALS || pk == TokenKind.COLON) break;
            prefixes.Add(_current.Value);
            Advance();
        }
        byte[] body = [];
        bool hasBody = false;
        if (_current.Kind == TokenKind.LBRACE)
        {
            int open = _current.Pos.Offset;
            // Read as Parse reads it, so a body that is not PXF is an error
            // here too, not skipped by counting braces.
            var parser = new Parser(_lex, _current, _limits.MaxNestingDepth, depth: 0);
            parser.ParseValueHere();
            _current = parser.Current;
            int close = BraceScan.FindMatchingBrace(_lex.Input, open);
            if (close < 0)
            {
                throw new PxfException(atPos, $"directive @{name}: unmatched '{{'");
            }
            body = System.Text.Encoding.UTF8.GetBytes(_lex.Input[(open + 1)..close]);
            hasBody = true;
        }
        string typeField = prefixes.Count == 1 ? prefixes[0] : string.Empty;
        return new Directive
        {
            Pos = atPos,
            Name = name,
            Prefixes = prefixes,
            Type = typeField,
            Body = body,
            HasBody = hasBody,
        };
    }

    /// <summary>Mirrors <see cref="Parser.ParseDatasetDirective"/>.</summary>
    private DatasetDirective ConsumeDatasetDirective()
    {
        var atPos = _current.Pos;
        Advance();
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
        Advance();
        if (_current.Kind != TokenKind.IDENT)
        {
            throw new PxfException(_current.Pos,
                $"@dataset column list must contain at least one field name, got {_current.Kind}");
        }
        var columns = new List<string>();
        var columnPositions = new List<Position>();
        for (; ; )
        {
            if (_current.Kind != TokenKind.IDENT)
            {
                throw new PxfException(_current.Pos, $"expected column field name, got {_current.Kind}");
            }
            columns.Add(_current.Value);
            columnPositions.Add(_current.Pos);
            Advance();
            if (_current.Kind == TokenKind.COMMA) { Advance(); continue; }
            if (_current.Kind == TokenKind.RPAREN) break;
            throw new PxfException(_current.Pos,
                $"expected ',' or ')' in @dataset column list, got {_current.Kind}");
        }
        DatasetPaths.CheckHeader(columns, columnPositions);
        Advance(); // consume )

        var rows = new List<DatasetRow>();
        while (_current.Kind == TokenKind.LPAREN)
        {
            var rowPos = _current.Pos;
            Advance();
            var cells = new List<IValue?>();
            cells.Add(ConsumeRowCell());
            while (_current.Kind == TokenKind.COMMA)
            {
                Advance();
                cells.Add(ConsumeRowCell());
            }
            if (_current.Kind != TokenKind.RPAREN)
            {
                throw new PxfException(_current.Pos,
                    $"expected ',' or ')' in @dataset row, got {_current.Kind}");
            }
            Advance();
            if (cells.Count != columns.Count)
            {
                throw new PxfException(rowPos,
                    $"@dataset row has {cells.Count} cells, expected {columns.Count} (column count)");
            }
            rows.Add(new DatasetRow { Pos = rowPos, Cells = cells });
        }

        return new DatasetDirective
        {
            Pos = atPos,
            Type = type,
            Columns = columns,
            Rows = rows,
        };
    }

    /// <summary>
    /// One cell of a @dataset row: null for an empty cell, and otherwise any
    /// value, read by the parser so it means what it means under Parse.
    /// </summary>
    private IValue? ConsumeRowCell()
    {
        if (_current.Kind is TokenKind.COMMA or TokenKind.RPAREN) return null;
        var parser = new Parser(_lex, _current, _limits.MaxNestingDepth, depth: 0);
        var value = parser.ParseValueHere();
        _current = parser.Current;
        return value;
    }

    /// <summary>Mirrors <see cref="Parser.ParseProtoDirective"/>.</summary>
    private ProtoDirective ConsumeProtoDirective()
    {
        var atPos = _current.Pos;
        Advance();
        switch (_current.Kind)
        {
            case TokenKind.LBRACE:
                {
                    var body = CaptureBraceBody("@proto (anonymous form)");
                    return new ProtoDirective
                    {
                        Pos = atPos,
                        Shape = ProtoShape.Anonymous,
                        Body = body,
                    };
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
                    var body = CaptureBraceBody("@proto " + typeName);
                    return new ProtoDirective
                    {
                        Pos = atPos,
                        Shape = ProtoShape.Named,
                        TypeName = typeName,
                        Body = body,
                    };
                }
            case TokenKind.STRING:
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(_current.Value);
                    Advance();
                    return new ProtoDirective
                    {
                        Pos = atPos,
                        Shape = ProtoShape.Source,
                        Body = bytes,
                    };
                }
            case TokenKind.BYTES:
                {
                    string raw = _current.Value;
                    if (!Base64Lenient.TryDecode(raw, out var decoded))
                    {
                        throw new PxfException(_current.Pos, "@proto descriptor body: invalid base64");
                    }
                    Advance();
                    return new ProtoDirective
                    {
                        Pos = atPos,
                        Shape = ProtoShape.Descriptor,
                        Body = decoded,
                    };
                }
            default:
                throw new PxfException(_current.Pos,
                    $"expected '{{', dotted identifier, triple-quoted string, or b\"...\" after @proto, got {_current.Kind}");
        }
    }

    /// <summary>
    /// LBRACE is current on entry. Slices raw inner bytes, repositions
    /// the lexer past the closing `}`, and primes the parser to the
    /// next token.
    /// </summary>
    private byte[] CaptureBraceBody(string label)
    {
        int open = _current.Pos.Offset;
        int close = BraceScan.FindMatchingBrace(_lex.Input, open);
        if (close < 0)
        {
            throw new PxfException(_current.Pos, $"{label}: unmatched '{{'");
        }
        var body = System.Text.Encoding.UTF8.GetBytes(_lex.Input[(open + 1)..close]);
        _lex.RepositionTo(close + 1);
        Advance();
        return body;
    }

    /// <summary>One-token lookahead with full state restore.</summary>
    private TokenKind PeekKind()
    {
        var lexState = _lex.Save();
        var savedCurrent = _current;
        Advance();
        var peeked = _current.Kind;
        _lex.Restore(lexState);
        _current = savedCurrent;
        return peeked;
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
            if (_current.Kind != TokenKind.COMMENT && _current.Kind != TokenKind.NEWLINE)
            {
                return;
            }
        }
    }

    // HARDENING.md § Recursion: the root message is depth 0 and every `{`
    // or `[` is one descent, so a document exactly MaxNestingDepth deep is
    // accepted and one deeper is rejected. Parser counts the same way.
    private void EnterDescent()
    {
        if (_depth >= _limits.MaxNestingDepth)
        {
            throw new PxfException(_current.Pos,
                $"nesting depth exceeds MaxNestingDepth={_limits.MaxNestingDepth}");
        }
        _depth++;
    }

    private void DecodeFields(object obj, bool inBlock)
    {
        // A hand-written class can hold a generated message.
        if (obj is IMessage message)
        {
            DecodeMessage(message, inBlock);
            return;
        }
        if (inBlock) EnterDescent();
        try
        {
            DecodeFieldsBody(obj, inBlock);
        }
        finally
        {
            if (inBlock) _depth--;
        }
    }

    // The path for a hand-written class: no descriptor, so a member's CLR type
    // is the only schema. An unknown member is skipped.
    private void DecodeFieldsBody(object obj, bool inBlock)
    {
        var type = obj.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        var bound = new HashSet<string>();

        while (true)
        {
            if (inBlock && _current.Kind == TokenKind.RBRACE)
            {
                Advance();
                return;
            }
            if (_current.Kind == TokenKind.EOF)
            {
                if (inBlock) throw new PxfException(_current.Pos, "expected '}', got EOF");
                return;
            }

            var pos = _current.Pos;
            if (_current.Kind == TokenKind.AT_TYPE) throw new PxfException(pos, TypeEntryMisplaced);
            if (_current.Kind is not (TokenKind.IDENT or TokenKind.STRING or TokenKind.INT or TokenKind.BOOL))
            {
                throw new PxfException(pos, $"expected identifier, string, integer, or bool, got {_current.Kind}");
            }
            var keyKind = _current.Kind;
            string key = _current.Value;
            Advance();

            // Strict-PXF-keys grammar, enforced before the unknown-member skip
            // can swallow it. A string key before `=` or `{` is well-formed
            // but is a quoted entry name, which names no member.
            if (_current.Kind is TokenKind.EQUALS or TokenKind.LBRACE)
            {
                if (keyKind == TokenKind.STRING) throw Keyed.QuotedNameUnkeyed(pos, key);
                if (keyKind is TokenKind.INT or TokenKind.BOOL)
                {
                    throw _current.Kind == TokenKind.EQUALS
                        ? Parser.NonNameAssignment(pos, keyKind, key)
                        : Parser.NonNameBlock(pos, keyKind, key);
                }
            }
            if (_current.Kind == TokenKind.COLON && !inBlock) throw Parser.TopLevelMapEntry(pos);

            var prop = props.FirstOrDefault(p => MatchName(p.Name, key));
            var field = fields.FirstOrDefault(f => MatchName(f.Name, key));
            if (prop != null && !prop.CanWrite && !typeof(IEnumerable).IsAssignableFrom(prop.PropertyType)) prop = null;
            Type? memberType = prop?.PropertyType ?? field?.FieldType;

            if (memberType == null)
            {
                if (_current.Kind == TokenKind.EQUALS)
                {
                    Advance();
                    SkipValue();
                }
                else if (_current.Kind == TokenKind.LBRACE)
                {
                    SkipValue();
                }
                continue;
            }

            bool collection = typeof(IEnumerable).IsAssignableFrom(memberType)
                && memberType != typeof(string) && memberType != typeof(byte[]);
            if (!collection && !bound.Add(prop?.Name ?? field!.Name))
            {
                throw new PxfException(pos, $"field \"{key}\" is already set");
            }

            switch (_current.Kind)
            {
                case TokenKind.EQUALS:
                    Advance();
                    if (_current.Kind == TokenKind.NULL)
                    {
                        Advance();
                        continue;
                    }
                    var currentVal = prop?.GetValue(obj) ?? field?.GetValue(obj);
                    var val = DecodeValue(memberType, currentVal);
                    if (prop != null && prop.CanWrite) prop.SetValue(obj, val);
                    else if (field != null) field.SetValue(obj, val);
                    break;

                case TokenKind.LBRACE:
                    Advance();
                    var subObj = prop?.GetValue(obj) ?? field?.GetValue(obj);
                    if (subObj == null)
                    {
                        subObj = Activator.CreateInstance(memberType);
                        if (prop != null && prop.CanWrite) prop.SetValue(obj, subObj);
                        else if (field != null) field.SetValue(obj, subObj);
                    }
                    DecodeFields(subObj!, true);
                    break;

                default:
                    throw new PxfException(_current.Pos, $"expected '=' or '{{' after \"{key}\"");
            }
        }
    }

    /// <summary>
    /// Descends into element <paramref name="index"/> of <paramref name="fd"/>:
    /// presence paths inside it are <c>name[i].field</c>, so they cannot
    /// pass for fields of the enclosing message. Returns the prefix to hand
    /// to <see cref="LeaveElement"/>.
    /// </summary>
    private string EnterElement(FieldDescriptor fd, int index)
    {
        var saved = _pathPrefix;
        _pathPrefix = $"{saved}{fd.Name}[{index}].";
        _elemDepth++;
        return saved;
    }

    private void LeaveElement(string saved)
    {
        _pathPrefix = saved;
        _elemDepth--;
    }

    private object? DecodeValue(Type type, object? currentVal)
    {
        var pos = _current.Pos;
        var underlying = System.Nullable.GetUnderlyingType(type);
        if (underlying != null) type = underlying;

        if (type == typeof(DateTime))
        {
            if (_current.Kind != TokenKind.TIMESTAMP) throw new PxfException(pos, "expected timestamp");
            var dt = DateTime.Parse(_current.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Advance();
            return dt;
        }

        if (type == typeof(TimeSpan))
        {
            if (_current.Kind is not (TokenKind.DURATION or TokenKind.INT)) throw new PxfException(pos, "expected duration");
            var ts = DurationParser.Parse(_current.Value);
            Advance();
            return ts;
        }

        if (type == typeof(System.Numerics.BigInteger))
        {
            CheckLiteralDigits(_current.Value, pos);
            if (!System.Numerics.BigInteger.TryParse(_current.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var bi))
            {
                throw new PxfException(pos, $"invalid big integer: {_current.Value}");
            }
            Advance();
            return bi;
        }
        if (type == typeof(Protowire.Pb.Decimal))
        {
            var dec = ParseDecimal(_current.Value, pos);
            Advance();
            return dec;
        }
        if (type == typeof(Protowire.Pb.BigFloat))
        {
            var bf = ParseBigFloatToken(pos);
            Advance();
            return bf;
        }

        if (type == typeof(string))
        {
            var s = _current.Value;
            Advance();
            return s;
        }
        if (type == typeof(bool))
        {
            var b = _current.Value == "true";
            Advance();
            return b;
        }
        if (type == typeof(int)) return ParseNumber<int>(pos);
        if (type == typeof(long)) return ParseNumber<long>(pos);
        // uint32 / fixed32 and uint64 / fixed64 generate uint / ulong.
        if (type == typeof(uint)) return ParseNumber<uint>(pos);
        if (type == typeof(ulong)) return ParseNumber<ulong>(pos);
        if (type == typeof(float)) return ParseNumber<float>(pos);
        if (type == typeof(double)) return ParseNumber<double>(pos);
        if (type == typeof(byte[]) || type == typeof(ByteString))
        {
            if (!Base64Lenient.TryDecode(_current.Value, out var bytes))
            {
                throw new PxfException(pos, $"invalid base64: {_current.Value}");
            }
            Advance();
            return type == typeof(byte[]) ? bytes : ByteString.CopyFrom(bytes);
        }
        if (type.IsEnum)
        {
            if (_current.Kind == TokenKind.IDENT)
            {
                string enumName = _current.Value;
                Advance();
                if (Enum.TryParse(type, enumName, true, out var parsed)) return parsed;
                throw new PxfException(pos, $"unknown enum value \"{enumName}\" for {type.Name}");
            }
            return Enum.ToObject(type, ParseNumber<int>(pos));
        }
        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            return DecodeMap(type, currentVal as IDictionary);
        }
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            return DecodeList(type, currentVal as IEnumerable);
        }

        if (_current.Kind == TokenKind.LBRACE)
        {
            Advance();
            var subObj = currentVal ?? Activator.CreateInstance(type)!;
            DecodeFields(subObj, true);
            return subObj;
        }

        throw new PxfException(pos, $"unsupported type {type}");
    }

    private T ParseNumber<T>(Position pos) where T : IParsable<T>
    {
        if (!T.TryParse(_current.Value, CultureInfo.InvariantCulture, out var value))
        {
            throw new PxfException(pos, $"invalid {typeof(T).Name}: {_current.Value}");
        }
        Advance();
        return value;
    }

    private object DecodeList(Type type, IEnumerable? currentList)
    {
        if (_current.Kind != TokenKind.LBRACKET) throw new PxfException(_current.Pos, "expected '['");
        EnterDescent();
        try
        {
            return DecodeListBody(type, currentList);
        }
        finally
        {
            _depth--;
        }
    }

    private object DecodeListBody(Type type, IEnumerable? currentList)
    {
        Advance();

        var itemType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
        var list = (currentList as IList) ?? (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;

        while (_current.Kind != TokenKind.RBRACKET && _current.Kind != TokenKind.EOF)
        {
            if (list.Count >= _limits.MaxRepeatedCount)
            {
                throw new PxfException(_current.Pos, $"repeated field exceeds MaxRepeatedCount={_limits.MaxRepeatedCount}");
            }
            if (_current.Kind == TokenKind.NULL)
            {
                throw new PxfException(_current.Pos, "null is not allowed as a list element");
            }
            list.Add(DecodeValue(itemType, null));
            if (_current.Kind == TokenKind.COMMA) Advance();
        }
        if (_current.Kind != TokenKind.RBRACKET) throw new PxfException(_current.Pos, "expected ']'");
        Advance();

        if (type.IsArray)
        {
            var array = Array.CreateInstance(itemType, list.Count);
            list.CopyTo(array, 0);
            return array;
        }
        return list;
    }

    private IDictionary DecodeMap(Type type, IDictionary? currentMap)
    {
        if (_current.Kind != TokenKind.LBRACE) throw new PxfException(_current.Pos, "expected '{'");
        // HARDENING.md § Recursion: a map's `{` is a descent like any other,
        // whether or not its values are messages.
        EnterDescent();
        try
        {
            return DecodeMapBody(type, currentMap);
        }
        finally
        {
            _depth--;
        }
    }

    private IDictionary DecodeMapBody(Type type, IDictionary? currentMap)
    {
        Advance();
        var keyType = type.GetGenericArguments()[0];
        var valType = type.GetGenericArguments()[1];
        var map = currentMap ?? (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valType))!;
        var keys = new HashSet<object>();

        while (_current.Kind != TokenKind.RBRACE && _current.Kind != TokenKind.EOF)
        {
            var pos = _current.Pos;
            if (map.Count >= _limits.MaxRepeatedCount)
            {
                throw new PxfException(pos, $"map field exceeds MaxRepeatedCount={_limits.MaxRepeatedCount}");
            }
            if (_current.Kind is not (TokenKind.IDENT or TokenKind.STRING or TokenKind.INT or TokenKind.BOOL))
            {
                throw new PxfException(pos, $"expected map key, got {_current.Kind}");
            }
            var keyStr = _current.Value;
            Advance();
            if (_current.Kind != TokenKind.COLON) throw new PxfException(_current.Pos, "expected ':'");
            Advance();

            object key;
            try
            {
                key = Convert.ChangeType(keyStr, keyType, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException or OverflowException)
            {
                throw new PxfException(pos, $"invalid map key: {keyStr}");
            }
            if (!keys.Add(key)) throw new PxfException(pos, $"map key {keyStr} is already set");
            if (_current.Kind == TokenKind.NULL)
            {
                throw new PxfException(_current.Pos, "null is not allowed as a map value");
            }
            map[key] = DecodeValue(valType, null);
        }
        if (_current.Kind != TokenKind.RBRACE) throw new PxfException(_current.Pos, "expected '}'");
        Advance();
        return map;
    }

    private void ApplyDefaultsAndValidatePoco(object obj)
    {
        var type = obj.GetType();
        foreach (var prop in type.GetProperties())
        {
            var required = prop.GetCustomAttribute<PxfRequiredAttribute>();
            var def = prop.GetCustomAttribute<PxfDefaultAttribute>();

            if (required == null && def == null) continue;

            var val = prop.GetValue(obj);
            bool isDefault = IsDefaultValue(prop.PropertyType, val);

            if (isDefault && def != null)
            {
                prop.SetValue(obj, Convert.ChangeType(def.Value, prop.PropertyType));
            }
            else if (isDefault && required != null)
            {
                throw new PxfException(Position.Empty, $"required field \"{prop.Name}\" is absent");
            }
        }
    }

    /// <summary>
    /// Validates <c>(pxf.required)</c>, applies <c>(pxf.default)</c>, and
    /// recurses into the bodies the input wrote: present singular messages,
    /// list elements and map values, each under the path its fields were
    /// recorded at. Skips the <c>_null</c> mask at the root. Mirrors
    /// <c>postDecode</c> in <c>protowire-go/encoding/pxf/decode_fast.go</c>.
    /// </summary>
    private void PostDecode(IMessage msg, string pathPrefix, FieldDescriptor? nullMaskFd)
    {
        foreach (var fd in msg.Descriptor.Fields.InDeclarationOrder())
        {
            if (nullMaskFd != null && fd.FieldNumber == nullMaskFd.FieldNumber) continue;
            string path = pathPrefix + fd.Name;
            if (!_result!.Has(path))
            {
                if (AnnotationsRuntime.IsRequired(fd))
                {
                    throw new PxfException(new Position(1, 1, 0), $"required field \"{path}\" is absent");
                }
                var def = AnnotationsRuntime.GetDefault(fd);
                if (def != null && !OneofAlreadyChosen(fd, pathPrefix))
                {
                    ApplyDefault(msg, fd, def);
                }
            }
            else if (!_result.IsNull(path))
            {
                PostDecodeBodies(msg, fd, path);
            }
            // null + default: the default does not apply, null is intentional.
        }
    }

    private void PostDecodeBodies(IMessage msg, FieldDescriptor fd, string path)
    {
        if (fd.IsMap)
        {
            var valueFd = fd.MessageType.FindFieldByNumber(2);
            if (valueFd.FieldType is not (FieldType.Message or FieldType.Group) || WellKnown.IsWrapper(valueFd.MessageType)) return;
            var entries = new List<(string Path, IMessage Body)>();
            foreach (DictionaryEntry e in (IDictionary)fd.Accessor.GetValue(msg))
            {
                entries.Add((path + KeySuffix(e.Key), (IMessage)e.Value!));
            }
            // Key order, so the first error is the same on every run.
            entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            foreach (var (entryPath, body) in entries)
            {
                PostDecode(body, entryPath + ".", null);
            }
            return;
        }
        if (fd.FieldType is not (FieldType.Message or FieldType.Group) || WellKnown.IsWrapper(fd.MessageType)) return;
        if (fd.IsRepeated)
        {
            int i = 0;
            foreach (var element in (IList)fd.Accessor.GetValue(msg))
            {
                PostDecode((IMessage)element, $"{path}[{i++}].", null);
            }
            return;
        }
        if (fd.Accessor.GetValue(msg) is IMessage sub)
        {
            PostDecode(sub, path + ".", null);
        }
    }

    // Setting a member of a oneof clears the rest, so a default applied over a
    // chosen sibling destroys it. Presence is read from the document, because
    // a default applied earlier in this pass also sets the case. A member
    // bound to null counts as chosen.
    private bool OneofAlreadyChosen(FieldDescriptor fd, string pathPrefix)
    {
        var oneof = fd.RealContainingOneof;
        return oneof != null
            && oneof.Fields.Any(m => m.FieldNumber != fd.FieldNumber && _result!.Has(pathPrefix + m.Name));
    }

    /// <summary>
    /// Sets <paramref name="fd"/> on <paramref name="msg"/> from the PXF default
    /// literal <paramref name="def"/>. Handles scalars, enums, bytes (base64),
    /// and the WKT message types (Timestamp, Duration, wrappers, BigInt, Decimal,
    /// BigFloat).
    ///
    /// <para>Mirrors <c>applyDefault</c> in <c>protowire-go/encoding/pxf/decode_fast.go</c>.</para>
    /// </summary>
    private static void ApplyDefault(IMessage msg, FieldDescriptor fd, string def)
    {
        switch (fd.FieldType)
        {
            case FieldType.String:
                fd.Accessor.SetValue(msg, def);
                return;
            case FieldType.Bool:
                fd.Accessor.SetValue(msg, def == "true");
                return;
            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.SFixed32:
                fd.Accessor.SetValue(msg, int.Parse(def, CultureInfo.InvariantCulture));
                return;
            case FieldType.Int64:
            case FieldType.SInt64:
            case FieldType.SFixed64:
                fd.Accessor.SetValue(msg, long.Parse(def, CultureInfo.InvariantCulture));
                return;
            case FieldType.UInt32:
            case FieldType.Fixed32:
                fd.Accessor.SetValue(msg, uint.Parse(def, CultureInfo.InvariantCulture));
                return;
            case FieldType.UInt64:
            case FieldType.Fixed64:
                fd.Accessor.SetValue(msg, ulong.Parse(def, CultureInfo.InvariantCulture));
                return;
            case FieldType.Float:
                fd.Accessor.SetValue(msg, float.Parse(def, CultureInfo.InvariantCulture));
                return;
            case FieldType.Double:
                fd.Accessor.SetValue(msg, double.Parse(def, CultureInfo.InvariantCulture));
                return;
            case FieldType.Bytes:
                fd.Accessor.SetValue(msg, ByteString.CopyFrom(Convert.FromBase64String(def)));
                return;
            case FieldType.Enum:
                {
                    var ev = fd.EnumType.FindValueByName(def);
                    int n = ev != null ? ev.Number : int.Parse(def, CultureInfo.InvariantCulture);
                    fd.Accessor.SetValue(msg, n);
                    return;
                }
            case FieldType.Message:
            case FieldType.Group:
                ApplyMessageDefault(msg, fd, def);
                return;
            default:
                throw new PxfException(Position.Empty,
                    $"default values not supported for kind {fd.FieldType} (field \"{fd.Name}\")");
        }
    }

    /// <summary>
    /// Applies a default literal to a singular WKT message field. Recognized:
    /// Timestamp (RFC 3339), Duration (Go-style), the nine wrapper types,
    /// pxf.BigInt, pxf.Decimal, pxf.BigFloat.
    ///
    /// <para>Mirrors <c>applyMessageDefault</c> in <c>decode_fast.go</c>.</para>
    /// </summary>
    internal static void ApplyMessageDefault(IMessage msg, FieldDescriptor fd, string def)
    {
        var mdesc = fd.MessageType;

        // Google.Protobuf C# maps the nine wrapper types to nullable scalars
        // on the parent message (e.g. Int32Value → int?), so the accessor
        // expects the boxed inner scalar directly — not an inner message.
        if (WellKnown.WrapperTypes.TryGetValue(mdesc.FullName, out var innerKind))
        {
            fd.Accessor.SetValue(msg, ParseScalarDefault(innerKind, def, fd));
            return;
        }

        var clrType = mdesc.ClrType
            ?? throw new PxfException(Position.Empty,
                $"default values not supported for message type {mdesc.FullName} (no CLR type)");
        var sub = (IMessage)Activator.CreateInstance(clrType)!;

        if (WellKnown.IsTimestamp(mdesc))
        {
            if (!Rfc3339.TryParse(def, out long seconds, out int nanos))
            {
                throw new PxfException(Position.Empty, $"invalid default timestamp \"{def}\" for field \"{fd.Name}\"");
            }
            sub = WellKnown.NewSecondsNanos(mdesc, seconds, nanos);
        }
        else if (WellKnown.IsDuration(mdesc))
        {
            if (!DurationParser.TryParseNanos(def, out long total))
            {
                throw new PxfException(Position.Empty, $"invalid default duration \"{def}\" for field \"{fd.Name}\"");
            }
            sub = WellKnown.NewSecondsNanos(mdesc, total / 1_000_000_000, (int)(total % 1_000_000_000));
        }
        else if (WellKnown.IsBigInt(mdesc))
        {
            CheckLiteralDigits(def, Position.Empty, PxfLimits.Default.MaxNumericLiteralDigits);
            WellKnown.SetBigInt(sub, System.Numerics.BigInteger.Parse(def, CultureInfo.InvariantCulture));
        }
        else if (WellKnown.IsDecimal(mdesc))
        {
            WellKnown.SetDecimal(sub, ParseDecimalLiteral(def));
        }
        else if (WellKnown.IsBigFloat(mdesc))
        {
            WellKnown.SetBigFloat(sub, ParseBigFloatLiteral(def));
        }
        else
        {
            throw new PxfException(Position.Empty,
                $"default values not supported for message type {mdesc.FullName} (field \"{fd.Name}\")");
        }
        fd.Accessor.SetValue(msg, sub);
    }

    private static object ParseScalarDefault(FieldType kind, string def, FieldDescriptor fd) => kind switch
    {
        FieldType.String => def,
        FieldType.Bool => def == "true",
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 =>
            int.Parse(def, CultureInfo.InvariantCulture),
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 =>
            long.Parse(def, CultureInfo.InvariantCulture),
        FieldType.UInt32 or FieldType.Fixed32 =>
            uint.Parse(def, CultureInfo.InvariantCulture),
        FieldType.UInt64 or FieldType.Fixed64 =>
            ulong.Parse(def, CultureInfo.InvariantCulture),
        FieldType.Float => float.Parse(def, CultureInfo.InvariantCulture),
        FieldType.Double => double.Parse(def, CultureInfo.InvariantCulture),
        FieldType.Bytes => (object)ByteString.CopyFrom(Convert.FromBase64String(def)),
        _ => throw new PxfException(Position.Empty,
            $"unsupported default kind {kind} for field \"{fd.Name}\""),
    };

    private static Protowire.Pb.Decimal ParseDecimalLiteral(string s)
    {
        CheckLiteralDigits(s, Position.Empty, PxfLimits.Default.MaxNumericLiteralDigits);
        bool negative = s.StartsWith('-');
        if (negative) s = s[1..];
        int dot = s.IndexOf('.');
        int scale = 0;
        if (dot >= 0)
        {
            scale = s.Length - dot - 1;
            s = s.Remove(dot, 1);
        }
        CheckDecimalScale(scale, Position.Empty, PxfLimits.Default.MaxNumericLiteralDigits);
        if (s.Length == 0) s = "0";
        return new Protowire.Pb.Decimal(
            System.Numerics.BigInteger.Parse(s, CultureInfo.InvariantCulture), scale, negative);
    }

    private static Protowire.Pb.BigFloat ParseBigFloatLiteral(string s)
    {
        try
        {
            return BigFloatText.Parse(s, PxfLimits.Default.MaxNumericLiteralDigits, PxfLimits.Default.MaxBigFloatExponent);
        }
        catch (FormatException e)
        {
            throw new PxfException(Position.Empty, $"invalid default big float: {e.Message}");
        }
    }

    // Rejects a literal past MaxNumericLiteralDigits before it reaches a
    // big-number parser. A literal no longer than the limit needs no count.
    private void CheckLiteralDigits(string s, Position pos) => CheckLiteralDigits(s, pos, _limits.MaxNumericLiteralDigits);

    private static void CheckLiteralDigits(string s, Position pos, int limit)
    {
        if (s.Length <= limit) return;
        int n = CountDigits(s);
        if (n > limit)
        {
            throw new PxfException(pos, $"numeric literal has {n} digits; MaxNumericLiteralDigits={limit}");
        }
    }

    // What MaxNumericLiteralDigits counts: the significant digits of a
    // literal's mantissa, either side of the point, leading zeros excluded.
    // An exponent and a precision suffix are not counted, so the count stops
    // at either marker (HARDENING.md § Mandatory limits, protowire#410).
    internal static int CountDigits(string s)
    {
        int n = 0;
        bool leading = true;
        foreach (char c in s)
        {
            if (c == 'e' || c == 'E' || c == 'p') break;
            if (c < '0' || c > '9' || (leading && c == '0')) continue;
            leading = false;
            n++;
        }
        return n;
    }

    // Leading zeros do not count as digits, so "0.000…1" is one digit however
    // many places it has; its scale is bounded here, as on the PB wire.
    private void CheckDecimalScale(int scale, Position pos) => CheckDecimalScale(scale, pos, _limits.MaxNumericLiteralDigits);

    private static void CheckDecimalScale(int scale, Position pos, int limit)
    {
        if (scale > limit)
        {
            throw new PxfException(pos, $"pxf.Decimal scale {scale} exceeds MaxNumericLiteralDigits={limit}");
        }
    }

    private bool IsDefaultValue(Type type, object? val)
    {
        if (val == null) return true;
        if (type == typeof(string)) return (string)val == "";
        if (type.IsValueType) return val.Equals(Activator.CreateInstance(type));
        if (val is IEnumerable e)
        {
            var enumerator = e.GetEnumerator();
            return !enumerator.MoveNext();
        }
        return false;
    }

    private bool MatchName(string memberName, string key)
    {
        if (memberName.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
        var normalizedMember = memberName.Replace("_", "").ToLowerInvariant();
        var normalizedKey = key.Replace("_", "").ToLowerInvariant();
        return normalizedMember == normalizedKey;
    }

    // Skips a value the target has no member for, read by the parser, so a
    // discarded value is held to the grammar: an unterminated block or a
    // lexical error inside it is reported. It starts from the enclosing body's
    // depth, so the nesting limit binds it as it binds a decoded value.
    private void SkipValue()
    {
        var parser = new Parser(_lex, _current, _limits.MaxNestingDepth, _depth);
        parser.ParseValueHere();
        _current = parser.Current;
    }
}
