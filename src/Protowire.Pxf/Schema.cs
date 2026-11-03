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
/// <c>(pxf.key)</c> value; null for the required-and-default rule). For an
/// option at a retired extension number (STABILITY.md promise 3) the element is
/// any annotated one, the file itself included, and the name is the number.
/// </summary>
public sealed record Violation(string File, string Element, string Detail, string? Name = null)
{
    internal string Message { get; init; } = Detail;

    // The whole line after "<file>: ", for a kind that protowire-go does not
    // print as an annotation on a field.
    internal string? Text { get; init; }

    public override string ToString() => Text != null ? $"{File}: {Text}" : $"{File}: field \"{Element}\": {Message}";
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
    /// names the file that declares it. Checks the reserved names, the shape of
    /// a well-known type, and the placement of <c>(pxf.key)</c>,
    /// <c>(pxf.required)</c> and <c>(pxf.default)</c>, in either spelling; and,
    /// in a file importing protowire's annotations, an option at an extension
    /// number retired in v1.12.0 (STABILITY.md promise 3), which means the
    /// descriptor must be recompiled.
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
        WalkEnums(file.Name, file.EnumTypes, found);
        Retired.Walk(file, found);
        foreach (var dep in file.Dependencies)
        {
            WalkFile(dep, seen, found);
        }
    }

    private static bool IsReserved(string name) => name is "null" or "true" or "false";

    // Printed as protowire-go prints it: "message field", "oneof" or "enum
    // value", then the element (#88).
    private static Violation Reserved(string path, string kind, string element, string name) =>
        new(path, element, "reserved name", name)
        {
            Text = $"{kind} \"{element}\" uses PXF-reserved name \"{name}\" (draft §3.13)",
        };

    private static void WalkMessages(string path, IEnumerable<MessageDescriptor> messages, List<Violation> found)
    {
        foreach (var msg in messages)
        {
            if (WellKnown.ShapeProblem(msg) is { } shape)
            {
                found.Add(new Violation(path, msg.FullName, shape)
                {
                    Text = $"message \"{msg.FullName}\" redefines a well-known type: {shape} (draft -01 §wkt-shapes)",
                });
            }
            var oneofDefaults = new List<(OneofDescriptor Oneof, FieldDescriptor Field, AnnotationsRuntime.PxfOptions Options)>();
            foreach (var fd in msg.Fields.InDeclarationOrder())
            {
                if (IsReserved(fd.Name)) found.Add(Reserved(path, "message field", fd.FullName, fd.Name));
                var keyName = AnnotationsRuntime.GetKey(fd);
                var keyProblem = keyName == null ? null : KeyPlacementProblem(fd, keyName);
                if (keyProblem != null)
                {
                    found.Add(new Violation(path, fd.FullName, keyProblem, keyName)
                    {
                        Message = $"invalid (pxf.key) = \"{keyName}\": {keyProblem} (draft -01 §3.13)",
                    });
                }

                var opts = AnnotationsRuntime.Options(fd);
                string defaultName = opts.DefaultInCarrier ? "@default" : "(pxf.default)";
                string requiredName = opts.RequiredInCarrier ? "@required" : "(pxf.required)";
                if (opts.DefaultProblem != null)
                {
                    found.Add(new Violation(path, fd.FullName, opts.DefaultProblem)
                    {
                        Message = $"invalid {defaultName}: {opts.DefaultProblem} (draft -01 §annotation-extensions)",
                    });
                }
                if (opts.Default != null && DefaultPlacementProblem(fd, defaultName) is { } placement)
                {
                    found.Add(new Violation(path, fd.FullName, placement, opts.Default)
                    {
                        Message = $"invalid {defaultName} = \"{opts.Default}\": {placement} (draft -01 §annotation-extensions)",
                    });
                }
                if (opts.Required && opts.Default != null)
                {
                    var detail = $"{requiredName} and {defaultName} are exclusive on one field: " + RequiredDefaultReason;
                    found.Add(new Violation(path, fd.FullName, detail)
                    {
                        Message = $"invalid {requiredName}: {detail} (draft -01 §annotation-extensions)",
                    });
                }
                if (fd.RealContainingOneof is { } oneof)
                {
                    if (opts.Required)
                    {
                        var detail = $"{requiredName} is not valid on a member of oneof \"{oneof.Name}\": read per field it demands that one arm always be chosen, which makes every other arm undecodable";
                        found.Add(new Violation(path, fd.FullName, detail, oneof.Name)
                        {
                            Message = $"invalid {requiredName}: {detail} (draft -01 §annotation-extensions)",
                        });
                    }
                    if (opts.Default != null) oneofDefaults.Add((oneof, fd, opts));
                }
            }
            CheckOneofDefaults(path, oneofDefaults, found);
            foreach (var oneof in msg.Oneofs)
            {
                if (!oneof.IsSynthetic && IsReserved(oneof.Name)) found.Add(Reserved(path, "oneof", oneof.FullName, oneof.Name));
            }
            WalkMessages(path, msg.NestedTypes, found);
            WalkEnums(path, msg.EnumTypes, found);
        }
    }

    // With two defaults on one oneof some default must win, and deciding by
    // declaration order would give meaning to a detail authors may change.
    private static void CheckOneofDefaults(string path, List<(OneofDescriptor Oneof, FieldDescriptor Field, AnnotationsRuntime.PxfOptions Options)> annotated, List<Violation> found)
    {
        foreach (var group in annotated.GroupBy(a => a.Oneof).Where(g => g.Count() > 1))
        {
            string names = string.Join(", ", group.Select(m => m.Field.Name));
            string detail = $"at most one member of oneof \"{group.Key.Name}\" may carry a default; {group.Count()} do ({names})";
            foreach (var (_, field, opts) in group)
            {
                string spelling = opts.DefaultInCarrier ? "@default" : "(pxf.default)";
                found.Add(new Violation(path, field.FullName, detail, opts.Default)
                {
                    Message = $"invalid {spelling} = \"{opts.Default}\": {detail} (draft -01 §annotation-extensions)",
                });
            }
        }
    }

    private static string? DefaultPlacementProblem(FieldDescriptor fd, string name)
    {
        if (fd.IsMap) return $"{name} is not valid on map fields: one literal cannot denote a map";
        if (fd.IsRepeated) return $"{name} is not valid on repeated fields: one literal cannot denote a list";
        if (fd.FieldType == FieldType.Group) return $"{name} is not valid on group fields";
        if (fd.FieldType == FieldType.Message && !WellKnown.Defaultable(fd.MessageType))
        {
            return $"{name} is not valid on message type {fd.MessageType.FullName}: no PXF literal denotes it";
        }
        return null;
    }

    private static void WalkEnums(string path, IEnumerable<EnumDescriptor> enums, List<Violation> found)
    {
        foreach (var e in enums)
        {
            foreach (var v in e.Values)
            {
                if (IsReserved(v.Name)) found.Add(Reserved(path, "enum value", EnumValueName(v), v.Name));
            }
        }
    }

    /// <summary>
    /// An enum value's full name as protobuf scopes it, and protowire-go
    /// names it: beside its enum, not inside it (<c>pkg.null</c>, or
    /// <c>pkg.Msg.null</c> for an enum nested in Msg). Google.Protobuf's
    /// <c>FullName</c> puts it inside the enum.
    /// </summary>
    internal static string EnumValueName(EnumValueDescriptor v)
    {
        string scope = v.EnumDescriptor.ContainingType?.FullName ?? v.File.Package;
        return scope.Length == 0 ? v.Name : scope + "." + v.Name;
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
