// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pb;

/// <summary>
/// The per-call limits of draft -01 § Mandatory Limits (HARDENING.md) that a PB
/// decode applies, and <see cref="MaxNestingDepth"/>, which
/// <see cref="Pb.Marshal(object, PbLimits)"/> applies too. The defaults are the
/// draft's values.
/// </summary>
public sealed record PbLimits
{
    /// <summary>The default limits.</summary>
    public static readonly PbLimits Default = new();

    /// <summary>
    /// Deepest value, counted from the root at depth 0, with every submessage
    /// and map entry one descent. The counter is threaded through the nested
    /// streams rather than left to <c>CodedInputStream</c>'s recursion limit,
    /// which starts again in each new stream.
    /// </summary>
    public int MaxNestingDepth { get; init; } = 100;

    /// <summary>Largest input to one decode call, in bytes.</summary>
    public int MaxMessageSize { get; init; } = 64 << 20;

    /// <summary>Largest magnitude of a pxf.Decimal scale.</summary>
    public int MaxNumericLiteralDigits { get; init; } = 4096;

    /// <summary>Most elements in one repeated or map field.</summary>
    public int MaxRepeatedCount { get; init; } = 64 << 20;

    /// <summary>Largest magnitude of a pxf.BigFloat value's binary exponent.</summary>
    public int MaxBigFloatExponent { get; init; } = 16384;
}
