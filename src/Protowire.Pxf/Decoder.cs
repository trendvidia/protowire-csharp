using System.Reflection;
using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

/// <summary>
/// Provides methods for decoding PXF (Protowire Exchange Format) text into C# objects.
/// </summary>
public class Decoder
{
    private Lexer _lex = null!;
    private Token _current = null!;
    public TypeRegistry Registry { get; set; } = TypeRegistry.Empty;

    /// <summary>
    /// Unmarshals a PXF-formatted string into the specified object.
    /// </summary>
    /// <param name="data">The PXF-formatted string.</param>
    /// <param name="obj">The object to unmarshal into.</param>
    public void Unmarshal(string data, object obj)
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
        ApplyDefaultsAndValidate(obj);
    }

    private void Advance()
    {
        while (true)
        {
            _current = _lex.Next();
            if (_current.Kind != TokenKind.COMMENT && _current.Kind != TokenKind.NEWLINE)
            {
                return;
            }
        }
    }

    private void DecodeFields(object obj, bool inBlock)
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
                        Advance();
                        continue;
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
                    DecodeFields(subObj!, true);
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

    private void ApplyDefaultsAndValidate(object obj)
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
