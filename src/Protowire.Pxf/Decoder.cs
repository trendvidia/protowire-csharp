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
public class Decoder
{
    // HARDENING.md § Mandatory limits — bounds native call-stack growth on
    // attacker input. Matches the cross-port default.
    private const int MaxNestingDepth = 100;

    private Lexer _lex = null!;
    private Token _current = null!;
    private Result? _result;
    private string _pathPrefix = "";
    private IMessage? _rootMsg;
    private FieldDescriptor? _nullMaskFd;
    private int _depth;
    public TypeRegistry Registry { get; set; } = TypeRegistry.Empty;

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
        PostDecode(msg, "");
        return _result;
    }

    private void ResetState()
    {
        _result = null;
        _pathPrefix = "";
        _rootMsg = null;
        _nullMaskFd = null;
        _depth = 0;
    }

    private void ParseInto(string data, object obj)
    {
        _lex = new Lexer(data);
        Advance();

        ConsumeDirectives();

        DecodeFields(obj, false);
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
        for (;;)
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
            int close = BraceScan.FindMatchingBrace(_lex.Input, open);
            if (close < 0)
            {
                throw new PxfException(atPos, $"directive @{name}: unmatched '{{'");
            }
            body = System.Text.Encoding.UTF8.GetBytes(_lex.Input[(open + 1)..close]);
            hasBody = true;
            _lex.RepositionTo(close + 1);
            Advance();
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
            if (_current.Kind == TokenKind.COMMA) { Advance(); continue; }
            if (_current.Kind == TokenKind.RPAREN) break;
            throw new PxfException(_current.Pos,
                $"expected ',' or ')' in @dataset column list, got {_current.Kind}");
        }
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
    /// Consumes one cell of a @dataset row. Returns null for an empty
    /// cell. Rejects list and block values per v1 cell-grammar.
    /// </summary>
    private IValue? ConsumeRowCell()
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
        // Mirror the value-parsing subset used by row cells. AST shape
        // matches Parser.ParseValue.
        var pos = _current.Pos;
        switch (_current.Kind)
        {
            case TokenKind.STRING:
                {
                    var v = new StringVal { Pos = pos, Value = _current.Value };
                    Advance();
                    return v;
                }
            case TokenKind.INT:
                {
                    var v = new IntVal { Pos = pos, Raw = _current.Value };
                    Advance();
                    return v;
                }
            case TokenKind.FLOAT:
                {
                    var v = new FloatVal { Pos = pos, Raw = _current.Value };
                    Advance();
                    return v;
                }
            case TokenKind.BOOL:
                {
                    var v = new BoolVal { Pos = pos, Value = _current.Value == "true" };
                    Advance();
                    return v;
                }
            case TokenKind.BYTES:
                {
                    byte[] decoded;
                    try { decoded = Convert.FromBase64String(_current.Value); }
                    catch (FormatException) { throw new PxfException(pos, $"invalid base64: {_current.Value}"); }
                    var v = new BytesVal { Pos = pos, Value = decoded };
                    Advance();
                    return v;
                }
            case TokenKind.TIMESTAMP:
                {
                    if (!DateTime.TryParse(_current.Value, out var dt))
                    {
                        throw new PxfException(pos, $"invalid timestamp \"{_current.Value}\"");
                    }
                    var v = new TimestampVal { Pos = pos, Value = dt, Raw = _current.Value };
                    Advance();
                    return v;
                }
            case TokenKind.DURATION:
                {
                    var dv = DurationParser.Parse(_current.Value);
                    var v = new DurationVal { Pos = pos, Value = dv, Raw = _current.Value };
                    Advance();
                    return v;
                }
            case TokenKind.NULL:
                {
                    var v = new NullVal { Pos = pos };
                    Advance();
                    return v;
                }
            case TokenKind.IDENT:
                {
                    var v = new IdentVal { Pos = pos, Name = _current.Value };
                    Advance();
                    return v;
                }
            default:
                throw new PxfException(pos, $"expected value, got {_current.Kind} (\"{_current.Value}\")");
        }
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
                    byte[] decoded;
                    try { decoded = Convert.FromBase64String(raw); }
                    catch (FormatException)
                    {
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
                                "@proto descriptor body: invalid base64");
                        }
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

    private void DecodeFields(object obj, bool inBlock)
    {
        if (inBlock && _depth >= MaxNestingDepth)
        {
            throw new PxfException(_current.Pos,
                $"nesting depth exceeds {MaxNestingDepth}");
        }
        if (inBlock) _depth++;
        try
        {
            DecodeFieldsBody(obj, inBlock);
        }
        finally
        {
            if (inBlock) _depth--;
        }
    }

    private void DecodeFieldsBody(object obj, bool inBlock)
    {
        var type = obj.GetType();
        IMessage? msg = obj as IMessage;
        var msgDesc = msg?.Descriptor;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);

        var localSetFields = new HashSet<string>();

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
            if (_current.Kind == TokenKind.AT_TYPE)
            {
                Advance();
                if (_current.Kind != TokenKind.IDENT) throw new PxfException(_current.Pos, "expected type name after @type");
                var typeUrl = _current.Value;
                Advance();

                if (obj is IMessage any && WellKnown.IsAny(any.Descriptor))
                {
                    // This is handled by the caller or we need to handle it here.
                    // Actually, if we are inside DecodeFields for an Any, we should probably 
                    // have already resolved the type.
                    // But if we are here, it means we are decoding an Any that was just created.
                    
                    var typeName = typeUrl;
                    if (typeName.Contains("/")) typeName = typeName[(typeName.LastIndexOf('/') + 1)..];
                    var desc = Registry.Find(typeName);
                    if (desc == null) throw new PxfException(pos, $"type \"{typeName}\" not found in registry");
                    
                    var unpackedMsg = desc.Parser.ParseFrom(Array.Empty<byte>());
                    DecodeFields(unpackedMsg, true);
                    
                    var typeUrlProp = any.GetType().GetProperty("TypeUrl");
                    var valueProp = any.GetType().GetProperty("Value");
                    typeUrlProp?.SetValue(any, typeUrl);
                    valueProp?.SetValue(any, unpackedMsg.ToByteString());
                    return; // Done with this block
                }
                continue;
            }

            if (_current.Kind != TokenKind.IDENT && _current.Kind != TokenKind.STRING && _current.Kind != TokenKind.INT)
            {
                throw new PxfException(pos, $"expected identifier, string, or integer, got {_current.Kind}");
            }
            var keyKind = _current.Kind;
            string key = _current.Value;
            Advance();

            // Strict-PXF-keys grammar: `=` (field assignment) and `{` (submessage)
            // require an identifier key. String / integer keys are only valid
            // with `:` (map entries). Enforce here before the unknown-field
            // skip path silently swallows the input.
            if (keyKind != TokenKind.IDENT)
            {
                if (_current.Kind == TokenKind.EQUALS)
                {
                    throw new PxfException(pos, $"field assignment with '=' requires an identifier key, got {keyKind} (\"{key}\"); use ':' for map entries");
                }
                if (_current.Kind == TokenKind.LBRACE)
                {
                    throw new PxfException(pos, $"submessage block requires an identifier key, got {keyKind} (\"{key}\")");
                }
            }
            // `:` (map entry) is reserved for inside a `{ ... }` block; the
            // document represents a proto message, never a map<K,V>.
            if (_current.Kind == TokenKind.COLON && !inBlock)
            {
                throw new PxfException(pos, "map entry (':' form) is only allowed inside a '{ … }' block; use '=' for top-level field assignments");
            }

            FieldDescriptor? fd = msgDesc?.Fields.InDeclarationOrder().FirstOrDefault(f => f.Name == key);
            if (fd != null && fd.ContainingOneof != null)
            {
                var oneofName = fd.ContainingOneof.Name;
                if (localSetFields.Contains("oneof:" + oneofName))
                {
                    throw new PxfException(pos, $"oneof \"{oneofName}\": field \"{key}\" conflicts with already-set field");
                }
                localSetFields.Add("oneof:" + oneofName);
            }

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
                    Advance();
                    SkipBraced();
                }
                continue;
            }

            localSetFields.Add(key);

            switch (_current.Kind)
            {
                case TokenKind.EQUALS:
                    Advance();
                    if (_current.Kind == TokenKind.NULL)
                    {
                        if (_result != null && fd != null)
                        {
                            var path = _pathPrefix + fd.Name;
                            _result.MarkNull(path);
                            if (_nullMaskFd != null && _rootMsg != null)
                            {
                                AnnotationsRuntime.AppendNullPath(_rootMsg, _nullMaskFd, path);
                            }
                        }
                        Advance();
                        continue;
                    }
                    if (_result != null && fd != null)
                    {
                        _result.MarkPresent(_pathPrefix + fd.Name);
                    }
                    var currentVal = prop?.GetValue(obj) ?? field?.GetValue(obj);
                    var val = DecodeValue(memberType, fd, currentVal);
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
                    if (_result != null && fd != null)
                    {
                        _result.MarkPresent(_pathPrefix + fd.Name);
                        var saved = _pathPrefix;
                        _pathPrefix = _pathPrefix + fd.Name + ".";
                        try
                        {
                            DecodeFields(subObj!, true);
                        }
                        finally
                        {
                            _pathPrefix = saved;
                        }
                    }
                    else
                    {
                        DecodeFields(subObj!, true);
                    }
                    break;

                default:
                    throw new PxfException(_current.Pos, $"expected '=' or '{{' after \"{key}\"");
            }
        }
    }

    private object? DecodeValue(Type type, FieldDescriptor? fd, object? currentVal)
    {
        var pos = _current.Pos;

        if (fd != null && fd.FieldType == FieldType.Message && WellKnown.IsAny(fd.MessageType))
        {
            return DecodeAny(currentVal);
        }

        if (type == typeof(DateTime) || (fd != null && fd.FieldType == FieldType.Message && WellKnown.IsTimestamp(fd.MessageType)))
        {
            if (_current.Kind != TokenKind.TIMESTAMP) throw new PxfException(pos, "expected timestamp");
            var dt = DateTime.Parse(_current.Value);
            Advance();
            if (type == typeof(DateTime)) return dt;
            var wkt = currentVal ?? Activator.CreateInstance(type)!;
            WellKnown.SetTimestamp(wkt, dt);
            return wkt;
        }

        if (type == typeof(TimeSpan) || (fd != null && fd.FieldType == FieldType.Message && WellKnown.IsDuration(fd.MessageType)))
        {
            if (_current.Kind != TokenKind.DURATION && _current.Kind != TokenKind.INT) throw new PxfException(pos, "expected duration");
            var ts = DurationParser.Parse(_current.Value);
            Advance();
            if (type == typeof(TimeSpan)) return ts;
            var wkt = currentVal ?? Activator.CreateInstance(type)!;
            WellKnown.SetDuration(wkt, ts);
            return wkt;
        }

        if (fd != null && fd.FieldType == FieldType.Message)
        {
            if (WellKnown.IsBigInt(fd.MessageType))
            {
                var bi = System.Numerics.BigInteger.Parse(_current.Value);
                Advance();
                var msg = currentVal ?? Activator.CreateInstance(type)!;
                WellKnown.SetBigInt(msg, bi);
                return msg;
            }
            if (WellKnown.IsDecimal(fd.MessageType))
            {
                var s = _current.Value;
                bool negative = s.StartsWith('-');
                if (negative) s = s[1..];
                int dotIdx = s.IndexOf('.');
                int scale = 0;
                if (dotIdx >= 0)
                {
                    scale = s.Length - dotIdx - 1;
                    s = s.Remove(dotIdx, 1);
                }
                var unscaled = System.Numerics.BigInteger.Parse(s);
                Advance();
                var msg = currentVal ?? Activator.CreateInstance(type)!;
                WellKnown.SetDecimal(msg, new Protowire.Pb.Decimal(unscaled, scale, negative));
                return msg;
            }
            if (WellKnown.IsBigFloat(fd.MessageType))
            {
                var s = _current.Value;
                bool negative = s.StartsWith('-');
                if (negative) s = s[1..];
                int dotIdx = s.IndexOf('.');
                int scale = 0;
                if (dotIdx >= 0)
                {
                    scale = s.Length - dotIdx - 1;
                    s = s.Remove(dotIdx, 1);
                }
                var unscaled = System.Numerics.BigInteger.Parse(s);
                Advance();
                var msg = currentVal ?? Activator.CreateInstance(type)!;
                WellKnown.SetBigFloat(msg, new Protowire.Pb.BigFloat(unscaled, -scale, (uint)s.Length, negative));
                return msg;
            }
        }

        if (type == typeof(System.Numerics.BigInteger))
        {
            var s = _current.Value.Trim();
            var bi = System.Numerics.BigInteger.Parse(s);
            Advance();
            return bi;
        }
        if (type == typeof(Protowire.Pb.Decimal))
        {
            var s = _current.Value.Trim();
            bool negative = s.StartsWith('-');
            if (negative) s = s[1..];
            int dotIdx = s.IndexOf('.');
            int scale = 0;
            if (dotIdx >= 0)
            {
                scale = s.Length - dotIdx - 1;
                s = s.Remove(dotIdx, 1);
            }
            if (string.IsNullOrEmpty(s)) s = "0";
            var unscaled = System.Numerics.BigInteger.Parse(s);
            Advance();
            return new Protowire.Pb.Decimal(unscaled, scale, negative);
        }
        if (type == typeof(Protowire.Pb.BigFloat))
        {
            // Simple parsing for now: treat as decimal and convert
            var s = _current.Value.Trim();
            bool negative = s.StartsWith('-');
            if (negative) s = s[1..];
            int dotIdx = s.IndexOf('.');
            int scale = 0;
            if (dotIdx >= 0)
            {
                scale = s.Length - dotIdx - 1;
                s = s.Remove(dotIdx, 1);
            }
            if (string.IsNullOrEmpty(s)) s = "0";
            var unscaled = System.Numerics.BigInteger.Parse(s);
            Advance();
            // This is a very simplified BigFloat conversion, usually BigFloat would use binary exponent
            return new Protowire.Pb.BigFloat(unscaled, -scale, (uint)s.Length, negative);
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
        if (type == typeof(int))
        {
            var i = int.Parse(_current.Value);
            Advance();
            return i;
        }
        if (type == typeof(long))
        {
            var l = long.Parse(_current.Value);
            Advance();
            return l;
        }
        if (type == typeof(float))
        {
            var f = float.Parse(_current.Value);
            Advance();
            return f;
        }
        if (type == typeof(double))
        {
            var d = double.Parse(_current.Value);
            Advance();
            return d;
        }
        if (type == typeof(byte[]))
        {
            var bytes = Convert.FromBase64String(_current.Value);
            Advance();
            return bytes;
        }
        if (type == typeof(ByteString))
        {
            var bytes = Convert.FromBase64String(_current.Value);
            Advance();
            return ByteString.CopyFrom(bytes);
        }
        if (type.IsEnum)
        {
            if (_current.Kind == TokenKind.IDENT)
            {
                string enumName = _current.Value;
                Advance();
                if (fd != null && fd.FieldType == FieldType.Enum)
                {
                    var ev = fd.EnumType.FindValueByName(enumName);
                    if (ev != null) return Enum.ToObject(type, ev.Number);
                }
                return Enum.Parse(type, enumName, true);
            }
            else
            {
                var en = int.Parse(_current.Value);
                Advance();
                return Enum.ToObject(type, en);
            }
        }
        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            return DecodeMap(type, fd, currentVal as IDictionary);
        }
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            return DecodeList(type, fd, currentVal as IEnumerable);
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

    private object DecodeAny(object? currentVal)
    {
        if (_current.Kind != TokenKind.LBRACE) throw new PxfException(_current.Pos, "expected '{' for Any");
        Advance();

        string? typeUrl = null;
        if (_current.Kind == TokenKind.AT_TYPE)
        {
            Advance();
            if (_current.Kind != TokenKind.IDENT) throw new PxfException(_current.Pos, "expected type name after @type");
            typeUrl = _current.Value;
            Advance();
        }

        if (typeUrl == null) throw new PxfException(_current.Pos, "missing @type in Any");

        var typeName = typeUrl;
        if (typeName.Contains("/")) typeName = typeName[(typeName.LastIndexOf('/') + 1)..];

        var desc = Registry.Find(typeName);
        if (desc == null) throw new PxfException(_current.Pos, $"type \"{typeName}\" not found in registry");

        var msg = desc.Parser.ParseFrom(Array.Empty<byte>());
        DecodeFields(msg, true);
        
        var any = (IMessage)(currentVal ?? Activator.CreateInstance(typeof(Google.Protobuf.WellKnownTypes.Any))!);
        var packMethod = any.GetType().GetMethod("Pack", new[] { typeof(IMessage) });
        packMethod?.Invoke(any, new object[] { msg });
        
        return any;
    }

    private object DecodeList(Type type, FieldDescriptor? fd, IEnumerable? currentList)
    {
        if (_current.Kind != TokenKind.LBRACKET) throw new PxfException(_current.Pos, "expected '['");
        Advance();
        
        var itemType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
        var list = (currentList as IList) ?? (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
        
        if (list.IsReadOnly && list is not IList) // Handle RepeatedField which is not exactly IList in some contexts but usually is
        {
             // RepeatedField implements IList
        }

        while (_current.Kind != TokenKind.RBRACKET && _current.Kind != TokenKind.EOF)
        {
            list.Add(DecodeValue(itemType, null, null));
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

    private IDictionary DecodeMap(Type type, FieldDescriptor? fd, IDictionary? currentMap)
    {
        if (_current.Kind != TokenKind.LBRACE) throw new PxfException(_current.Pos, "expected '{'");
        Advance();
        var keyType = type.GetGenericArguments()[0];
        var valType = type.GetGenericArguments()[1];
        var map = currentMap ?? (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(keyType, valType))!;

        while (_current.Kind != TokenKind.RBRACE && _current.Kind != TokenKind.EOF)
        {
            var keyStr = _current.Value;
            Advance();
            if (_current.Kind != TokenKind.COLON) throw new PxfException(_current.Pos, "expected ':'");
            Advance();

            var key = Convert.ChangeType(keyStr, keyType);
            var val = DecodeValue(valType, null, null);
            map[key] = val;
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
    /// Descriptor-driven counterpart of <see cref="ApplyDefaultsAndValidatePoco"/>:
    /// validates <c>(pxf.required)</c>, applies <c>(pxf.default)</c>, and recurses
    /// into present, non-null singular message fields. Skips the <c>_null</c>
    /// FieldMask field at the root.
    ///
    /// <para>Mirrors <c>postDecode</c> in <c>protowire-go/encoding/pxf/decode_fast.go</c>.</para>
    /// </summary>
    private void PostDecode(IMessage msg, string pathPrefix)
    {
        var desc = msg.Descriptor;
        foreach (var fd in desc.Fields.InDeclarationOrder())
        {
            if (pathPrefix == "" && _nullMaskFd != null && fd.FieldNumber == _nullMaskFd.FieldNumber)
            {
                continue;
            }
            string path = pathPrefix + fd.Name;
            bool present = _result!.Has(path);
            if (!present)
            {
                if (AnnotationsRuntime.IsRequired(fd))
                {
                    throw new PxfException(Position.Empty, $"required field \"{path}\" is absent");
                }
                var def = AnnotationsRuntime.GetDefault(fd);
                if (def != null)
                {
                    ApplyDefault(msg, fd, def);
                }
            }
            else if ((fd.FieldType == FieldType.Message || fd.FieldType == FieldType.Group)
                     && !fd.IsRepeated && !fd.IsMap && !_result.IsNull(path))
            {
                if (fd.Accessor.GetValue(msg) is IMessage sub)
                {
                    PostDecode(sub, path + ".");
                }
            }
        }
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
    private static void ApplyMessageDefault(IMessage msg, FieldDescriptor fd, string def)
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
            var dt = DateTime.Parse(def, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            WellKnown.SetTimestamp(sub, dt);
        }
        else if (WellKnown.IsDuration(mdesc))
        {
            WellKnown.SetDuration(sub, DurationParser.Parse(def));
        }
        else if (WellKnown.IsBigInt(mdesc))
        {
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
        bool negative = s.StartsWith('-');
        if (negative) s = s[1..];
        int dot = s.IndexOf('.');
        int scale = 0;
        if (dot >= 0)
        {
            scale = s.Length - dot - 1;
            s = s.Remove(dot, 1);
        }
        if (s.Length == 0) s = "0";
        return new Protowire.Pb.Decimal(
            System.Numerics.BigInteger.Parse(s, CultureInfo.InvariantCulture), scale, negative);
    }

    private static Protowire.Pb.BigFloat ParseBigFloatLiteral(string s)
    {
        bool negative = s.StartsWith('-');
        if (negative) s = s[1..];
        int dot = s.IndexOf('.');
        int scale = 0;
        if (dot >= 0)
        {
            scale = s.Length - dot - 1;
            s = s.Remove(dot, 1);
        }
        if (s.Length == 0) s = "0";
        return new Protowire.Pb.BigFloat(
            System.Numerics.BigInteger.Parse(s, CultureInfo.InvariantCulture),
            -scale, (uint)s.Length, negative);
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

    private void SkipValue()
    {
        switch (_current.Kind)
        {
            case TokenKind.LBRACE:
                Advance();
                SkipBraced();
                break;
            case TokenKind.LBRACKET:
                Advance();
                SkipBracketed();
                break;
            default:
                Advance();
                break;
        }
    }

    private void SkipBraced()
    {
        int depth = 1;
        while (depth > 0 && _current.Kind != TokenKind.EOF)
        {
            if (_current.Kind == TokenKind.LBRACE) depth++;
            else if (_current.Kind == TokenKind.RBRACE) depth--;
            Advance();
        }
    }

    private void SkipBracketed()
    {
        int depth = 1;
        while (depth > 0 && _current.Kind != TokenKind.EOF)
        {
            if (_current.Kind == TokenKind.LBRACKET) depth++;
            else if (_current.Kind == TokenKind.RBRACKET) depth--;
            Advance();
        }
    }
}
