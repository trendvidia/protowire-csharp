// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Collections;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace Protowire.Pxf;

/// <summary>
/// Runtime readers for the proto-level <c>(pxf.required)</c> /
/// <c>(pxf.default)</c> / <c>(pxf.key)</c> annotations and helpers for the <c>_null</c>
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
    /// <summary>
    /// Every PXF annotation on one field, from both spellings: the bracket
    /// options and the 1327 carrier. Neither is synthesized from the other.
    /// </summary>
    internal sealed record PxfOptions(
        bool Required, string? Default, bool RequiredInCarrier, bool DefaultInCarrier, string? DefaultProblem);

    // Per field, because a decode reads these on every body.
    private static readonly ConditionalWeakTable<FieldDescriptor, PxfOptions> Cache = new();

    public static PxfOptions Options(FieldDescriptor fd) => Cache.GetValue(fd, Read);

    private static PxfOptions Read(FieldDescriptor fd)
    {
        var opts = fd.GetOptions();
        bool required = opts != null && opts.GetExtension(AnnotationsExtensions.Required);
        string? def = opts != null && opts.HasExtension(AnnotationsExtensions.Default)
            ? opts.GetExtension(AnnotationsExtensions.Default)
            : null;
        var payload = Carrier.Payload(opts);
        if (payload == null) return new PxfOptions(required, def, false, false, null);

        // required is one boolean: either spelling asserting it is enough. A
        // default has a value, so two spellings can disagree, and picking one
        // would give meaning to which an author migrated first. RFC-001 §8.5
        // forbids reconciling them, so the schema is refused instead.
        var c = Carrier.Parse(payload, fd);
        string? problem = c.DefaultProblem;
        bool fromCarrier = problem != null;
        if (problem == null && c.Default != null)
        {
            if (def == null)
            {
                def = c.Default;
                fromCarrier = true;
            }
            else if (def != c.Default)
            {
                problem = $"(pxf.default) = \"{def}\" and @default = \"{c.Default}\" carry different values, and the two surfaces must not be reconciled (RFC-001 §8.5)";
            }
        }
        return new PxfOptions(required || c.Required, def, c.Required && !required, fromCarrier, problem);
    }

    /// <summary>True if the field is required, in either spelling.</summary>
    public static bool IsRequired(FieldDescriptor fd) => Options(fd).Required;

    /// <summary>The field's default literal in either spelling, or null for none.</summary>
    public static string? GetDefault(FieldDescriptor fd) => Options(fd).Default;

    /// <summary>
    /// The field's <c>(pxf.key)</c> string, or null if the option is absent.
    /// </summary>
    public static string? GetKey(FieldDescriptor fd)
    {
        var opts = fd.GetOptions();
        if (opts == null) return null;
        if (!opts.HasExtension(AnnotationsExtensions.Key)) return null;
        return opts.GetExtension(AnnotationsExtensions.Key);
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
