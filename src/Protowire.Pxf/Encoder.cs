using System.Text;
using System.Collections;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

/// <summary>
/// Provides methods for encoding C# objects into PXF (Protowire Exchange Format) text format.
/// </summary>
public class Encoder
{
    private readonly StringBuilder _sb = new();
    private readonly string _indent = "  ";
    private readonly bool _emitDefaults = false;
    public TypeRegistry Registry { get; set; } = TypeRegistry.Empty;

    /// <summary>
    /// Marshals the specified object into a PXF-formatted string.
    /// </summary>
    /// <param name="obj">The object to marshal.</param>
    /// <returns>A PXF-formatted string representation of the object.</returns>
    public string Marshal(object obj)
    {
        _sb.Clear();
        EncodeMessage(obj, 0);
        return _sb.ToString();
    }

    private void EncodeAny(string name, object any, int level)
    {
        var typeUrl = (string)any.GetType().GetProperty("TypeUrl")?.GetValue(any)!;
        var typeName = typeUrl;
        if (typeName.Contains("/")) typeName = typeName[(typeName.LastIndexOf('/') + 1)..];
        
        var desc = Registry.Find(typeName);
        if (desc == null)
        {
            // If not in registry, we can't unpack it easily to PXF.
            // Fallback to regular message encoding which might show TypeUrl and Value fields.
            WriteIndent(level);
            _sb.Append(name).Append(" {\n");
            EncodeMessage(any, level + 1);
            WriteIndent(level);
            _sb.Append("}\n");
            return;
        }

        var msg = desc.Parser.ParseFrom(Array.Empty<byte>());
        var unpackMethod = any.GetType().GetMethod("Unpack", new[] { typeof(MessageDescriptor) });
        // Wait, Unpack in Google.Protobuf returns IMessage.
        var unpacked = any.GetType().GetMethod("Unpack", new Type[] { })?.MakeGenericMethod(msg.GetType()).Invoke(any, null) as IMessage;
        
        if (unpacked == null)
        {
             // Fallback
            WriteIndent(level);
            _sb.Append(name).Append(" {\n");
            EncodeMessage(any, level + 1);
            WriteIndent(level);
            _sb.Append("}\n");
            return;
        }

        WriteIndent(level);
        _sb.Append(name).Append(" {\n");
        WriteIndent(level + 1);
        _sb.Append("@type ").Append(typeUrl).Append('\n');
        EncodeMessage(unpacked, level + 1);
        WriteIndent(level);
        _sb.Append("}\n");
    }

    private void WriteIndent(int level)
    {
        for (int i = 0; i < level; i++) _sb.Append(_indent);
    }

    private void EncodeMessage(object obj, int level)
    {
        var type = obj.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);

        // Check for _null FieldMask
        var nullMaskProp = props.FirstOrDefault(p => p.Name == "_null" || p.Name == "Null");
        var nullFields = new HashSet<string>();
        if (nullMaskProp != null && nullMaskProp.GetValue(obj) is IMessage mask)
        {
             var paths = mask.GetType().GetProperty("Paths")?.GetValue(mask) as IList;
             if (paths != null)
             {
                 foreach (var path in paths) nullFields.Add(path?.ToString() ?? "");
             }
        }

        foreach (var prop in props)
        {
            if (prop.Name == "_null" || prop.Name == "Null") continue;
            var val = prop.GetValue(obj);
            
            if (nullFields.Contains(prop.Name.ToLowerInvariant()) || nullFields.Contains(ToSnakeCase(prop.Name)))
            {
                WriteIndent(level);
                _sb.Append(ToSnakeCase(prop.Name)).Append(" = null\n");
                continue;
            }

            if (!_emitDefaults && IsDefault(prop.PropertyType, val)) continue;
            EncodeMember(ToSnakeCase(prop.Name), prop.PropertyType, val, level);
        }

