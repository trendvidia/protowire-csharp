// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pxf;

/// <summary>
/// The per-call limits of draft -01 § Mandatory Limits (HARDENING.md). The
/// defaults are the draft's values. Lower one to refuse sooner; the draft lets
/// a caller raise them too.
/// </summary>
public sealed record PxfLimits
{
    /// <summary>The default limits.</summary>
    public static readonly PxfLimits Default = new();

    /// <summary>Largest input to one decode or parse call, in bytes.</summary>
    public int MaxMessageSize { get; init; } = 64 << 20;

    /// <summary>Deepest <c>{</c> or <c>[</c> nesting, the root message being 0.</summary>
    public int MaxNestingDepth { get; init; } = 100;

    /// <summary>Most significant digits in a big-number literal, and the largest Decimal scale.</summary>
    public int MaxNumericLiteralDigits { get; init; } = 4096;

    /// <summary>Largest decoded length of one <c>b"…"</c> literal.</summary>
    public int MaxBytesLiteralLength { get; init; } = 64 << 20;

    /// <summary>Most elements in one repeated or map field.</summary>
    public int MaxRepeatedCount { get; init; } = 64 << 20;

    /// <summary>Largest magnitude of a pxf.BigFloat value's binary exponent.</summary>
    public int MaxBigFloatExponent { get; init; } = 16384;
}
