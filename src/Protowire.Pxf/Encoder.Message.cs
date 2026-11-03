// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Collections;
using System.Numerics;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

// The descriptor-driven writer, for generated messages. Mirrors encoder in
// protowire-go/encoding/pxf/encode.go, whose text every port's is compared
// against (testdata/text in the spec repo).
public partial class Encoder
{
    private FieldDescriptor? _nullMaskFd;
    private HashSet<string>? _nullSet;
    private string _pathPrefix = "";

    /// <summary>Write fields with zero values too.</summary>
    public bool EmitDefaults { get => _emitDefaults; set => _emitDefaults = value; }

    /// <summary>Write the nulls a decode recorded, for a message with no <c>_null</c> mask.</summary>
    public Result? NullFields { get; set; }

    /// <summary>The deepest document to write, counted as a decoder counts it.</summary>
    public int MaxNestingDepth { get; set; } = PxfLimits.Default.MaxNestingDepth;

    private void WriteRoot(IMessage msg)
    {
        _pathPrefix = "";
        _nullSet = null;
        _nullMaskFd = AnnotationsRuntime.FindNullMaskField(msg.Descriptor);
        if (_nullMaskFd != null && _nullMaskFd.Accessor.GetValue(msg) is IMessage mask)
        {
            var paths = (IEnumerable)mask.Descriptor.FindFieldByName("paths").Accessor.GetValue(mask);
            _nullSet = paths.Cast<string>().ToHashSet();
        }
        else if (NullFields != null)
        {
            _nullSet = NullFields.NullFields().ToHashSet();
        }
        WriteMessage(msg, 0, null);
    }

    private void CheckDepth(int level)
    {
        if (level > MaxNestingDepth)
        {
            throw new NestingException(MaxNestingDepth);
        }
    }

    private sealed class NestingException(int limit)
        : PxfException(new Position(1, 1, 0), $"nesting depth exceeds MaxNestingDepth={limit}");

    private static bool IsMessage(FieldDescriptor fd) => fd.FieldType is FieldType.Message or FieldType.Group;

