// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

/// <summary>
/// Retired option numbers (STABILITY.md promise 3).
///
/// <para>
/// Before protowire v1.12.0 the family's extensions lived in an unregistered
/// 50000–59999 range; they moved to the registered block 1314–1363, and the old
/// numbers are dead. A descriptor compiled before the move still carries them,
/// and a reader of the registered numbers alone would see none of its
/// annotations: <c>(pxf.required)</c> stops being enforced and the schema binds
/// documents it should reject. So such a descriptor is refused, naming the number.
/// </para>
///
/// <para>
/// The check is scoped two ways, because 50000-and-up is where every project
/// that never registered its numbers puts them: a number counts only on the
/// Options kind its retired allocation used, and only in a file that imports one
/// of protowire's own annotation files. Mirrors
/// <c>protowire-go/encoding/pxf/retired.go</c>.
/// </para>
/// </summary>
internal static class Retired
{
    private readonly record struct RetiredNumber(string Was, int Now, string File);

    private const string SbeFile = "sbe/annotations.proto";
    private const string PxfFile = "pxf/annotations.proto";
    private const string SchemaFile = "protowire/schema/v1/descriptor.proto";
    private const string ProtocheckFile = "protocheck's annotations";

    private static readonly Dictionary<int, RetiredNumber> FileOptions = new()
    {
        [50100] = new("(sbe.schema_id)", 1319, SbeFile),
        [50101] = new("(sbe.version)", 1320, SbeFile),
        [50400] = new("the file_annotations carrier", 1327, SchemaFile),
        [50401] = new("the functions carrier", 1328, SchemaFile),
        [50402] = new("the annotation_decls carrier", 1329, SchemaFile),
        [50403] = new("the type_decls carrier", 1330, SchemaFile),
        [50404] = new("the source_map carrier", 1331, SchemaFile),
    };

    private static readonly Dictionary<int, RetiredNumber> MessageOptions = new()
    {
        [50200] = new("(sbe.template_id)", 1321, SbeFile),
        [50400] = new("the message_annotations carrier", 1327, SchemaFile),
        [51001] = new("a protocheck message constraint", 1348, ProtocheckFile),
    };

    private static readonly Dictionary<int, RetiredNumber> FieldOptions = new()
    {
        [50000] = new("(pxf.required)", 1314, PxfFile),
        [50001] = new("(pxf.default)", 1315, PxfFile),
        [50002] = new("(pxf.key)", 1316, PxfFile),
        [50300] = new("(sbe.length)", 1322, SbeFile),
        [50301] = new("(sbe.encoding)", 1323, SbeFile),
        [50400] = new("the field_annotations carrier", 1327, SchemaFile),
        [51000] = new("a protocheck field constraint", 1347, ProtocheckFile),
    };

    private static readonly Dictionary<int, RetiredNumber> OneofOptions = new()
    {
        [50400] = new("the oneof_annotations carrier", 1327, SchemaFile),
        [51002] = new("a protocheck oneof constraint", 1349, ProtocheckFile),
    };

    // Enum, enum value, service and method options carried only the carrier.
    private static readonly Dictionary<int, RetiredNumber> CarrierOnly = new()
    {
        [50400] = new("the *_annotations carrier", 1327, SchemaFile),
    };

    // The imports that mark a file as compiled against protowire's
    // annotations, in either era.
    private static readonly HashSet<string> ProtowireAnnotationFiles = new(StringComparer.Ordinal)
    {
        PxfFile,
        SbeFile,
        "protowire/schema/v1/annotations.proto",
        SchemaFile,
    };

