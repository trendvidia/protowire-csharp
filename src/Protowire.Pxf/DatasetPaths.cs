// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
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

    private static bool IsPathPrefix(string prefix, string path) =>
        path.Length > prefix.Length && path[prefix.Length] == '.' && path.StartsWith(prefix, StringComparison.Ordinal);
}