    private static bool Has(IMessage msg, FieldDescriptor fd)
    {
        var value = fd.Accessor.GetValue(msg);
        if (fd.IsMap) return ((IDictionary)value).Count > 0;
        if (fd.IsRepeated) return ((IList)value).Count > 0;
        if (fd.HasPresence) return fd.Accessor.HasValue(msg);
        return value switch
        {
            string s => s.Length > 0,
            ByteString b => b.Length > 0,
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            uint u => u != 0,
            ulong u => u != 0,
            // By bits, so a negative zero is written.
            float f => BitConverter.SingleToInt32Bits(f) != 0,
            double d => BitConverter.DoubleToInt64Bits(d) != 0,
            _ => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) != 0,
        };
    }

    private void WriteMessage(IMessage msg, int level, FieldDescriptor? skipKey)
    {
        CheckDepth(level);
        foreach (var fd in msg.Descriptor.Fields.InDeclarationOrder())
        {
            if (skipKey != null && fd.FieldNumber == skipKey.FieldNumber) continue;
            // Only at the root: a body's field of the same number is data.
            if (_nullMaskFd != null && level == 0 && fd.FieldNumber == _nullMaskFd.FieldNumber) continue;

            string path = _pathPrefix + fd.Name;
            if (_nullSet != null && _nullSet.Contains(path) && fd.HasPresence && !fd.IsRepeated && !fd.IsMap && !fd.IsRequired)
            {
                WritePrefix(level, fd.Name);
                _sb.Append("null\n");
                continue;
            }
            if (!_emitDefaults && !Has(msg, fd)) continue;

            var value = fd.Accessor.GetValue(msg);
            if (fd.IsMap)
            {
                WriteMap(fd, (IDictionary)value, level);
            }
            else if (fd.IsRepeated)
            {
                WriteList(fd, (IList)value, level);
            }
            else if (IsMessage(fd))
            {
                if (value == null) continue;
                WriteMessageField(fd, value, level);
            }
            else
            {
                WritePrefix(level, fd.Name);
                WriteValue(fd, value);
                _sb.Append('\n');
            }
        }
    }

    private void WritePrefix(int level, string name)
    {
        WriteIndent(level);
        _sb.Append(name).Append(" = ");
    }

    private void WriteMessageField(FieldDescriptor fd, object value, int level)
    {
        var mdesc = fd.MessageType;
        if (IsLiteral(mdesc, value))
        {
            WritePrefix(level, fd.Name);
            WriteLiteral(mdesc, value);
            _sb.Append('\n');
            return;
        }
        var sub = (IMessage)value;
        if (WellKnown.IsAny(mdesc) && TryWriteAny(fd.Name, sub, level)) return;
        WriteBlock(fd.Name, sub, level);
    }

    // A message a hand-written class holds, with only its value's descriptor.
    private void WriteNamedMessage(string name, IMessage sub, int level)
    {
        var mdesc = sub.Descriptor;
        if (IsLiteral(mdesc, sub))
        {
            WritePrefix(level, name);
            WriteLiteral(mdesc, sub);
            _sb.Append('\n');
            return;
        }
        if (WellKnown.IsAny(mdesc) && TryWriteAny(name, sub, level)) return;
        WriteBlock(name, sub, level);
    }

    private void WriteBlock(string name, IMessage sub, int level)
    {
        WriteIndent(level);
        _sb.Append(name).Append(" {\n");
        string saved = Extend(name, "");
        WriteMessage(sub, level + 1, null);
        _pathPrefix = saved;
        WriteIndent(level);
        _sb.Append("}\n");
    }

    private string Extend(string name, string suffix)
    {
        string saved = _pathPrefix;
        if (_nullSet != null) _pathPrefix = $"{saved}{name}{suffix}.";
        return saved;
    }

    // A wrapper's value is the bare scalar generated code stores. A Secret
    // with metadata needs its block, and a Duration past the literal's range
    // does too.
    private static bool IsLiteral(MessageDescriptor mdesc, object value)
    {
        if (WellKnown.IsWrapper(mdesc)) return true;
        var sub = (IMessage)value;
        if (WellKnown.IsSecret(mdesc)) return !SecretHasMetadata(sub);
        if (WellKnown.IsDuration(mdesc)) return DurationFitsLiteral(sub);
        return WellKnown.IsTimestamp(mdesc) || WellKnown.IsBigInt(mdesc)
            || WellKnown.IsDecimal(mdesc) || WellKnown.IsBigFloat(mdesc);
    }

    private static bool SecretHasMetadata(IMessage msg) =>
        msg.Descriptor.Fields.InDeclarationOrder().Any(f => f.Name is "hint" or "fingerprint" && (string)f.Accessor.GetValue(msg) != "");

    private static (long Seconds, int Nanos) SecondsNanos(IMessage msg) => (
        (long)msg.Descriptor.FindFieldByName("seconds").Accessor.GetValue(msg),
        (int)msg.Descriptor.FindFieldByName("nanos").Accessor.GetValue(msg));

    // A literal holds 64 bits of nanoseconds, about 292 years, and the type
    // reaches about 10,000.
    private static bool DurationFitsLiteral(IMessage msg)
    {
        var (seconds, nanos) = SecondsNanos(msg);
        var total = new BigInteger(seconds) * 1_000_000_000 + nanos;
        return total >= long.MinValue && total <= long.MaxValue;
    }

    private void WriteLiteral(MessageDescriptor mdesc, object value)
    {
        if (WellKnown.IsWrapper(mdesc))
        {
            WriteValue(mdesc.FindFieldByName("value"), value);
            return;
        }
        var msg = (IMessage)value;
        var limits = PxfLimits.Default;
        try
        {
            if (WellKnown.IsTimestamp(mdesc))
            {
                var (seconds, nanos) = SecondsNanos(msg);
                _sb.Append(Rfc3339.Format(seconds, nanos));
            }
            else if (WellKnown.IsDuration(mdesc))
            {
                var (seconds, nanos) = SecondsNanos(msg);
                _sb.Append(GoFormat.Duration(unchecked(seconds * 1_000_000_000 + nanos)));
            }
            else if (WellKnown.IsSecret(mdesc))
            {
                WriteValue(mdesc.FindFieldByName("value"), mdesc.FindFieldByName("value").Accessor.GetValue(msg));
            }
            else if (WellKnown.IsBigInt(mdesc))
            {
                var abs = Bytes(msg, "abs");
                var v = new BigInteger(abs, isUnsigned: true, isBigEndian: true);
                if (Bool(msg, "negative")) v = -v;
                _sb.Append(WellKnown.BoundLiteral("pxf.BigInt", v.ToString(System.Globalization.CultureInfo.InvariantCulture), limits.MaxNumericLiteralDigits));
            }
            else if (WellKnown.IsDecimal(mdesc))
            {
                var unscaled = new BigInteger(Bytes(msg, "unscaled"), isUnsigned: true, isBigEndian: true);
                int scale = (int)mdesc.FindFieldByName("scale").Accessor.GetValue(msg);
                _sb.Append(WellKnown.DecimalLiteral(unscaled, scale, Bool(msg, "negative"), limits.MaxNumericLiteralDigits));
            }
            else
            {
                var bf = new Protowire.Pb.BigFloat(
                    new BigInteger(Bytes(msg, "mantissa"), isUnsigned: true, isBigEndian: true),
                    (int)mdesc.FindFieldByName("exponent").Accessor.GetValue(msg),
                    (uint)mdesc.FindFieldByName("prec").Accessor.GetValue(msg),
                    Bool(msg, "negative"));
                _sb.Append(BigFloatText.Format(bf, limits.MaxNumericLiteralDigits, limits.MaxBigFloatExponent));
            }
        }
        catch (FormatException e)
        {
            throw new PxfException(new Position(1, 1, 0), e.Message);
        }
    }

    private static ReadOnlySpan<byte> Bytes(IMessage msg, string field) =>
        ((ByteString)msg.Descriptor.FindFieldByName(field).Accessor.GetValue(msg)).Span;

    private static bool Bool(IMessage msg, string field) =>
        (bool)msg.Descriptor.FindFieldByName(field).Accessor.GetValue(msg);

    private void WriteList(FieldDescriptor fd, IList list, int level)
    {
        if (list.Count == 0 && !_emitDefaults) return;
        // Keyed form whenever every key is present, non-empty and distinct;
        // anything else can only be written anonymously.
        if (Keyed.KeyField(fd) is { } keyFd && list.Count > 0 && KeyedFormEligible(list, keyFd))
        {
            WriteKeyedList(fd, list, keyFd, level);
            return;
        }

        CheckDepth(level + 1);
        WritePrefix(level, fd.Name);
        _sb.Append("[\n");
        for (int i = 0; i < list.Count; i++)
        {
            var elem = list[i]!;
            WriteIndent(level + 1);
            if (!IsMessage(fd))
            {
                WriteValue(fd, elem);
            }
            else if (IsLiteral(fd.MessageType, elem))
            {
                WriteLiteral(fd.MessageType, elem);
            }
            else
            {
                _sb.Append("{\n");
                string saved = Extend(fd.Name, $"[{i}]");
                WriteMessage((IMessage)elem, level + 2, null);
                _pathPrefix = saved;
                WriteIndent(level + 1);
                _sb.Append('}');
            }
            if (i < list.Count - 1) _sb.Append(',');
            _sb.Append('\n');
        }
        WriteIndent(level);
        _sb.Append("]\n");
    }

    private static bool KeyedFormEligible(IList list, FieldDescriptor keyFd)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var elem in list)
        {
            var key = (string)keyFd.Accessor.GetValue((IMessage)elem!);
            if (key.Length == 0 || !seen.Add(key)) return false;
        }
        return true;
    }

    private void WriteKeyedList(FieldDescriptor fd, IList list, FieldDescriptor keyFd, int level)
    {
        WriteIndent(level);
        _sb.Append(fd.Name).Append(" {\n");
        for (int i = 0; i < list.Count; i++)
        {
            var element = (IMessage)list[i]!;
            var key = (string)keyFd.Accessor.GetValue(element);
            WriteIndent(level + 1);
            if (Keyed.IdentSafeEntryName(key)) _sb.Append(key);
            else WriteQuotedString(_sb, key);
            _sb.Append(" {\n");
            string saved = Extend(fd.Name, $"[{i}]");
            WriteMessage(element, level + 2, keyFd);
            _pathPrefix = saved;
            WriteIndent(level + 1);
            _sb.Append("}\n");
        }
        WriteIndent(level);
        _sb.Append("}\n");
    }

    private void WriteMap(FieldDescriptor fd, IDictionary map, int level)
    {
        if (map.Count == 0 && !_emitDefaults) return;
        CheckDepth(level + 1);
        WritePrefix(level, fd.Name);
        _sb.Append("{\n");

        var keyFd = fd.MessageType.FindFieldByNumber(1);
        var valueFd = fd.MessageType.FindFieldByNumber(2);
        var entries = new List<(string Key, object RawKey, object Value)>(map.Count);
        foreach (DictionaryEntry e in map)
        {
            entries.Add((MapKeyText(keyFd, e.Key), e.Key, e.Value!));
        }
        // By written form, in UTF-8 byte order: 10 sorts before 9.
        entries.Sort((a, b) => Encoding.UTF8.GetBytes(a.Key).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b.Key)));

        foreach (var (key, rawKey, value) in entries)
        {
            WriteIndent(level + 1);
            _sb.Append(key).Append(": ");
            if (!IsMessage(valueFd))
            {
                WriteValue(valueFd, value);
            }
            else if (IsLiteral(valueFd.MessageType, value))
            {
                WriteLiteral(valueFd.MessageType, value);
            }
            else
            {
                _sb.Append("{\n");
                string saved = Extend(fd.Name, KeySuffix(rawKey));
                WriteMessage((IMessage)value, level + 2, null);
                _pathPrefix = saved;
                WriteIndent(level + 1);
                _sb.Append('}');
            }
            _sb.Append('\n');
        }
        WriteIndent(level);
        _sb.Append("}\n");
    }

    private static string KeySuffix(object key) => key switch
    {
        string s => $"[{Quoted(s)}]",
        bool b => b ? "[true]" : "[false]",
        _ => $"[{Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture)}]",
    };

    private static string Quoted(string s)
    {
        var sb = new StringBuilder();
        GoFormat.Quote(sb, s);
        return sb.ToString();
    }

    private static string MapKeyText(FieldDescriptor keyFd, object key) => key switch
    {
        string s => Keyed.IdentSafeEntryName(s) ? s : Quoted(s),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture)!,
    };

    private void WriteValue(FieldDescriptor fd, object value)
    {
        switch (value)
        {
            case string s:
                WriteQuotedString(_sb, s);
                break;
            case bool b:
                _sb.Append(b ? "true" : "false");
                break;
            case ByteString bytes:
                _sb.Append("b\"").Append(bytes.ToBase64()).Append('"');
                break;
            case float f:
                _sb.Append(GoFormat.Float(f, single: true));
                break;
            case double d:
                _sb.Append(GoFormat.Float(d, single: false));
                break;
            case int or long or uint or ulong:
                _sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                int number = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                var ev = fd.EnumType.FindValueByNumber(number);
                if (ev != null) _sb.Append(ev.Name);
                else _sb.Append(number);
                break;
        }
    }

    /// <summary>
    /// Writes an Any as a type entry and a body, or returns false, having
    /// written nothing, to leave it to the block of its own fields: when the
    /// type does not resolve, the value does not parse, or the body would not
    /// read back as the same value. An Any at the depth limit is not unpacked,
    /// which also bounds a chain of Anys.
    /// </summary>
    private bool TryWriteAny(string name, IMessage any, int level)
    {
        string url = (string)any.Descriptor.FindFieldByName("type_url").Accessor.GetValue(any);
        var value = (ByteString)any.Descriptor.FindFieldByName("value").Accessor.GetValue(any);
        if (url.Length == 0 || level >= MaxNestingDepth) return false;
        var desc = Registry.Find(url[(url.LastIndexOf('/') + 1)..]);
        if (desc == null) return false;

        IMessage inner;
        try
        {
            // The parser's own recursion limit is 100 whatever this
            // encoder's is; under a raised MaxNestingDepth a deeper value
            // still fits a body, so it parses to that limit instead.
            using var stream = new MemoryStream(value.ToByteArray(), writable: false);
            inner = desc.Parser.ParseFrom(CodedInputStream.CreateWithLimits(stream, int.MaxValue, MaxNestingDepth));
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }

        var outer = _sb;
        _sb = new StringBuilder();
        string saved = Extend(name, "");
        string body;
        try
        {
            WriteMessage(inner, level + 1, null);
            body = _sb.ToString();
        }
        catch (NestingException)
        {
            return false; // too deep to unpack here; its bytes are not
        }
        finally
        {
            _pathPrefix = saved;
            _sb = outer;
        }
        if (!Decoder.AnyBodyReadsBack(body, desc, Registry, level, MaxNestingDepth, value)) return false;

        WriteIndent(level);
        _sb.Append(name).Append(" {\n");
        WriteIndent(level + 1);
        _sb.Append("@type = ");
        WriteQuotedString(_sb, url);
        _sb.Append('\n').Append(body);
        WriteIndent(level);
        _sb.Append("}\n");
        return true;
    }
}
