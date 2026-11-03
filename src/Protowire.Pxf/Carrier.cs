// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Globalization;
using System.Numerics;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

/// <summary>What the 1327 carrier says about one field.</summary>
internal sealed record CarrierOptions(bool Required, string? Default, string? DefaultProblem);

/// <summary>
/// The annotation carrier (RFC-001 §8.1). <c>@required</c> and <c>@default(v)</c>
/// lower to one <c>AnnotationList</c> at option number 1327, not to the bracket
/// options 1314 and 1315, and a compiler never writes both, so a reader of the
/// bracket numbers alone binds such a schema with no enforcement.
///
/// <para>
/// It reads the option bytes rather than a generated binding, so it needs no
/// extension registered: an unregistered extension arrives as unknown bytes,
/// and <see cref="FieldOptions"/> writes those back. Mirrors
/// <c>protowire-go/encoding/pxf/carrier.go</c>.
/// </para>
/// </summary>
internal static class Carrier
{
    private const int ExtAnnotations = 1327;
    private const string AnnotationRequired = "protowire.schema.v1.required";
    private const string AnnotationDefault = "protowire.schema.v1.default";

    private readonly record struct WireField(int Number, WireFormat.WireType Type, ulong Varint, ByteString? Bytes);

    /// <summary>The carrier's payload in <paramref name="opts"/>, or null for none.</summary>
    public static byte[]? Payload(FieldOptions? opts)
    {
        if (opts == null) return null;
        using var ms = new MemoryStream();
        foreach (var f in Fields(opts.ToByteArray()))
        {
            // Occurrences merge, and AnnotationList holds one repeated field,
            // so concatenating them is that merge.
            if (f.Number == ExtAnnotations && f.Bytes != null) f.Bytes.WriteTo(ms);
        }
        return ms.Length == 0 ? null : ms.ToArray();
    }

    /// <summary>
    /// Unparseable bytes stop the walk and keep what was read before them, so
    /// a truncated entry cannot turn a schema unannotated.
    /// </summary>
    public static CarrierOptions Parse(byte[] carrier, FieldDescriptor fd)
    {
        bool required = false;
        string? def = null, problem = null;
        foreach (var entry in Fields(carrier))
        {
            if (entry.Number != 1 || entry.Bytes == null) continue;
            string? name = null;
            var args = new List<ByteString>();
            foreach (var f in Fields(entry.Bytes.ToByteArray()))
            {
                if (f.Number == 1 && f.Bytes != null) name = f.Bytes.ToStringUtf8();
                else if (f.Number == 2 && f.Bytes != null) args.Add(f.Bytes);
            }
            if (name == AnnotationRequired)
            {
                required = true;
            }
            else if (name == AnnotationDefault && def == null && problem == null)
            {
                var arg = args.Find(a => ArgName(a) is "" or "value");
                if (arg == null)
                {
                    problem = "@default carries no value argument";
                    continue;
                }
                (def, problem) = ArgLiteral(arg.ToByteArray(), fd);
            }
        }
        return new CarrierOptions(required, def, problem);
    }

