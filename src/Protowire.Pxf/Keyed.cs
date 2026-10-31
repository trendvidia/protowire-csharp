// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

/// <summary>Which keyed-repeated-field rule a document broke (draft -01 "Decoding").</summary>
public enum KeyedErrorKind
{
    /// <summary>Two entries of one keyed block with the same unquoted name.</summary>
    DuplicateKey = 1,

    /// <summary>An explicit key-field assignment disagrees with the entry name.</summary>
    KeyConflict,

    /// <summary>A quoted entry name <c>""</c>, or an explicit empty key assignment.</summary>
    EmptyKey,

    /// <summary>A quoted entry name outside a keyed repeated field's block.</summary>
    QuotedNameUnkeyed,
}

/// <summary>A keyed-repeated-field decode error.</summary>
public sealed class KeyedException : PxfException
{
    public KeyedErrorKind Kind { get; }

    /// <summary>The keyed field involved; empty for <see cref="KeyedErrorKind.QuotedNameUnkeyed"/>.</summary>
    public string Field { get; }

    /// <summary>The offending key or entry name, when one applies.</summary>
    public string Key { get; }

    internal KeyedException(Position pos, KeyedErrorKind kind, string field, string key, string message)
        : base(pos, message)
    {
        Kind = kind;
        Field = field;
        Key = key;
    }
}

/// <summary>
/// The key-field checks for the immediate body of one element of a keyed
/// repeated field. An explicit key assignment may not be empty and, in the
/// named form, must agree with the entry name.
/// </summary>
internal sealed record KeyedElemState(string Field, string KeyName, string EntryName, bool Named)
{
    public void CheckExplicitKey(string value, Position pos)
    {
        if (value.Length == 0)
        {
            throw new KeyedException(pos, KeyedErrorKind.EmptyKey, Field, "",
                $"explicit empty-string assignment to key field \"{KeyName}\" of keyed field \"{Field}\": the empty string is not a valid key");
        }
        if (Named && value != EntryName)
        {
            throw new KeyedException(pos, KeyedErrorKind.KeyConflict, Field, EntryName,
                $"key field \"{KeyName}\" = \"{value}\" conflicts with entry name \"{EntryName}\" in keyed field \"{Field}\"");
        }
    }
}

/// <summary>
/// Keyed repeated fields (draft-trendvidia-protowire-01, "Keyed Repeated
/// Fields"): a repeated message field carrying <c>(pxf.key)</c> may be
/// written as a block of named blocks, entry name = key-field value, entry
/// order = list order. This class holds the descriptor helpers, the
/// entry-name spelling rule and the schema-aware fmt canonicalizer. The
/// decode loop is in <see cref="Decoder"/>, keyed emission in
/// <see cref="Encoder"/>, the placement check in <see cref="Schema"/>.
/// Mirrors <c>protowire-go/encoding/pxf/keyed.go</c>.
/// </summary>
public static class Keyed
{
    /// <summary>
    /// The key field of a keyed repeated field: the singular string field
    /// of <paramref name="fd"/>'s element message that its <c>(pxf.key)</c>
    /// names. Null when there is no annotation or its placement is invalid;
    /// <see cref="Schema.ValidateDescriptor"/> reports invalid placements.
    /// </summary>
    public static FieldDescriptor? KeyField(FieldDescriptor fd)
    {
        if (!fd.IsRepeated || fd.IsMap || fd.FieldType != FieldType.Message) return null;
        var name = AnnotationsRuntime.GetKey(fd);
        if (string.IsNullOrEmpty(name)) return null;
        var keyFd = fd.MessageType.FindFieldByName(name);
        if (keyFd == null || keyFd.IsRepeated || keyFd.IsMap || keyFd.FieldType != FieldType.String) return null;
        return keyFd;
    }

    /// <summary>
    /// True when <paramref name="s"/> can be written as an unquoted entry
    /// name: it matches the <c>identifier</c> production and is not one of
    /// the value keywords <c>null</c>, <c>true</c>, <c>false</c>.
    /// </summary>
    public static bool IdentSafeEntryName(string s)
    {
        if (s.Length == 0 || s is "true" or "false" or "null") return false;
        if (!IsIdentStart(s[0])) return false;
        foreach (char ch in s.AsSpan(1))
        {
            if (!IsIdentStart(ch) && !char.IsAsciiDigit(ch) && ch != '.') return false;
        }
        return true;
    }

