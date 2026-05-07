// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using Google.Protobuf;

namespace Protowire.Pb;

/// <summary>
/// Provides methods for marshaling and unmarshaling objects to and from Protobuf binary format.
/// </summary>
public static class Pb
{
    // HARDENING.md § Mandatory limits — bounds native call-stack growth on
    // attacker input. Matches the cross-port default. The depth counter
    // must survive nested submessage construction; we thread an explicit
    // counter rather than relying on CodedInputStream's RecursionLimit,
    // since that resets every time a fresh stream is constructed.
    private const int MaxNestingDepth = 100;

    private static readonly ConcurrentDictionary<Type, StructInfo> StructCache = new();

    /// <summary>
    /// Marshals the specified object into a Protobuf binary-encoded byte array.
    /// </summary>
    /// <param name="v">The object to marshal.</param>
    /// <returns>A byte array containing the Protobuf binary-encoded data.</returns>
    public static byte[] Marshal(object v)
    {
        var type = v.GetType();
        var info = GetStructInfo(type);
        using var stream = new MemoryStream();
        using var output = new CodedOutputStream(stream);
        MarshalStruct(output, v, info);
        output.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Unmarshals Protobuf binary-encoded data into the specified object.
    /// </summary>
    /// <param name="data">The byte array containing the Protobuf binary-encoded data.</param>
    /// <param name="v">The object to unmarshal into.</param>
    public static void Unmarshal(byte[] data, object v)
    {
        var type = v.GetType();
        var info = GetStructInfo(type);
        var input = new CodedInputStream(data);
        UnmarshalStruct(input, v, info, depth: 0);
    }

    private static StructInfo GetStructInfo(Type type)
    {
        return StructCache.GetOrAdd(type, t =>
        {
            var fields = new List<FieldInfoData>();
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = prop.GetCustomAttribute<ProtowireAttribute>();
                if (attr != null)
                {
                    fields.Add(new FieldInfoData(prop, attr.Tag, attr.ZigZag, attr.Oneof));
                }
            }
            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = field.GetCustomAttribute<ProtowireAttribute>();
                if (attr != null)
                {
                    fields.Add(new FieldInfoData(field, attr.Tag, attr.ZigZag, attr.Oneof));
                }
            }
            return new StructInfo(fields);
        });
    }

    private static void MarshalStruct(CodedOutputStream output, object v, StructInfo info)
    {
        var setOneofs = new HashSet<string>();
        // Process in tag order to be consistent
        foreach (var field in info.Fields.OrderBy(f => f.Tag))
        {
            var val = field.GetValue(v);
            if (val is null || IsZero(val)) continue;

            if (!string.IsNullOrEmpty(field.Oneof))
            {
                if (setOneofs.Contains(field.Oneof)) continue; // Only first one wins in this simple implementation
                setOneofs.Add(field.Oneof);
            }

            MarshalField(output, (uint)field.Tag, val, field.ZigZag);
        }
    }

    private static void MarshalField(CodedOutputStream output, uint tag, object val, bool zigzag)
    {
        if (val is bool b)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.Varint));
            output.WriteBool(b);
        }
        else if (val is int i)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.Varint));
            if (zigzag) output.WriteSInt32(i);
            else output.WriteInt32(i);
        }
        else if (val is long l)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.Varint));
            if (zigzag) output.WriteSInt64(l);
            else output.WriteInt64(l);
        }
        else if (val is uint ui)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.Varint));
            output.WriteUInt32(ui);
        }
        else if (val is ulong ul)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.Varint));
            output.WriteUInt64(ul);
        }
        else if (val is float f)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.Fixed32));
            output.WriteFloat(f);
        }
        else if (val is double d)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.Fixed64));
            output.WriteDouble(d);
        }
        else if (val is string s)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.LengthDelimited));
            output.WriteString(s);
        }
        else if (val is byte[] bytes)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(ByteString.CopyFrom(bytes));
        }
        else if (val is ByteString bs)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(bs);
        }
        else if (val is BigInteger bi)
        {
            var msg = MarshalBigInt(bi);
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(ByteString.CopyFrom(msg));
        }
        else if (val is Decimal dec)
        {
            var msg = MarshalDecimal(dec);
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(ByteString.CopyFrom(msg));
        }
        else if (val is BigFloat bf)
        {
            var msg = MarshalBigFloat(bf);
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(ByteString.CopyFrom(msg));
        }
        else if (val.GetType().IsEnum)
        {
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.Varint));
            output.WriteEnum((int)val);
        }
        else if (val is System.Collections.IDictionary dict)
        {
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                using var ms = new MemoryStream();
                using var entryOutput = new CodedOutputStream(ms);
                if (entry.Key != null) MarshalField(entryOutput, 1, entry.Key, zigzag);
                if (entry.Value != null) MarshalField(entryOutput, 2, entry.Value, zigzag);
                entryOutput.Flush();
                output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.LengthDelimited));
                output.WriteBytes(ByteString.CopyFrom(ms.ToArray()));
            }
        }
        else if (val is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item != null) MarshalField(output, tag, item, zigzag);
            }
        }
        else
        {
            // Nested message
            var nestedInfo = GetStructInfo(val.GetType());
            using var ms = new MemoryStream();
            using var nestedOutput = new CodedOutputStream(ms);
            MarshalStruct(nestedOutput, val, nestedInfo);
            nestedOutput.Flush();
            output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(ByteString.CopyFrom(ms.ToArray()));
        }
    }

    private static bool IsZero(object? val)
    {
        if (val == null) return true;
        if (val is bool b) return !b;
        if (val is int i) return i == 0;
        if (val is long l) return l == 0;
        if (val is uint ui) return ui == 0;
        if (val is ulong ul) return ul == 0;
        if (val is float f) return f == 0;
        if (val is double d) return d == 0;
        if (val is string s) return string.IsNullOrEmpty(s);
        if (val is byte[] bytes) return bytes.Length == 0;
        if (val is ByteString bs) return bs.Length == 0;
        if (val is BigInteger bi) return bi.IsZero;
        if (val is Decimal dec) return dec.Unscaled.IsZero && dec.Scale == 0 && !dec.Negative;
        if (val is BigFloat bf) return bf.Mantissa.IsZero && bf.Exponent == 0 && bf.Precision == 0 && !bf.Negative;
        if (val.GetType().IsEnum) return (int)val == 0;
        return false;
    }

    private static void UnmarshalStruct(CodedInputStream input, object v, StructInfo info, int depth)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            var fieldNum = WireFormat.GetTagFieldNumber(tag);
            if (info.ByNumber.TryGetValue(fieldNum, out var field))
            {
                if (!string.IsNullOrEmpty(field.Oneof))
                {
                    foreach (var other in info.Fields.Where(f => f.Oneof == field.Oneof && f != field))
                    {
                        other.SetValue(v, null); // Clear other fields in the same oneof
                    }
                }
                var currentVal = field.GetValue(v);
                var newVal = UnmarshalField(input, tag, field.Type, currentVal, field.ZigZag, depth);
                field.SetValue(v, newVal);
            }
            else
            {
                input.SkipLastField();
            }
        }
    }

    private static object? UnmarshalField(CodedInputStream input, uint tag, Type type, object? currentVal, bool zigzag, int depth)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        if (underlyingType == typeof(bool)) return input.ReadBool();
        if (underlyingType == typeof(int)) return zigzag ? input.ReadSInt32() : input.ReadInt32();
        if (underlyingType == typeof(long)) return zigzag ? input.ReadSInt64() : input.ReadInt64();
        if (underlyingType == typeof(uint)) return input.ReadUInt32();
        if (underlyingType == typeof(ulong)) return input.ReadUInt64();
        if (underlyingType == typeof(float)) return input.ReadFloat();
        if (underlyingType == typeof(double)) return input.ReadDouble();
        if (underlyingType == typeof(string)) return input.ReadString();
        if (underlyingType == typeof(byte[])) return input.ReadBytes().ToByteArray();
        if (underlyingType == typeof(ByteString)) return input.ReadBytes();
        if (underlyingType == typeof(BigInteger)) return UnmarshalBigInt(input.ReadBytes().ToByteArray());
        if (underlyingType == typeof(Decimal)) return UnmarshalDecimal(input.ReadBytes().ToByteArray());
        if (underlyingType == typeof(BigFloat)) return UnmarshalBigFloat(input.ReadBytes().ToByteArray());
        if (underlyingType.IsEnum) return Enum.ToObject(underlyingType, input.ReadEnum());

        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
        {
            var keyType = type.GetGenericArguments()[0];
            var valType = type.GetGenericArguments()[1];
            var map = (System.Collections.IDictionary?)currentVal ?? (System.Collections.IDictionary)Activator.CreateInstance(type)!;

            var entryData = input.ReadBytes().ToByteArray();
            var entryInput = new CodedInputStream(entryData);
            object? key = null, val = null;
            uint etag;
            while ((etag = entryInput.ReadTag()) != 0)
            {
                var enumNum = WireFormat.GetTagFieldNumber(etag);
                if (enumNum == 1) key = UnmarshalField(entryInput, etag, keyType, null, zigzag, depth);
                else if (enumNum == 2) val = UnmarshalField(entryInput, etag, valType, null, zigzag, depth);
                else entryInput.SkipLastField();
            }
            if (key != null) map[key] = val;
            return map;
        }

        if (typeof(System.Collections.IList).IsAssignableFrom(type))
        {
            var itemType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            var item = UnmarshalField(input, tag, itemType, null, zigzag, depth);
            if (type.IsArray)
            {
                var array = (Array?)currentVal;
                var newArray = Array.CreateInstance(itemType, (array?.Length ?? 0) + 1);
                if (array != null) Array.Copy(array, newArray, array.Length);
                newArray.SetValue(item, newArray.Length - 1);
                return newArray;
            }
            var list = (System.Collections.IList?)currentVal ?? (System.Collections.IList)Activator.CreateInstance(type)!;
            list.Add(item);
            return list;
        }

        // Nested submessage. HARDENING.md § Recursion: the depth counter
        // must survive nested CodedInputStream construction. The library's
        // CodedInputStream.RecursionLimit is reset every time a fresh stream
        // is constructed (which we do per nesting level here), so we keep
        // an explicit `depth` parameter threaded through and reject before
        // a stack overflow becomes possible.
        if (depth >= MaxNestingDepth)
        {
            throw new InvalidOperationException(
                $"PB nesting depth exceeds {MaxNestingDepth}");
        }
        var nestedData = input.ReadBytes().ToByteArray();
        var nestedVal = Activator.CreateInstance(type)!;
        var nestedInput = new CodedInputStream(nestedData);
        UnmarshalStruct(nestedInput, nestedVal, GetStructInfo(type), depth + 1);
        return nestedVal;
    }

    private static BigInteger UnmarshalBigInt(byte[] data)
    {
        var input = new CodedInputStream(data);
        byte[]? absBytes = null;
        bool negative = false;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: absBytes = input.ReadBytes().ToByteArray(); break;
                case 2: negative = input.ReadBool(); break;
                default: input.SkipLastField(); break;
            }
        }
        if (absBytes == null) return BigInteger.Zero;
        var bi = new BigInteger(absBytes, isUnsigned: true, isBigEndian: true);
        return negative ? -bi : bi;
    }

    private static byte[] MarshalBigInt(BigInteger bi)
    {
        using var ms = new MemoryStream();
        using var output = new CodedOutputStream(ms);
        var abs = BigInteger.Abs(bi);
        var bytes = abs.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > 0)
        {
            output.WriteTag(WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(ByteString.CopyFrom(bytes));
        }
        if (bi.Sign < 0)
        {
            output.WriteTag(WireFormat.MakeTag(2, WireFormat.WireType.Varint));
            output.WriteBool(true);
        }
        output.Flush();
        return ms.ToArray();
    }

    private static byte[] MarshalDecimal(Decimal dec)
    {
        using var ms = new MemoryStream();
        using var output = new CodedOutputStream(ms);
        var bytes = dec.Unscaled.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > 0)
        {
            output.WriteTag(WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(ByteString.CopyFrom(bytes));
        }
        if (dec.Scale != 0)
        {
            output.WriteTag(WireFormat.MakeTag(2, WireFormat.WireType.Varint));
            output.WriteSInt32(dec.Scale);
        }
        if (dec.Negative)
        {
            output.WriteTag(WireFormat.MakeTag(3, WireFormat.WireType.Varint));
            output.WriteBool(true);
        }
        output.Flush();
        return ms.ToArray();
    }

    private static Decimal UnmarshalDecimal(byte[] data)
    {
        var input = new CodedInputStream(data);
        var dec = new Decimal();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: dec.Unscaled = new BigInteger(input.ReadBytes().ToByteArray(), isUnsigned: true, isBigEndian: true); break;
                case 2: dec.Scale = input.ReadSInt32(); break;
                case 3: dec.Negative = input.ReadBool(); break;
                default: input.SkipLastField(); break;
            }
        }
        return dec;
    }

    private static byte[] MarshalBigFloat(BigFloat bf)
    {
        using var ms = new MemoryStream();
        using var output = new CodedOutputStream(ms);
        var bytes = bf.Mantissa.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > 0)
        {
            output.WriteTag(WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(ByteString.CopyFrom(bytes));
        }
        if (bf.Exponent != 0)
        {
            output.WriteTag(WireFormat.MakeTag(2, WireFormat.WireType.Varint));
            output.WriteSInt32(bf.Exponent);
        }
        if (bf.Precision != 0)
        {
            output.WriteTag(WireFormat.MakeTag(3, WireFormat.WireType.Varint));
            output.WriteUInt32(bf.Precision);
        }
        if (bf.Negative)
        {
            output.WriteTag(WireFormat.MakeTag(4, WireFormat.WireType.Varint));
            output.WriteBool(true);
        }
        output.Flush();
        return ms.ToArray();
    }

    private static BigFloat UnmarshalBigFloat(byte[] data)
    {
        var input = new CodedInputStream(data);
        var bf = new BigFloat();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: bf.Mantissa = new BigInteger(input.ReadBytes().ToByteArray(), isUnsigned: true, isBigEndian: true); break;
                case 2: bf.Exponent = input.ReadSInt32(); break;
                case 3: bf.Precision = input.ReadUInt32(); break;
                case 4: bf.Negative = input.ReadBool(); break;
                default: input.SkipLastField(); break;
            }
        }
        return bf;
    }

    private class StructInfo
    {
        public List<FieldInfoData> Fields { get; }
        public Dictionary<int, FieldInfoData> ByNumber { get; }

        public StructInfo(List<FieldInfoData> fields)
        {
            Fields = fields;
            ByNumber = fields.ToDictionary(f => f.Tag, f => f);
        }
    }

    private class FieldInfoData
    {
        private readonly Func<object, object?> _getter;
        private readonly Action<object, object?> _setter;

        public Type Type { get; }
        public int Tag { get; }
        public bool ZigZag { get; }
        public string? Oneof { get; }

        public FieldInfoData(PropertyInfo prop, int tag, bool zigzag, string? oneof)
        {
            Type = prop.PropertyType; Tag = tag; ZigZag = zigzag; Oneof = oneof;
            _getter = CreateGetter(prop);
            _setter = CreateSetter(prop);
        }

        public FieldInfoData(FieldInfo field, int tag, bool zigzag, string? oneof)
        {
            Type = field.FieldType; Tag = tag; ZigZag = zigzag; Oneof = oneof;
            _getter = CreateGetter(field);
            _setter = CreateSetter(field);
        }

        public object? GetValue(object obj) => _getter(obj);
        public void SetValue(object obj, object? val) => _setter(obj, val);

        private static Func<object, object?> CreateGetter(PropertyInfo prop)
        {
            var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "obj");
            var cast = System.Linq.Expressions.Expression.Convert(param, prop.DeclaringType!);
            var body = System.Linq.Expressions.Expression.Property(cast, prop);
            var conv = System.Linq.Expressions.Expression.Convert(body, typeof(object));
            return System.Linq.Expressions.Expression.Lambda<Func<object, object?>>(conv, param).Compile();
        }

        private static Action<object, object?> CreateSetter(PropertyInfo prop)
        {
            var paramObj = System.Linq.Expressions.Expression.Parameter(typeof(object), "obj");
            var paramVal = System.Linq.Expressions.Expression.Parameter(typeof(object), "val");
            var castObj = System.Linq.Expressions.Expression.Convert(paramObj, prop.DeclaringType!);
            var castVal = System.Linq.Expressions.Expression.Convert(paramVal, prop.PropertyType);
            var body = System.Linq.Expressions.Expression.Assign(System.Linq.Expressions.Expression.Property(castObj, prop), castVal);
            return System.Linq.Expressions.Expression.Lambda<Action<object, object?>>(body, paramObj, paramVal).Compile();
        }

        private static Func<object, object?> CreateGetter(FieldInfo field)
        {
            var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "obj");
            var cast = System.Linq.Expressions.Expression.Convert(param, field.DeclaringType!);
            var body = System.Linq.Expressions.Expression.Field(cast, field);
            var conv = System.Linq.Expressions.Expression.Convert(body, typeof(object));
            return System.Linq.Expressions.Expression.Lambda<Func<object, object?>>(conv, param).Compile();
        }

        private static Action<object, object?> CreateSetter(FieldInfo field)
        {
            var paramObj = System.Linq.Expressions.Expression.Parameter(typeof(object), "obj");
            var paramVal = System.Linq.Expressions.Expression.Parameter(typeof(object), "val");
            var castObj = System.Linq.Expressions.Expression.Convert(paramObj, field.DeclaringType!);
            var castVal = System.Linq.Expressions.Expression.Convert(paramVal, field.FieldType);
            var body = System.Linq.Expressions.Expression.Assign(System.Linq.Expressions.Expression.Field(castObj, field), castVal);
            return System.Linq.Expressions.Expression.Lambda<Action<object, object?>>(body, paramObj, paramVal).Compile();
        }
    }
}