    private static List<WireField> Fields(byte[] data)
    {
        var fields = new List<WireField>();
        var input = new CodedInputStream(data);
        try
        {
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                int number = WireFormat.GetTagFieldNumber(tag);
                var type = WireFormat.GetTagWireType(tag);
                switch (type)
                {
                    case WireFormat.WireType.Varint: fields.Add(new(number, type, input.ReadUInt64(), null)); break;
                    case WireFormat.WireType.Fixed64: fields.Add(new(number, type, input.ReadFixed64(), null)); break;
                    case WireFormat.WireType.Fixed32: input.ReadFixed32(); break;
                    case WireFormat.WireType.LengthDelimited: fields.Add(new(number, type, 0, input.ReadBytes())); break;
                    default: return fields;
                }
            }
        }
        catch (InvalidProtocolBufferException)
        {
        }
        return fields;
    }

    private static string ArgName(ByteString arg)
    {
        foreach (var f in Fields(arg.ToByteArray()))
        {
            if (f.Number == 1 && f.Bytes != null) return f.Bytes.ToStringUtf8();
        }
        return "";
    }

    private static (string? Literal, string? Problem) ArgLiteral(byte[] arg, FieldDescriptor fd)
    {
        foreach (var f in Fields(arg))
        {
            switch (f.Number)
            {
                case 10 when f.Bytes != null:
                    return (f.Bytes.ToStringUtf8(), null);
                case 11 when f.Type == WireFormat.WireType.Varint:
                    return (Unsigned(fd) ? f.Varint.ToString(CultureInfo.InvariantCulture)
                        : ((long)f.Varint).ToString(CultureInfo.InvariantCulture), null);
                case 12 when f.Type == WireFormat.WireType.Fixed64:
                    return (GoFormat.Float(BitConverter.Int64BitsToDouble((long)f.Varint), single: false), null);
                case 13 when f.Type == WireFormat.WireType.Varint:
                    return (f.Varint != 0 ? "true" : "false", null);
                case 14 when f.Bytes != null:
                    return (f.Bytes.ToBase64(), null);
                case 16 when f.Bytes != null:
                    return BigIntLiteral(f.Bytes);
                case 17 when f.Bytes != null:
                    return DecimalLiteral(f.Bytes);
                case 18 when f.Bytes != null:
                    return BigFloatLiteral(f.Bytes);
                case 15 when f.Bytes != null:
                    return EnumLiteral(f.Bytes);
            }
        }
        return (null, "@default's value is empty");
    }

    private static bool Unsigned(FieldDescriptor fd) =>
        fd.FieldType is FieldType.UInt32 or FieldType.Fixed32 or FieldType.UInt64 or FieldType.Fixed64
        || (fd.FieldType == FieldType.Message && fd.MessageType.FullName is "google.protobuf.UInt32Value" or "google.protobuf.UInt64Value");

    private static (string?, string?) EnumLiteral(ByteString literal)
    {
        foreach (var f in Fields(literal.ToByteArray()))
        {
            if (f.Number != 1 || f.Bytes == null) continue;
            foreach (var e in Fields(f.Bytes.ToByteArray()))
            {
                if (e.Number == 2 && e.Bytes != null && e.Bytes.Length > 0) return (e.Bytes.ToStringUtf8(), null);
            }
            return (null, "@default names an enum value the lowering pass left unresolved");
        }
        return (null, "@default carries a message or list literal, and a default is exactly one literal");
    }

    private static BigInteger Magnitude(ByteString? b) =>
        b == null ? BigInteger.Zero : new BigInteger(b.Span, isUnsigned: true, isBigEndian: true);

    private static (string?, string?) BigIntLiteral(ByteString b)
    {
        BigInteger abs = BigInteger.Zero;
        bool negative = false;
        foreach (var f in Fields(b.ToByteArray()))
        {
            if (f.Number == 1) abs = Magnitude(f.Bytes);
            else if (f.Number == 2) negative = f.Varint != 0;
        }
        return ((negative ? -abs : abs).ToString(CultureInfo.InvariantCulture), null);
    }

    private static (string?, string?) DecimalLiteral(ByteString b)
    {
        BigInteger unscaled = BigInteger.Zero;
        int scale = 0;
        bool negative = false;
        foreach (var f in Fields(b.ToByteArray()))
        {
            if (f.Number == 1) unscaled = Magnitude(f.Bytes);
            else if (f.Number == 2) scale = (int)f.Varint; // a plain varint, as bignum.proto declares
            else if (f.Number == 3) negative = f.Varint != 0;
        }
        int limit = PxfLimits.Default.MaxNumericLiteralDigits;
        if (scale > limit || scale < -limit)
        {
            return (null, $"@default's decimal_value has scale {scale}, and MaxNumericLiteralDigits={limit} bounds its magnitude");
        }
        return (WellKnown.DecimalLiteral(unscaled, scale, negative, int.MaxValue), null);
    }

    private static (string?, string?) BigFloatLiteral(ByteString b)
    {
        BigInteger mantissa = BigInteger.Zero;
        int exponent = 0;
        uint prec = 0;
        bool negative = false;
        foreach (var f in Fields(b.ToByteArray()))
        {
            if (f.Number == 1) mantissa = Magnitude(f.Bytes);
            else if (f.Number == 2) exponent = (int)f.Varint;
            else if (f.Number == 3) prec = (uint)f.Varint;
            else if (f.Number == 4) negative = f.Varint != 0;
        }
        var limits = PxfLimits.Default;
        try
        {
            return (BigFloatText.Format(new Protowire.Pb.BigFloat(mantissa, exponent, prec, negative),
                limits.MaxNumericLiteralDigits, limits.MaxBigFloatExponent), null);
        }
        catch (FormatException e)
        {
            return (null, $"@default's big_float_value: {e.Message}");
        }
    }
}
