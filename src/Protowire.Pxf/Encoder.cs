// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Text;
using System.Collections;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

/// <summary>
/// Provides methods for encoding C# objects into PXF (Protowire Exchange Format) text format.
/// </summary>
public partial class Encoder
{
    private StringBuilder _sb = new();
    private readonly string _indent = "  ";
    private bool _emitDefaults;
    public TypeRegistry Registry { get; set; } = TypeRegistry.Empty;

    /// <summary>
    /// Marshals the specified object into a PXF-formatted string.
    /// </summary>
    /// <param name="obj">The object to marshal.</param>
    /// <returns>A PXF-formatted string representation of the object.</returns>
    public string Marshal(object obj)
    {
        _sb.Clear();
        if (obj is IMessage message)
        {
            WriteRoot(message);
        }
        else
        {
            EncodeMessage(obj, 0);
        }
        return _sb.ToString();
    }

    private void WriteIndent(int level)
    {
        for (int i = 0; i < level; i++) _sb.Append(_indent);
    }

    // The path for a hand-written class: no descriptor, so members are written
    // in reflection order and a zero value is omitted. The indentation level is
    // the depth a decoder counts (a block, a list and a map are each one
    // descent), so the same MaxNestingDepth check as the descriptor path
    // applies, and a cycle of references fails instead of overflowing the stack.
    private void EncodeMessage(object obj, int level)
    {
        CheckDepth(level);
        var type = obj.GetType();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var val = prop.GetValue(obj);
            if (!_emitDefaults && IsDefault(prop.PropertyType, val)) continue;
            EncodeMember(ToSnakeCase(prop.Name), prop.PropertyType, val, level);
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var val = field.GetValue(obj);
            if (!_emitDefaults && IsDefault(field.FieldType, val)) continue;
            EncodeMember(ToSnakeCase(field.Name), field.FieldType, val, level);
        }
    }

    private void EncodeMember(string name, Type type, object? val, int level)
    {
        if (val is IMessage message)
        {
            WriteNamedMessage(name, message, level);
            return;
        }
        if (val is DateTime dt)
        {
            WriteIndent(level);
            _sb.Append(name).Append(" = ").Append(dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")).Append('\n');
            return;
        }
        if (val is TimeSpan ts)
        {
            WriteIndent(level);
            _sb.Append(name).Append(" = ").Append(GoFormat.Duration(ts.Ticks * 100)).Append('\n');
            return;
        }
        if (val is System.Numerics.BigInteger or Protowire.Pb.Decimal or Protowire.Pb.BigFloat)
        {
            WriteIndent(level);
            _sb.Append(name).Append(" = ").Append(BigNumberLiteral(val)).Append('\n');
            return;
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
        CheckDepth(level + 1);
        WriteIndent(level);
        _sb.Append(name).Append(" = [\n");
        foreach (var elem in list)
        {
            if (elem == null) continue;
            WriteIndent(level + 1);
            if (elem is System.Numerics.BigInteger or Protowire.Pb.Decimal or Protowire.Pb.BigFloat)
            {
                _sb.Append(BigNumberLiteral(elem));
            }
            else if (!elem.GetType().IsValueType && elem is not string && elem is not byte[] && elem is not ByteString)
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
        CheckDepth(level + 1);
        WriteIndent(level);
        _sb.Append(name).Append(" = {\n");
        foreach (DictionaryEntry entry in map)
        {
            WriteIndent(level + 1);
            if (entry.Key is string key && !Keyed.IdentSafeEntryName(key)) GoFormat.Quote(_sb, key);
            else if (entry.Key is bool flag) _sb.Append(flag ? "true" : "false");
            else _sb.Append(entry.Key);
            _sb.Append(": ");
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
            _sb.Append(Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static string BigNumberLiteral(object val) => val switch
    {
        System.Numerics.BigInteger bi => bi.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Protowire.Pb.Decimal dec => WellKnown.DecimalLiteral(dec.Unscaled, dec.Scale, dec.Negative, PxfLimits.Default.MaxNumericLiteralDigits),
        Protowire.Pb.BigFloat bf => BigFloatText.Format(bf, PxfLimits.Default.MaxNumericLiteralDigits, PxfLimits.Default.MaxBigFloatExponent),
        _ => throw new ArgumentException(val.GetType().Name),
    };

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
