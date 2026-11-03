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
    // HARDENING.md § UTF-8: a proto3 string is valid UTF-8, and the default
    // decoder would substitute U+FFFD for what is not.
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static readonly ConcurrentDictionary<Type, StructInfo> StructCache = new();

    /// <summary>
    /// Marshals the specified object into a Protobuf binary-encoded byte array.
    /// </summary>
    /// <param name="v">The object to marshal.</param>
    /// <returns>A byte array containing the Protobuf binary-encoded data.</returns>
    public static byte[] Marshal(object v) => Marshal(v, PbLimits.Default);

    /// <summary>
    /// Marshals under the given limits. Only <see cref="PbLimits.MaxNestingDepth"/>
    /// applies to writing: a value nested more deeply, counted as a decoder
    /// counts it (the root is depth 0, and every submessage or map entry is one
    /// descent), is an error rather than bytes a reader at that limit refuses
    /// (HARDENING.md § Recursion). Raise it only together with every reader's.
    /// </summary>
    /// <param name="v">The object to marshal.</param>
    /// <param name="limits">The limits of this call.</param>
    /// <returns>A byte array containing the Protobuf binary-encoded data.</returns>
    public static byte[] Marshal(object v, PbLimits limits)
    {
        var type = v.GetType();
        var info = GetStructInfo(type);
        using var stream = new MemoryStream();
        using var output = new CodedOutputStream(stream);
        MarshalStruct(output, v, info, 0, limits.MaxNestingDepth);
        output.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Unmarshals Protobuf binary-encoded data into the specified object.
    /// </summary>
    /// <param name="data">The byte array containing the Protobuf binary-encoded data.</param>
    /// <param name="v">The object to unmarshal into.</param>
    public static void Unmarshal(byte[] data, object v) => Unmarshal(data, v, PbLimits.Default);

    /// <summary>Unmarshals under the given limits.</summary>
    /// <param name="data">The byte array containing the Protobuf binary-encoded data.</param>
    /// <param name="v">The object to unmarshal into.</param>
    /// <param name="limits">The limits of this call.</param>
    public static void Unmarshal(byte[] data, object v, PbLimits limits)
    {
        if (data.Length > limits.MaxMessageSize)
        {
            throw new InvalidOperationException($"input of {data.Length} bytes exceeds MaxMessageSize={limits.MaxMessageSize}");
        }
        var type = v.GetType();
        var info = GetStructInfo(type);
        var input = new CodedInputStream(data);
        UnmarshalStruct(input, v, info, 0, limits);
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

    private static InvalidOperationException DepthError(int maxDepth) =>
        new($"nesting depth exceeds MaxNestingDepth={maxDepth}");

    private static void MarshalStruct(CodedOutputStream output, object v, StructInfo info, int depth, int maxDepth)
    {
        // A message a reader at the limit would refuse is an error here, not
        // bytes nobody can read.
        if (depth > maxDepth)
        {
            throw DepthError(maxDepth);
        }
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

            MarshalField(output, (uint)field.Tag, val, field.ZigZag, depth, maxDepth);
        }
    }

    private static void MarshalField(CodedOutputStream output, uint tag, object val, bool zigzag, int depth, int maxDepth)
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
            // An entry is a descent whatever its value's type, as the decoder
            // counts it, so a map of scalars at the limit is refused too.
            if (dict.Count > 0 && depth + 1 > maxDepth)
            {
                throw DepthError(maxDepth);
            }
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                using var ms = new MemoryStream();
                using var entryOutput = new CodedOutputStream(ms);
                if (entry.Key != null) MarshalField(entryOutput, 1, entry.Key, zigzag, depth + 1, maxDepth);
                if (entry.Value != null) MarshalField(entryOutput, 2, entry.Value, zigzag, depth + 1, maxDepth);
                entryOutput.Flush();
                output.WriteTag(WireFormat.MakeTag((int)tag, WireFormat.WireType.LengthDelimited));
                output.WriteBytes(ByteString.CopyFrom(ms.ToArray()));
            }
        }
        else if (val is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item != null) MarshalField(output, tag, item, zigzag, depth, maxDepth);
            }
        }
        else
        {
            // Nested message
            var nestedInfo = GetStructInfo(val.GetType());
            using var ms = new MemoryStream();
            using var nestedOutput = new CodedOutputStream(ms);
            MarshalStruct(nestedOutput, val, nestedInfo, depth + 1, maxDepth);
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

    private static void UnmarshalStruct(CodedInputStream input, object v, StructInfo info, int depth, PbLimits limits)
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
                var newVal = UnmarshalField(input, tag, field.Type, currentVal, field.ZigZag, depth, limits);
                field.SetValue(v, newVal);
            }
            else
            {
                input.SkipLastField();
            }
        }
    }

    private static object? UnmarshalField(CodedInputStream input, uint tag, Type type, object? currentVal, bool zigzag, int depth, PbLimits limits)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
        if (underlyingType == typeof(bool)) return input.ReadBool();
        if (underlyingType == typeof(int)) return zigzag ? input.ReadSInt32() : input.ReadInt32();
        if (underlyingType == typeof(long)) return zigzag ? input.ReadSInt64() : input.ReadInt64();
        if (underlyingType == typeof(uint)) return input.ReadUInt32();
        if (underlyingType == typeof(ulong)) return input.ReadUInt64();
        if (underlyingType == typeof(float)) return input.ReadFloat();
        if (underlyingType == typeof(double)) return input.ReadDouble();
        if (underlyingType == typeof(string)) return ReadStrictString(input);
        if (underlyingType == typeof(byte[])) return input.ReadBytes().ToByteArray();
        if (underlyingType == typeof(ByteString)) return input.ReadBytes();
        if (underlyingType == typeof(BigInteger)) return UnmarshalBigInt(input.ReadBytes().ToByteArray());
        if (underlyingType == typeof(Decimal)) return UnmarshalDecimal(input.ReadBytes().ToByteArray(), limits);
        if (underlyingType == typeof(BigFloat)) return UnmarshalBigFloat(input.ReadBytes().ToByteArray(), limits);
        if (underlyingType.IsEnum) return Enum.ToObject(underlyingType, input.ReadEnum());

        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
        {
            var keyType = type.GetGenericArguments()[0];
            var valType = type.GetGenericArguments()[1];
            var map = (System.Collections.IDictionary?)currentVal ?? (System.Collections.IDictionary)Activator.CreateInstance(type)!;

            // The entry is a descent whatever its value's type, so this
            // cannot wait for a message value to reach the check below.
            if (depth >= limits.MaxNestingDepth)
            {
                throw DepthError(limits.MaxNestingDepth);
            }
            var entryData = input.ReadBytes().ToByteArray();
            var entryInput = new CodedInputStream(entryData);
            object? key = null, val = null;
            uint etag;
            while ((etag = entryInput.ReadTag()) != 0)
            {
                var enumNum = WireFormat.GetTagFieldNumber(etag);
                // HARDENING.md § Recursion: a map entry is one descent, so a
                // message value sits two below the map field's parent.
                if (enumNum == 1) key = UnmarshalField(entryInput, etag, keyType, null, zigzag, depth + 1, limits);
                else if (enumNum == 2) val = UnmarshalField(entryInput, etag, valType, null, zigzag, depth + 1, limits);
                else entryInput.SkipLastField();
            }
            // Protobuf: a field missing from a map entry is its type's
            // default, so an entry is never dropped and a value never null.
            key ??= DefaultOf(keyType);
            if (map.Count >= limits.MaxRepeatedCount && !map.Contains(key))
            {
                throw new InvalidOperationException($"map field exceeds MaxRepeatedCount={limits.MaxRepeatedCount}");
            }
            map[key] = val ?? DefaultOf(valType);
            return map;
        }

        if (typeof(System.Collections.IList).IsAssignableFrom(type))
        {
            var itemType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            // A packed field is one length-delimited run of scalars, and it
            // is what proto3 writes by default, so a decoder must take it.
            if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited && IsPackable(itemType))
            {
                var run = new CodedInputStream(input.ReadBytes().ToByteArray());
                var packedTag = WireFormat.MakeTag(WireFormat.GetTagFieldNumber(tag), WireFormat.WireType.Varint);
                object? packed = currentVal;
                while (!run.IsAtEnd)
                {
                    packed = AppendItem(type, itemType, packed, UnmarshalField(run, packedTag, itemType, null, zigzag, depth, limits), limits);
                }
                return packed ?? Activator.CreateInstance(type);
            }
            return AppendItem(type, itemType, currentVal, UnmarshalField(input, tag, itemType, null, zigzag, depth, limits), limits);
        }

        // Nested submessage. HARDENING.md § Recursion: the depth counter
        // must survive nested CodedInputStream construction. The library's
        // CodedInputStream.RecursionLimit is reset every time a fresh stream
        // is constructed (which we do per nesting level here), so we keep
        // an explicit `depth` parameter threaded through and reject before
        // a stack overflow becomes possible.
        if (depth >= limits.MaxNestingDepth)
        {
            throw DepthError(limits.MaxNestingDepth);
        }
        var nestedData = input.ReadBytes().ToByteArray();
        var nestedVal = Activator.CreateInstance(type)!;
        var nestedInput = new CodedInputStream(nestedData);
        UnmarshalStruct(nestedInput, nestedVal, GetStructInfo(type), depth + 1, limits);
        return nestedVal;
    }

    // What a field of this type holds when the wire omits it. A message is an
    // empty instance rather than null: a map entry's value is always present.
    private static object DefaultOf(Type type)
    {
        if (type == typeof(string)) return "";
        if (type == typeof(byte[])) return Array.Empty<byte>();
        if (type == typeof(ByteString)) return ByteString.Empty;
        return Activator.CreateInstance(type)!;
    }

    private static bool IsPackable(Type itemType)
    {
        var t = Nullable.GetUnderlyingType(itemType) ?? itemType;
        return t.IsPrimitive || t.IsEnum;
    }

    private static object AppendItem(Type type, Type itemType, object? current, object? item, PbLimits limits)
    {
        int count = (current as System.Collections.ICollection)?.Count ?? 0;
        if (count >= limits.MaxRepeatedCount)
        {
            throw new InvalidOperationException($"repeated field exceeds MaxRepeatedCount={limits.MaxRepeatedCount}");
        }
        if (type.IsArray)
        {
            var array = (Array?)current;
            var newArray = Array.CreateInstance(itemType, (array?.Length ?? 0) + 1);
            if (array != null) Array.Copy(array, newArray, array.Length);
            newArray.SetValue(item, newArray.Length - 1);
            return newArray;
        }
        var list = (System.Collections.IList?)current ?? (System.Collections.IList)Activator.CreateInstance(type)!;
        list.Add(item);
        return list;
    }

    private static string ReadStrictString(CodedInputStream input)
    {
        var bytes = input.ReadBytes();
        try
        {
            return StrictUtf8.GetString(bytes.Span);
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new InvalidOperationException("invalid UTF-8 in string field");
        }
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
        var bytes = MagnitudeBytes(abs);
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

    // BigInteger.Zero's ToByteArray is one 0 byte; on the wire a zero
    // magnitude is empty.
    private static byte[] MagnitudeBytes(BigInteger magnitude) =>
        magnitude.IsZero ? [] : magnitude.ToByteArray(isUnsigned: true, isBigEndian: true);

    private static byte[] MarshalDecimal(Decimal dec)
    {
        int limit = PbLimits.Default.MaxNumericLiteralDigits;
        if (dec.Scale > limit || dec.Scale < -limit)
        {
            throw new InvalidOperationException($"pxf.Decimal scale {dec.Scale} exceeds MaxNumericLiteralDigits={limit}");
        }
        using var ms = new MemoryStream();
        using var output = new CodedOutputStream(ms);
        var bytes = MagnitudeBytes(dec.Unscaled);
        if (bytes.Length > 0)
        {
            output.WriteTag(WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(ByteString.CopyFrom(bytes));
        }
        if (dec.Scale != 0)
        {
            output.WriteTag(WireFormat.MakeTag(2, WireFormat.WireType.Varint));
            output.WriteInt32(dec.Scale);
        }
        if (dec.Negative)
        {
            output.WriteTag(WireFormat.MakeTag(3, WireFormat.WireType.Varint));
            output.WriteBool(true);
        }
        output.Flush();
        return ms.ToArray();
    }

    private static Decimal UnmarshalDecimal(byte[] data, PbLimits limits)
    {
        var input = new CodedInputStream(data);
        var dec = new Decimal();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: dec.Unscaled = new BigInteger(input.ReadBytes().ToByteArray(), isUnsigned: true, isBigEndian: true); break;
                case 2: dec.Scale = input.ReadInt32(); break;
                case 3: dec.Negative = input.ReadBool(); break;
                default: input.SkipLastField(); break;
            }
        }
        // Rendering costs work proportional to the scale, four bytes whoever
        // wrote them chose, so it is bounded on both signs.
        if (dec.Scale > limits.MaxNumericLiteralDigits || dec.Scale < -limits.MaxNumericLiteralDigits)
        {
            throw new InvalidOperationException($"pxf.Decimal scale {dec.Scale} exceeds MaxNumericLiteralDigits={limits.MaxNumericLiteralDigits}");
        }
        return dec;
    }

    private static byte[] MarshalBigFloat(BigFloat bf)
    {
        using var ms = new MemoryStream();
        using var output = new CodedOutputStream(ms);
        var bytes = MagnitudeBytes(bf.Mantissa);
        if (bytes.Length > 0)
        {
            output.WriteTag(WireFormat.MakeTag(1, WireFormat.WireType.LengthDelimited));
            output.WriteBytes(ByteString.CopyFrom(bytes));
        }
        // A zero has one encoding: an empty mantissa and exponent 0.
        if (bf.Exponent != 0 && !bf.Mantissa.IsZero)
        {
            output.WriteTag(WireFormat.MakeTag(2, WireFormat.WireType.Varint));
            output.WriteInt32(bf.Exponent);
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

    private static BigFloat UnmarshalBigFloat(byte[] data, PbLimits limits)
    {
        var input = new CodedInputStream(data);
        var bf = new BigFloat();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: bf.Mantissa = new BigInteger(input.ReadBytes().ToByteArray(), isUnsigned: true, isBigEndian: true); break;
                case 2: bf.Exponent = input.ReadInt32(); break;
                case 3: bf.Precision = input.ReadUInt32(); break;
                case 4: bf.Negative = input.ReadBool(); break;
                default: input.SkipLastField(); break;
            }
        }
        // The value's binary exponent, not the wire field, which shifts with
        // the precision a producer chose. A zero has none.
        if (!bf.Mantissa.IsZero)
        {
            long binary = bf.Exponent + (long)bf.Mantissa.GetBitLength();
            if (Math.Abs(binary) > limits.MaxBigFloatExponent)
            {
                throw new InvalidOperationException($"pxf.BigFloat binary exponent {binary} exceeds MaxBigFloatExponent={limits.MaxBigFloatExponent}");
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