    private static bool IsIdentStart(char ch) => char.IsAsciiLetter(ch) || ch == '_';

    /// <summary>
    /// Rewrites <paramref name="doc"/> in place to the canonical keyed form,
    /// using <paramref name="desc"/> as the document's message schema. Per
    /// keyed field binding it converts an eligible anonymous list to the
    /// keyed block form, normalizes <c>name = { ... }</c> to
    /// <c>name { ... }</c>, unquotes identifier-safe entry names and drops
    /// redundant agreeing key assignments.
    ///
    /// <para>
    /// Ineligible bindings (duplicate, absent or empty keys) stay anonymous
    /// and entries the schema does not resolve are left alone, so formatting
    /// an invalid document never destroys information. Follow with
    /// <see cref="Format.FormatDocument"/>; the pair is the <c>pxf fmt</c>
    /// pipeline.
    /// </para>
    /// </summary>
    public static void Canonicalize(Document doc, MessageDescriptor desc) => CanonEntries(doc.Entries, desc);

    internal static KeyedException EmptyName(Position pos, string field) =>
        new(pos, KeyedErrorKind.EmptyKey, field, "",
            $"empty entry name in keyed field \"{field}\": the empty string is not a valid key");

    internal static KeyedException DuplicateKey(Position pos, string field, string key) =>
        new(pos, KeyedErrorKind.DuplicateKey, field, key,
            $"duplicate key \"{key}\" in keyed field \"{field}\"");

    internal static KeyedException QuotedNameUnkeyed(Position pos, string name) =>
        new(pos, KeyedErrorKind.QuotedNameUnkeyed, "", name,
            $"quoted entry name \"{name}\" is only valid inside a keyed repeated field's block (draft -01 §3.13)");

    private static bool IsSingularMessage(FieldDescriptor fd) =>
        fd.FieldType == FieldType.Message && !fd.IsRepeated;

