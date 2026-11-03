// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

/// <summary>
/// Dotted column paths (draft §3.4.4). A cell in column <c>a.b</c> stands for
/// <c>b = value</c> inside the block <c>a { ... }</c>, and every cell of a row
/// that shares a prefix lands in one block.
/// </summary>
internal static class DatasetPaths
{
    /// <summary>
    /// The header rules that need no schema: no empty path segment, no path
    /// twice, and no field beside a path inside it. Throws at the first column
    /// that breaks one.
    /// </summary>
    public static void CheckHeader(List<string> columns, List<Position> positions)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (HeaderError(columns, i) is { } message) throw new PxfException(positions[i], message);
        }
    }

    public static string? HeaderError(List<string> columns)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (HeaderError(columns, i) is { } message) return message;
        }
        return null;
    }

    private static string? HeaderError(List<string> columns, int i)
    {
        string c = columns[i];
        if (c.StartsWith('.') || c.EndsWith('.') || c.Contains("..", StringComparison.Ordinal))
        {
            return $"@dataset column \"{c}\": a column path has an empty segment (draft §3.4.4)";
        }
        for (int p = 0; p < i; p++)
        {
            string prev = columns[p];
            if (prev == c) return $"@dataset column \"{c}\" appears twice (draft §3.4.4)";
            if (IsPathPrefix(prev, c) || IsPathPrefix(c, prev))
            {
                return $"@dataset column \"{c}\" overlaps column \"{prev}\": a field and a path inside it cannot both be columns (draft §3.4.4)";
            }
        }
        return null;
    }

    /// <summary>
    /// Checks every column against the row type, and returns the first problem
    /// or null. Each segment names a field, and each one but the last a
    /// singular message field whose type is not written as a literal. A path
    /// has no index, so it cannot go through a repeated or map field. A
    /// pxf.Secret has a block form, so a path may go into one.
    /// </summary>
    public static string? CheckColumns(MessageDescriptor desc, IReadOnlyList<string> columns)
    {
        if (HeaderError(columns.ToList()) is { } header) return header;
        foreach (string column in columns)
        {
            var md = desc;
            var segments = column.Split('.');
            for (int i = 0; i < segments.Length; i++)
            {
                var fd = md.FindFieldByName(segments[i]);
                if (fd == null) return $"@dataset column \"{column}\": {md.FullName} has no field \"{segments[i]}\"";
                if (i == segments.Length - 1) break;
                string at = string.Join('.', segments[..(i + 1)]);
                if (fd.IsMap)
                {
                    return $"@dataset column \"{column}\": \"{at}\" is a map field, and a column path cannot go through one; use a value cell for \"{at}\" (draft §3.4.4)";
                }
                if (fd.IsRepeated)
                {
                    return $"@dataset column \"{column}\": \"{at}\" is a repeated field, and a column path cannot go through one; use a value cell for \"{at}\" (draft §3.4.4)";
                }
                if (fd.FieldType is not (FieldType.Message or FieldType.Group))
                {
                    return $"@dataset column \"{column}\": \"{at}\" is not a message field";
                }
                var mt = fd.MessageType;
                if (WellKnown.IsTimestamp(mt) || WellKnown.IsDuration(mt) || WellKnown.IsWrapper(mt)
                    || WellKnown.IsBigInt(mt) || WellKnown.IsDecimal(mt) || WellKnown.IsBigFloat(mt))
                {
                    return $"@dataset column \"{column}\": \"{at}\" is a {mt.FullName}, which is written as a literal; a column path cannot go into it (draft §3.4.4)";
                }
                md = mt;
            }
        }
        return null;
    }

    private static bool IsPathPrefix(string prefix, string path) =>
        path.Length > prefix.Length && path[prefix.Length] == '.' && path.StartsWith(prefix, StringComparison.Ordinal);
}
