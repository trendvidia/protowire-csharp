// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pb;

/// <summary>
/// The per-call limits of draft -01 § Mandatory Limits (HARDENING.md) that a PB
/// decode applies. The defaults are the draft's values.
/// </summary>
public sealed record PbLimits
{
    /// <summary>The default limits.</summary>
    public static readonly PbLimits Default = new();

    /// <summary>Largest input to one decode call, in bytes.</summary>
    public int MaxMessageSize { get; init; } = 64 << 20;

    /// <summary>Largest magnitude of a pxf.Decimal scale.</summary>
    public int MaxNumericLiteralDigits { get; init; } = 4096;

    /// <summary>Most elements in one repeated or map field.</summary>
    public int MaxRepeatedCount { get; init; } = 64 << 20;

    /// <summary>Largest magnitude of a pxf.BigFloat value's binary exponent.</summary>
    public int MaxBigFloatExponent { get; init; } = 16384;
}
