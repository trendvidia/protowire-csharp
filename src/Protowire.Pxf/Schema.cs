// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Reflection;
using System.Runtime.CompilerServices;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

/// <summary>
/// One schema element that fails a PXF bind-time check. <paramref name="File"/>
/// is the .proto declaring the element, <paramref name="Element"/> its
/// fully-qualified name, <paramref name="Detail"/> what is wrong, and
/// <paramref name="Name"/> the annotation value when the rule has one (the
/// <c>(pxf.key)</c> value; null for the required-and-default rule).
/// </summary>
public sealed record Violation(string File, string Element, string Detail, string? Name = null)
{
    internal string Message { get; init; } = Detail;

    public override string ToString() => $"{File}: field \"{Element}\": {Message}";
}

/// <summary>
/// PXF directive-name reservations per draft §3.4.6.
///
/// <para>
/// The full reserved-directive-name set is 13 names: the four value
/// keywords (<c>null</c>, <c>true</c>, <c>false</c>) — rejected at the
/// lexer because they tokenise as their value form, never as a
/// directive — plus seven names with parser-layer or future
/// significance:
/// </para>
/// <list type="bullet">
///   <item><c>type</c>, <c>dataset</c>, <c>proto</c> — own production, lexed as dedicated tokens</item>
///   <item><c>entry</c> — spec-registered named directive (draft §3.4.3)</item>
///   <item><c>table</c>, <c>datasource</c>, <c>view</c>, <c>procedure</c>, <c>function</c>, <c>permissions</c>
///     — future-reserved; v1 decoders MUST reject them so applications
///     cannot squat the names before the spec allocates semantics</item>
/// </list>
/// </summary>
public static class Schema
{
    /// <summary>
    /// True when <paramref name="name"/> is reserved for future
    /// allocation by draft §3.4.6 and MUST be rejected by v1 decoders.
    /// Names with their own lexer production (<c>type</c>, <c>dataset</c>,
    /// <c>proto</c>) and the registered <c>entry</c> are not included
    /// here — they're handled either at the lexer or by the
    /// named-directive shape.
    /// </summary>
    public static bool IsFutureReservedDirective(string name) => name switch
    {
        "table" or "datasource" or "view" or "procedure" or "function" or "permissions" => true,
        _ => false,
    };

    // Decoders re-check on every bind, and the closure of any annotated
    // file reaches descriptor.proto, so the result is kept per file
    // (draft -01 "Scope of Bind-Time Checks", non-normative note).
    private static readonly ConditionalWeakTable<FileDescriptor, IReadOnlyList<Violation>> ClosureCache = new();

    /// <summary>
    /// The bind-time violations in the file declaring <paramref name="desc"/>
    /// and its transitive imports. Empty means conformant.
    /// </summary>
    public static IReadOnlyList<Violation> ValidateDescriptor(MessageDescriptor desc) => ValidateFile(desc.File);

    /// <summary>
    /// The bind-time violations in <paramref name="file"/> and its transitive
    /// imports, sorted by declaring file and then by element name. Each file
    /// is checked once, however many import paths reach it, and a violation
    /// names the file that declares it. Two rules so far: draft -01 "Required
    /// and Default" (<c>(pxf.required) = true</c> and <c>(pxf.default)</c> on
    /// one field) and <c>(pxf.key)</c> placement (draft -01 "Schema Placement").
    /// </summary>
    public static IReadOnlyList<Violation> ValidateFile(FileDescriptor file) =>
        ClosureCache.GetValue(file, static f =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var found = new List<Violation>();
            WalkFile(f, seen, found);
            return found
                .OrderBy(v => v.File, StringComparer.Ordinal)
                .ThenBy(v => v.Element, StringComparer.Ordinal)
                .ToArray();
        });

    /// <summary>Throws if <paramref name="desc"/>'s closure has a violation.</summary>
    internal static void EnsureBindable(MessageDescriptor desc)
    {
        var violations = ValidateDescriptor(desc);
        if (violations.Count > 0)
        {
            throw new PxfException(Position.Empty, string.Join("; ", violations));
        }
    }

    /// <summary>
    /// The attribute spelling of the same rule, for a hand-written type.
    /// Only the bound type's own properties are read, because those are the
    /// only ones <see cref="Decoder"/> honours the attributes on.
    /// </summary>
    internal static void EnsureBindable(Type type)
    {
        foreach (var prop in type.GetProperties())
        {
            if (prop.IsDefined(typeof(PxfRequiredAttribute)) && prop.IsDefined(typeof(PxfDefaultAttribute)))
            {
                throw new PxfException(Position.Empty,
                    $"{type.FullName}.{prop.Name}: [PxfRequired] and [PxfDefault] are exclusive on one field: " + RequiredDefaultReason);
            }
        }
    }

    private const string RequiredDefaultReason =
        "the first rejects a document where the field is absent, and the second treats it as supplied";

    private static void WalkFile(FileDescriptor file, HashSet<string> seen, List<Violation> found)
    {
        if (!seen.Add(file.Name)) return;
        WalkMessages(file.Name, file.MessageTypes, found);
        foreach (var dep in file.Dependencies)
        {
            WalkFile(dep, seen, found);
        }
    }

    private static void WalkMessages(string path, IEnumerable<MessageDescriptor> messages, List<Violation> found)
    {
        foreach (var msg in messages)
        {
            foreach (var fd in msg.Fields.InDeclarationOrder())
            {
                var keyName = AnnotationsRuntime.GetKey(fd);
                var keyProblem = keyName == null ? null : KeyPlacementProblem(fd, keyName);
                if (keyProblem != null)
                {
                    found.Add(new Violation(path, fd.FullName, keyProblem, keyName)
                    {
                        Message = $"invalid (pxf.key) = \"{keyName}\": {keyProblem} (draft -01 §3.13)",
                    });
                }
                if (AnnotationsRuntime.IsRequired(fd) && AnnotationsRuntime.GetDefault(fd) != null)
                {
                    var detail = "(pxf.required) and (pxf.default) are exclusive on one field: " + RequiredDefaultReason;
                    found.Add(new Violation(path, fd.FullName, detail)
                    {
                        Message = $"invalid (pxf.required): {detail} (draft -01 §annotation-extensions)",
                    });
                }
            }
            WalkMessages(path, msg.NestedTypes, found);
        }
    }

    private static string? KeyPlacementProblem(FieldDescriptor fd, string keyName)
    {
        if (!fd.IsRepeated || fd.IsMap || fd.FieldType != FieldType.Message)
        {
            return "(pxf.key) is valid only on repeated message-typed fields";
        }
        var keyFd = fd.MessageType.FindFieldByName(keyName);
        if (keyFd == null)
        {
            return $"element message {fd.MessageType.FullName} has no field \"{keyName}\"";
        }
        if (keyFd.IsRepeated || keyFd.IsMap || keyFd.FieldType != FieldType.String)
        {
            return $"key field {keyFd.FullName} must be a singular string field";
        }
        return null;
    }
}
