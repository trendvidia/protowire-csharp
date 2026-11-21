// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf.Reflection;
using Protowire.Pb;

namespace Protowire.Pxf;

/// <summary>
/// Reads PB bytes by a message's descriptor and refuses what protobuf-go
/// refuses, before Google.Protobuf's parser reads them (#120).
/// </summary>
/// <remarks>
/// Google.Protobuf 3.36.2 refuses wire type 6 or 7 on a message's own fields,
/// but a generated map codec reads an entry's key and value and skips any
/// other field with <c>SkipLastField</c>, which consumes nothing for wire type
/// 6 or 7. So it accepts <c>0a011f</c> as a <c>google.protobuf.Struct</c>,
/// which protobuf-go refuses, as it refuses those wire types on any field at
/// any depth (trendvidia/protowire#614). This walk skips every field it does
/// not read into with <see cref="Wire.ConsumeFieldValue"/>, and reads into
/// each submessage, group and map entry the descriptor names, so a map entry
/// is checked wherever it sits. It reads into nothing else: a string or bytes
/// field whose content would not parse as a message is not a message.
/// </remarks>
internal static class WireCheck
{
    // A message being read: its descriptor, where its bytes end, the number
    // of the group it is (0 if it is length-delimited), and whether it is a
    // map entry, which names its fields in an error.
    private readonly record struct Frame(MessageDescriptor Desc, int End, long Group, bool Entry);

    // protowire.MaxValidNumber.
    private const long MaxValidNumber = (1 << 29) - 1;

    /// <summary>
    /// Null when the bytes hold nothing protobuf-go refuses, or why they do,
    /// in protowire-go's words, such as <c>corrupt field 3 in map entry:
    /// proto: cannot parse reserved wire type</c>.
    /// </summary>
    /// <param name="desc">The message the bytes are.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="maxDepth">
    /// The recursion limit of the parser that reads the bytes next. The walk
    /// counts as Google.Protobuf counts, one descent for each submessage, group
    /// and map entry (HARDENING.md § Recursion), and goes no deeper than this:
    /// the parser refuses anything past it, so the walk skips it unread. Its
    /// stack of open messages is that deep at most.
    /// </param>
    internal static string? Check(MessageDescriptor desc, ReadOnlySpan<byte> data, int maxDepth)
    {
        var open = new Stack<Frame>();
        var cur = new Frame(desc, data.Length, 0, false);
        int pos = 0;
        while (true)
        {
            if (pos == cur.End)
            {
                if (cur.Group != 0) return Corrupt(cur.Group, open.Peek().Entry, Wire.ErrTruncated);
                if (open.Count == 0) return null;
                cur = open.Pop();
                continue;
            }
            var rest = data[pos..cur.End];
            // protobuf-go refuses a field number past 2^29-1 in a message or
            // map entry. Google.Protobuf 3.36.2 accepts one: 888080801005,
            // field 2^29+1, parses as an adversarial.v1.BigIntHolder.
            int n = Wire.ConsumeTag(rest, out long num, out int typ);
            if (n < 0 || num > MaxValidNumber) return cur.Entry ? "corrupt tag in map entry" : "corrupt tag";
            pos += n;
            rest = rest[n..];

            if (typ == Wire.EndGroup && cur.Group == num)
            {
                cur = open.Pop();
                continue;
            }
            var fd = open.Count < maxDepth ? cur.Desc.FindFieldByNumber((int)num) : null;
            if (fd?.FieldType == FieldType.Message && typ == Wire.Bytes)
            {
                int m = Wire.ConsumeVarint(rest, out ulong len);
                if (m < 0) return Corrupt(num, cur.Entry, m);
                if (len > (ulong)(rest.Length - m)) return Corrupt(num, cur.Entry, Wire.ErrTruncated);
                open.Push(cur);
                pos += m;
                cur = new Frame(fd.MessageType, pos + (int)len, 0, fd.IsMap);
                continue;
            }
            if (fd?.FieldType == FieldType.Group && typ == Wire.StartGroup)
            {
                open.Push(cur);
                cur = cur with { Desc = fd.MessageType, Group = num, Entry = false };
                continue;
            }
            // Not read into: an unknown field, a known one whose wire type
            // does not match, a scalar, or a message past the depth limit.
            n = Wire.ConsumeFieldValue(num, typ, rest);
            if (n < 0) return Corrupt(num, cur.Entry, n);
            pos += n;
        }
    }

    // pb.skipField's text in protowire-go.
    private static string Corrupt(long num, bool entry, int code) =>
        $"corrupt field {num}{(entry ? " in map entry" : "")}: {Wire.ParseError(code)}";
}
