// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Collections;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Protowire.Pxf;

/// <summary>
/// Runtime readers for the proto-level <c>(pxf.required)</c> /
/// <c>(pxf.default)</c> annotations and helpers for the <c>_null</c>
/// <see cref="FieldMask"/> convention. These work against
/// <see cref="FieldDescriptor"/> (descriptor-driven), complementing the
/// reflection-attribute-based <see cref="PxfRequiredAttribute"/> /
/// <see cref="PxfDefaultAttribute"/> path used for hand-written POCO types.
///
/// <para>
/// Mirrors <c>protowire-go/encoding/pxf/annotations.go</c> and
/// <c>protowire-go/encoding/pxf/wellknown.go</c>.
/// </para>
/// </summary>
internal static class AnnotationsRuntime
{
    /// <summary>True if the field carries <c>(pxf.required) = true</c>.</summary>
    public static bool IsRequired(FieldDescriptor fd)
    {
        var opts = fd.GetOptions();
        return opts != null && opts.GetExtension(AnnotationsExtensions.Required);
    }

    /// <summary>
    /// The field's <c>(pxf.default)</c> string, or null if the option is
    /// absent.
    /// </summary>
    public static string? GetDefault(FieldDescriptor fd)
    {
        var opts = fd.GetOptions();
        if (opts == null) return null;
        if (!opts.HasExtension(AnnotationsExtensions.Default)) return null;
        return opts.GetExtension(AnnotationsExtensions.Default);
    }

    /// <summary>
    /// Returns the <c>_null</c> field if the message carries one of type
    /// <see cref="FieldMask"/>. Both the name and the type must match;
    /// regular FieldMask fields (e.g. <c>update_mask</c>) aren't recognized.
    /// </summary>
    public static FieldDescriptor? FindNullMaskField(MessageDescriptor desc)
    {
        var fd = desc.FindFieldByName("_null");
        if (fd == null) return null;
        if (fd.FieldType != FieldType.Message) return null;
        if (fd.MessageType.FullName != "google.protobuf.FieldMask") return null;
        return fd;
    }

    /// <summary>Appends a dotted path to the root message's <c>_null</c> FieldMask.</summary>
    public static void AppendNullPath(IMessage root, FieldDescriptor nullMaskFd, string path)
    {
        // Singular message fields default to null in protobuf C# — allocate
        // the FieldMask on demand and assign it back so subsequent appends
        // see the same instance.
        if (nullMaskFd.Accessor.GetValue(root) is not IMessage mask)
        {
            mask = new FieldMask();
            nullMaskFd.Accessor.SetValue(root, mask);
        }
        var pathsFd = mask.Descriptor.FindFieldByName("paths");
        if (pathsFd == null) return;
        ((IList)pathsFd.Accessor.GetValue(mask)).Add(path);
    }
}