    /// <summary>
    /// Appends a violation for every retired option number <paramref name="file"/>'s
    /// own elements carry, when the file imports protowire's annotations.
    /// </summary>
    public static void Walk(FileDescriptor file, List<Violation> found)
    {
        if (!file.Dependencies.Any(d => ProtowireAnnotationFiles.Contains(d.Name))) return;
        var scan = new Scan(file, found);
        scan.Check(file.Name, "FileOptions", file.GetOptions(), FileOptions);
        scan.Messages(file.MessageTypes);
        scan.Enums(file.EnumTypes);
        foreach (var s in file.Services)
        {
            scan.Check(s.FullName, "ServiceOptions", s.GetOptions(), CarrierOnly);
            foreach (var m in s.Methods)
            {
                scan.Check(m.FullName, "MethodOptions", m.GetOptions(), CarrierOnly);
            }
        }
    }

    private sealed class Scan(FileDescriptor file, List<Violation> found)
    {
        // (extendee, number) of every extension the closure declares on an
        // Options message; built on the first hit, which most files never have.
        private HashSet<(string, int)>? declared;

        public void Messages(IEnumerable<MessageDescriptor> messages)
        {
            foreach (var md in messages)
            {
                Check(md.FullName, "MessageOptions", md.GetOptions(), MessageOptions);
                foreach (var f in md.Fields.InDeclarationOrder())
                {
                    Check(f.FullName, "FieldOptions", f.GetOptions(), FieldOptions);
                }
                foreach (var o in md.Oneofs)
                {
                    Check(o.FullName, "OneofOptions", o.GetOptions(), OneofOptions);
                }
                Enums(md.EnumTypes);
                Messages(md.NestedTypes);
            }
        }

        public void Enums(IEnumerable<EnumDescriptor> enums)
        {
            foreach (var e in enums)
            {
                Check(e.FullName, "EnumOptions", e.GetOptions(), CarrierOnly);
                // An enum value is scoped beside its enum, not inside it, as
                // the reference names it.
                string scope = e.ContainingType?.FullName ?? e.File.Package;
                foreach (var v in e.Values)
                {
                    string name = scope.Length == 0 ? v.Name : scope + "." + v.Name;
                    Check(name, "EnumValueOptions", v.GetOptions(), CarrierOnly);
                }
            }
        }

        /// <summary>
        /// The reference reads an Options message's unknown bytes. The options
        /// here serialize registered extensions too, so a number some file of the
        /// closure declares as an extension of this Options kind is that
        /// extension, not a retired option, and is skipped.
        /// </summary>
        public void Check(string element, string kind, IMessage? opts, Dictionary<int, RetiredNumber> table)
        {
            if (opts == null) return;
            var input = new CodedInputStream(opts.ToByteArray());
            try
            {
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    int num = WireFormat.GetTagFieldNumber(tag);
                    input.SkipLastField();
                    if (!table.TryGetValue(num, out var r) || Declared().Contains(("google.protobuf." + kind, num))) continue;
                    string detail = $"option number {num} on {kind} was {r.Was} before protowire v1.12.0 and is {r.Now} now; " +
                        $"the descriptor predates the registered extension block and must be recompiled against the current {r.File}";
                    found.Add(new Violation(file.Name, element, detail, num.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    {
                        Subject = "",
                        Message = detail + " (STABILITY.md promise 3)",
                    });
                }
            }
            catch (InvalidProtocolBufferException)
            {
                // Unparseable bytes end the scan, as in the reference.
            }
        }

        private HashSet<(string, int)> Declared()
        {
            if (declared != null) return declared;
            declared = [];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<FileDescriptor>([file]);
            while (stack.Count > 0)
            {
                var f = stack.Pop();
                if (!seen.Add(f.Name)) continue;
                Add(f.Extensions.UnorderedExtensions);
                AddNested(f.MessageTypes);
                foreach (var dep in f.Dependencies) stack.Push(dep);
            }
            return declared;
        }

        private void AddNested(IEnumerable<MessageDescriptor> messages)
        {
            foreach (var md in messages)
            {
                Add(md.Extensions.UnorderedExtensions);
                AddNested(md.NestedTypes);
            }
        }

        private void Add(IEnumerable<FieldDescriptor> extensions)
        {
            foreach (var ext in extensions) declared!.Add((ext.ExtendeeType.FullName, ext.FieldNumber));
        }
    }
}
