// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Numerics;

namespace Protowire.Pb;

public class Decimal
{
    public BigInteger Unscaled { get; set; }
    public int Scale { get; set; }
    public bool Negative { get; set; }

    public Decimal() { }
    public Decimal(BigInteger unscaled, int scale, bool negative)
    {
        Unscaled = unscaled;
        Scale = scale;
        Negative = negative;
    }
}

public class BigFloat
{
    public BigInteger Mantissa { get; set; }
    public int Exponent { get; set; }
    public uint Precision { get; set; }
    public bool Negative { get; set; }

    public BigFloat() { }
    public BigFloat(BigInteger mantissa, int exponent, uint precision, bool negative)
    {
        Mantissa = mantissa;
        Exponent = exponent;
        Precision = precision;
        Negative = negative;
    }
}
