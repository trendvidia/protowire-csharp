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

        if (_current.Kind == TokenKind.AT_TYPE)
        {
            Advance();
            if (_current.Kind != TokenKind.IDENT)
            {
                throw new PxfException(_current.Pos, "expected type name after @type");
            }
            Advance();
        }

        DecodeFields(obj, false);
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
            string key = _current.Value;
            Advance();

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