        foreach (var field in fields)
        {
            if (field.Name == "_null" || field.Name == "Null") continue;
            var val = field.GetValue(obj);

            if (nullFields.Contains(field.Name.ToLowerInvariant()) || nullFields.Contains(ToSnakeCase(field.Name)))
            {
                WriteIndent(level);
                _sb.Append(ToSnakeCase(field.Name)).Append(" = null\n");
                continue;
            }

            if (!_emitDefaults && IsDefault(field.FieldType, val)) continue;
            EncodeMember(ToSnakeCase(field.Name), field.FieldType, val, level);
        }
    }

    private void EncodeMember(string name, Type type, object? val, int level)
    {
        // Handle WKT
        if (val != null)
        {
            var typeName = val.GetType().FullName;
            if (typeName == "Google.Protobuf.WellKnownTypes.Timestamp" || val is DateTime)
            {
                var dt = val is DateTime d ? d : ReadTimestamp(val);
                WriteIndent(level);
                _sb.Append(name).Append(" = ").Append(dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")).Append('\n');
                return;
            }
            if (typeName == "Google.Protobuf.WellKnownTypes.Duration" || val is TimeSpan)
            {
                var ts = val is TimeSpan t ? t : ReadDuration(val);
                WriteIndent(level);
                _sb.Append(name).Append(" = ").Append(DurationParser.Format(ts)).Append('\n');
                return;
            }
            if (typeName == "Google.Protobuf.WellKnownTypes.Any")
            {
                EncodeAny(name, val, level);
                return;
            }
        }

        if (typeof(IDictionary).IsAssignableFrom(type))
        {
            EncodeMapField(name, (IDictionary?)val, level);
            return;
        }

        if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string) && type != typeof(byte[]))
        {
            EncodeListField(name, (IEnumerable?)val, level);
            return;
        }

        if (val != null && !type.IsValueType && type != typeof(string) && type != typeof(byte[]))
        {
            // Nested message
            WriteIndent(level);
            _sb.Append(name).Append(" {\n");
            EncodeMessage(val, level + 1);
            WriteIndent(level);
            _sb.Append("}\n");
            return;
        }

        WriteIndent(level);
        _sb.Append(name).Append(" = ");
        WriteScalar(val);
        _sb.Append('\n');
    }

    private DateTime ReadTimestamp(object obj)
    {
        long seconds = (long)(obj.GetType().GetProperty("Seconds")?.GetValue(obj) ?? 0L);
        int nanos = (int)(obj.GetType().GetProperty("Nanos")?.GetValue(obj) ?? 0);
        return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanos / 100);
    }

    private TimeSpan ReadDuration(object obj)
    {
        long seconds = (long)(obj.GetType().GetProperty("Seconds")?.GetValue(obj) ?? 0L);
        int nanos = (int)(obj.GetType().GetProperty("Nanos")?.GetValue(obj) ?? 0);
        return TimeSpan.FromSeconds(seconds).Add(TimeSpan.FromTicks(nanos / 100));
    }

    private bool IsDefault(Type type, object? val)
    {
        if (val == null) return true;
        if (type == typeof(string)) return (string)val == "";
        if (type == typeof(bool)) return !(bool)val;
        if (type == typeof(int)) return (int)val == 0;
        if (type == typeof(long)) return (long)val == 0;
        if (type == typeof(float)) return (float)val == 0;
        if (type == typeof(double)) return (double)val == 0;
        if (type == typeof(byte[])) return ((byte[])val).Length == 0;
        if (type == typeof(ByteString)) return ((ByteString)val).Length == 0;
        if (type.IsEnum) return (int)val == 0;
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            if (val is ICollection c) return c.Count == 0;
            return false;
        }
        return false;
    }

    private void EncodeListField(string name, IEnumerable? list, int level)
    {
        if (list == null) return;
        WriteIndent(level);
        _sb.Append(name).Append(" = [\n");
        foreach (var elem in list)
        {
            if (elem == null) continue;
            WriteIndent(level + 1);
            if (!elem.GetType().IsValueType && elem is not string && elem is not byte[] && elem is not ByteString)
            {
                _sb.Append("{\n");
                EncodeMessage(elem, level + 2);
                WriteIndent(level + 1);
                _sb.Append('}');
            }
            else
            {
                WriteScalar(elem);
            }
            _sb.Append(",\n");
        }
        if (_sb.Length > 2 && _sb[^2] == ',') _sb.Remove(_sb.Length - 2, 1);
        WriteIndent(level);
        _sb.Append("]\n");
    }

    private void EncodeMapField(string name, IDictionary? map, int level)
    {
        if (map == null) return;
        WriteIndent(level);
        _sb.Append(name).Append(" = {\n");
        foreach (DictionaryEntry entry in map)
        {
            WriteIndent(level + 1);
            _sb.Append(entry.Key).Append(": ");
            if (entry.Value != null && !entry.Value.GetType().IsValueType && entry.Value is not string && entry.Value is not byte[] && entry.Value is not ByteString)
            {
                _sb.Append("{\n");
                EncodeMessage(entry.Value, level + 2);
                WriteIndent(level + 1);
                _sb.Append("}\n");
            }
            else
            {
                WriteScalar(entry.Value);
                _sb.Append('\n');
            }
        }
        WriteIndent(level);
        _sb.Append("}\n");
    }

    private void WriteScalar(object? val)
    {
        if (val == null)
        {
            _sb.Append("null");
            return;
        }
        if (val is string s)
        {
            WriteQuotedString(_sb, s);
        }
        else if (val is bool b)
        {
            _sb.Append(b ? "true" : "false");
        }
        else if (val is byte[] bytes)
        {
            _sb.Append("b\"").Append(Convert.ToBase64String(bytes)).Append('"');
        }
        else if (val is ByteString bs)
        {
            _sb.Append("b\"").Append(bs.ToBase64()).Append('"');
        }
        else
        {
            _sb.Append(val);
        }
    }

    private string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                sb.Append(name[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Emits a quoted PXF string literal: the simple escape mnemonics for
    /// `"` `\` `\n` `\r` `\t`, plus `\xHH` for any other code unit under
    /// 0x20. Code units &gt;= 0x20 pass through literally so valid UTF-8
    /// stays UTF-8. Mirrors protowire-go/encoding/pxf/encode.go:writeQuotedString.
    /// </summary>
    internal static void WriteQuotedString(StringBuilder sb, string s)
    {
        const string Hex = "0123456789abcdef";
        sb.Append('"');
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\x").Append(Hex[(ch >> 4) & 0xF]).Append(Hex[ch & 0xF]);
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
