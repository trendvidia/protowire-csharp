// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pxf;

/// <summary>
/// Byte-level brace matching used by directive parsing to slice raw body
/// content out of the input without re-lexing it as PXF. Mirrors the
/// lexer's string / comment handling so braces inside literals don't
/// confuse the brace count.
/// </summary>
internal static class BraceScan
{
    /// <summary>
    /// Returns the offset of the <c>}</c> matching the <c>{</c> at
    /// <paramref name="openOffset"/>, or -1 on unterminated input.
    /// </summary>
    public static int FindMatchingBrace(string input, int openOffset) => FindMatchingBrace(input, openOffset, out _);

    /// <summary>
    /// <see cref="FindMatchingBrace(string, int)"/>, and on -1 whether the
    /// body can never close however much input follows: a simple string or
    /// a bytes literal in it met a newline. Otherwise the input ended first.
    /// </summary>
    public static int FindMatchingBrace(string input, int openOffset, out bool failed)
    {
        failed = false;
        int depth = 1;
        int i = openOffset + 1;
        while (i < input.Length)
        {
            char ch = input[i];
            if (ch == '{')
            {
                depth++;
                i++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return i;
                i++;
            }
            else if (ch == '"')
            {
                int j = SkipString(input, i);
                if (j < 0)
                {
                    failed = j == NewlineInLiteral;
                    return -1;
                }
                i = j;
            }
            else if (ch == 'b' && i + 1 < input.Length && input[i + 1] == '"')
            {
                int j = SkipBytes(input, i);
                if (j < 0)
                {
                    failed = j == NewlineInLiteral;
                    return -1;
                }
                i = j;
            }
            else if (ch == '#')
            {
                i = SkipEOL(input, i + 1);
            }
            else if (ch == '/' && i + 1 < input.Length && input[i + 1] == '/')
            {
                i = SkipEOL(input, i + 2);
            }
            else if (ch == '/' && i + 1 < input.Length && input[i + 1] == '*')
            {
                int j = i + 2;
                bool closed = false;
                while (j + 1 < input.Length)
                {
                    if (input[j] == '*' && input[j + 1] == '/') { j += 2; closed = true; break; }
                    j++;
                }
                if (!closed) return -1;
                i = j;
            }
            else
            {
                i++;
            }
        }
        return -1;
    }

    // What the Skip helpers return for a literal that met a newline, which
    // no more input can close; -1 is a literal the input ended in.
    private const int NewlineInLiteral = -2;

    private static int SkipString(string input, int i)
    {
        if (i + 2 < input.Length && input[i + 1] == '"' && input[i + 2] == '"')
        {
            int j = i + 3;
            while (j + 2 < input.Length)
            {
                if (input[j] == '"' && input[j + 1] == '"' && input[j + 2] == '"') return j + 3;
                j++;
            }
            return -1;
        }
        int k = i + 1;
        while (k < input.Length)
        {
            if (input[k] == '\\')
            {
                if (k + 1 >= input.Length) return -1;
                k += 2;
                continue;
            }
            if (input[k] == '"') return k + 1;
            if (input[k] == '\n') return NewlineInLiteral;
            k++;
        }
        return -1;
    }

    private static int SkipBytes(string input, int i)
    {
        int j = i + 2;
        while (j < input.Length)
        {
            if (input[j] == '"') return j + 1;
            if (input[j] == '\n') return NewlineInLiteral;
            j++;
        }
        return -1;
    }

    private static int SkipEOL(string input, int i)
    {
        while (i < input.Length && input[i] != '\n') i++;
        return i;
    }
}