    private static void CanonEntries(List<IEntry> entries, MessageDescriptor desc)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            switch (entries[i])
            {
                case Assignment { KeyQuoted: false } a:
                    if (desc.FindFieldByName(a.Key) is { } assigned)
                    {
                        entries[i] = CanonAssignment(a, assigned);
                    }
                    break;
                case Block { NameQuoted: false } b:
                    if (desc.FindFieldByName(b.Name) is not { } fd) break;
                    if (KeyField(fd) is { } keyFd)
                    {
                        CanonKeyedEntries(b.Entries, fd, keyFd);
                    }
                    else if (IsSingularMessage(fd))
                    {
                        CanonEntries(b.Entries, fd.MessageType);
                    }
                    break;
            }
        }
    }

    private static IEntry CanonAssignment(Assignment a, FieldDescriptor fd)
    {
        if (fd.IsMap)
        {
            var valueFd = fd.MessageType.FindFieldByNumber(2);
            if (a.Value is BlockVal map && valueFd.FieldType == FieldType.Message)
            {
                foreach (var entry in map.Entries)
                {
                    if (entry is MapEntry { Value: BlockVal inner })
                    {
                        CanonEntries(inner.Entries, valueFd.MessageType);
                    }
                }
            }
            return a;
        }
        if (fd.IsRepeated)
        {
            if (fd.FieldType != FieldType.Message) return a;
            var keyFd = KeyField(fd);
            switch (a.Value)
            {
                case ListVal list when keyFd != null:
                    return CanonAnonymous(a, list, fd, keyFd);
                case ListVal list:
                    CanonElements(list, fd);
                    break;
                case BlockVal block when keyFd != null:
                    // `children = { ... }` → `children { ... }`.
                    CanonKeyedEntries(block.Entries, fd, keyFd);
                    return new Block { Pos = a.Pos, Name = a.Key, Entries = block.Entries, LeadingComments = a.LeadingComments };
            }
            return a;
        }
        if (fd.FieldType == FieldType.Message && a.Value is BlockVal sub)
        {
            CanonEntries(sub.Entries, fd.MessageType);
        }
        return a;
    }

    private static void CanonElements(ListVal list, FieldDescriptor fd)
    {
        foreach (var elem in list.Elements)
        {
            if (elem is BlockVal bv)
            {
                CanonEntries(bv.Entries, fd.MessageType);
            }
        }
    }

    /// <summary>
    /// Normalizes the entries of a keyed block: assignment-spelled entries
    /// become blocks, identifier-safe names are unquoted, redundant key
    /// assignments are dropped, and entry bodies are canonicalized.
    /// </summary>
    private static void CanonKeyedEntries(List<IEntry> entries, FieldDescriptor fd, FieldDescriptor keyFd)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            Block? block = entries[i] switch
            {
                Block b => b,
                Assignment { Value: BlockVal bv } a => new Block
                {
                    Pos = a.Pos,
                    Name = a.Key,
                    NameQuoted = a.KeyQuoted,
                    Entries = bv.Entries,
                    LeadingComments = a.LeadingComments,
                },
                _ => null,
            };
            if (block == null) continue; // malformed entry; leave untouched
            if (block.NameQuoted && IdentSafeEntryName(block.Name))
            {
                block = block with { NameQuoted = false };
            }
            entries[i] = block;
            DropKeyAssignments(block.Entries, keyFd.Name, block.Name);
            CanonEntries(block.Entries, fd.MessageType);
        }
    }

    /// <summary>
    /// Removes <c>keyName = "entryName"</c> from <paramref name="entries"/>.
    /// A disagreeing or non-string assignment stays: the document is invalid
    /// and formatting must not change what it says. Leading comments of a
    /// dropped assignment move to the next surviving entry.
    /// </summary>
    private static void DropKeyAssignments(List<IEntry> entries, string keyName, string entryName)
    {
        var pending = new List<Comment>();
        var kept = new List<IEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is Assignment { KeyQuoted: false, Value: StringVal sv } a && a.Key == keyName && sv.Value == entryName)
            {
                pending.AddRange(a.LeadingComments);
                continue;
            }
            if (pending.Count > 0)
            {
                var comments = entry switch
                {
                    Assignment e => e.LeadingComments,
                    Block e => e.LeadingComments,
                    MapEntry e => e.LeadingComments,
                    _ => null,
                };
                comments?.InsertRange(0, pending);
                pending.Clear();
            }
            kept.Add(entry);
        }
        entries.Clear();
        entries.AddRange(kept);
    }

    /// <summary>
    /// Converts an eligible anonymous list binding to the keyed block form.
    /// Eligible means every element is a block with exactly one non-empty,
    /// distinct explicit key assignment; anything else stays anonymous, and
    /// its element bodies are still canonicalized.
    /// </summary>
    private static IEntry CanonAnonymous(Assignment a, ListVal list, FieldDescriptor fd, FieldDescriptor keyFd)
    {
        var elements = new List<(string Key, BlockVal Body)>(list.Elements.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in list.Elements)
        {
            if (value is not BlockVal body
                || ExplicitKeyOf(body.Entries, keyFd.Name) is not { Length: > 0 } key
                || !seen.Add(key))
            {
                CanonElements(list, fd);
                return a;
            }
            elements.Add((key, body));
        }

        var block = new Block { Pos = a.Pos, Name = a.Key, LeadingComments = a.LeadingComments };
        foreach (var (key, body) in elements)
        {
            DropKeyAssignments(body.Entries, keyFd.Name, key);
            CanonEntries(body.Entries, fd.MessageType);
            block.Entries.Add(new Block
            {
                Pos = body.Pos,
                Name = key,
                NameQuoted = !IdentSafeEntryName(key),
                Entries = body.Entries,
            });
        }
        return block;
    }

    /// <summary>
    /// The value of the single explicit string assignment to
    /// <paramref name="keyName"/>, or null when there is none, more than
    /// one, a quoted spelling, or a non-string value.
    /// </summary>
    private static string? ExplicitKeyOf(List<IEntry> entries, string keyName)
    {
        string? key = null;
        foreach (var entry in entries)
        {
            if (entry is not Assignment a || a.Key != keyName) continue;
            if (a.Value is not StringVal sv || a.KeyQuoted || key != null) return null;
            key = sv.Value;
        }
        return key;
    }
}
