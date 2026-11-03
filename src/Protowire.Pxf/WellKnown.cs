// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

internal static class WellKnown
{
    public static bool IsTimestamp(MessageDescriptor desc) => desc.FullName == "google.protobuf.Timestamp";
    public static bool IsDuration(MessageDescriptor desc) => desc.FullName == "google.protobuf.Duration";
    public static bool IsBigInt(MessageDescriptor desc) => desc.FullName == "pxf.BigInt";
    public static bool IsDecimal(MessageDescriptor desc) => desc.FullName == "pxf.Decimal";
    public static bool IsBigFloat(MessageDescriptor desc) => desc.FullName == "pxf.BigFloat";
    public static bool IsAny(MessageDescriptor desc) => desc.FullName == "google.protobuf.Any";
    public static bool IsSecret(MessageDescriptor desc) => desc.FullName == "pxf.Secret";
    public static bool IsWrapper(MessageDescriptor desc) => WrapperTypes.ContainsKey(desc.FullName);

    public static readonly Dictionary<string, FieldType> WrapperTypes = new()
    {
        { "google.protobuf.DoubleValue", FieldType.Double },
        { "google.protobuf.FloatValue", FieldType.Float },
        { "google.protobuf.Int64Value", FieldType.Int64 },
        { "google.protobuf.UInt64Value", FieldType.UInt64 },
        { "google.protobuf.Int32Value", FieldType.Int32 },
        { "google.protobuf.UInt32Value", FieldType.UInt32 },
        { "google.protobuf.BoolValue", FieldType.Bool },
        { "google.protobuf.StringValue", FieldType.String },
        { "google.protobuf.BytesValue", FieldType.Bytes },
    };

    /// <summary>The scalar a generated class stores for a wrapper message.</summary>
    public static object Unwrap(IMessage wrapper) =>
        wrapper.Descriptor.FindFieldByName("value").Accessor.GetValue(wrapper);

    /// <summary>A Timestamp or Duration with the given fields.</summary>
    public static IMessage NewSecondsNanos(MessageDescriptor desc, long seconds, int nanos)
    {
        var msg = desc.Parser.ParseFrom(ByteString.Empty);
        desc.FindFieldByName("seconds").Accessor.SetValue(msg, seconds);
        desc.FindFieldByName("nanos").Accessor.SetValue(msg, nanos);
        return msg;
    }

    /// <summary>Deterministic bytes, so one document always packs the same.</summary>
    public static byte[] Deterministic(IMessage msg)
    {
        using var ms = new MemoryStream();
        using (var output = new CodedOutputStream(ms, leaveOpen: true) { Deterministic = true })
        {
            msg.WriteTo(output);
        }
        return ms.ToArray();
    }

    public static void SetTimestamp(object obj, DateTime dt)
    {
        var type = obj.GetType();
        var seconds = (long)(dt.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;
        var nanos = dt.ToUniversalTime().Ticks % TimeSpan.TicksPerSecond * 100;
        type.GetProperty("Seconds")?.SetValue(obj, seconds);
        type.GetProperty("Nanos")?.SetValue(obj, (int)nanos);
    }

    public static void SetDuration(object obj, TimeSpan ts)
    {
        var type = obj.GetType();
        var seconds = (long)ts.TotalSeconds;
        var nanos = (int)(ts.Ticks % TimeSpan.TicksPerSecond * 100);
        type.GetProperty("Seconds")?.SetValue(obj, seconds);
        type.GetProperty("Nanos")?.SetValue(obj, nanos);
    }

    public static void SetBigInt(object obj, System.Numerics.BigInteger bi)
    {
        var type = obj.GetType();
        var abs = System.Numerics.BigInteger.Abs(bi);
        var bytes = Magnitude(abs);
        type.GetProperty("Abs")?.SetValue(obj, Google.Protobuf.ByteString.CopyFrom(bytes));
        type.GetProperty("Negative")?.SetValue(obj, bi.Sign < 0);
    }

    public static void SetDecimal(object obj, Protowire.Pb.Decimal dec)
    {
        var type = obj.GetType();
        var bytes = Magnitude(dec.Unscaled);
        type.GetProperty("Unscaled")?.SetValue(obj, Google.Protobuf.ByteString.CopyFrom(bytes));
        type.GetProperty("Scale")?.SetValue(obj, dec.Scale);
        type.GetProperty("Negative")?.SetValue(obj, dec.Negative);
    }

    public static void SetBigFloat(object obj, Protowire.Pb.BigFloat bf)
    {
        var type = obj.GetType();
        var bytes = Magnitude(bf.Mantissa);
        type.GetProperty("Mantissa")?.SetValue(obj, Google.Protobuf.ByteString.CopyFrom(bytes));
        type.GetProperty("Exponent")?.SetValue(obj, bf.Exponent);
        type.GetProperty("Prec")?.SetValue(obj, bf.Precision);
        type.GetProperty("Negative")?.SetValue(obj, bf.Negative);
    }

    // On the wire a zero magnitude is empty, not one 0 byte.
    private static byte[] Magnitude(System.Numerics.BigInteger magnitude) =>
        magnitude.IsZero ? [] : magnitude.ToByteArray(isUnsigned: true, isBigEndian: true);
}
