// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf.Tests.Bench;

namespace Protowire.Pxf.Tests;

// The spellings tests/Protowire.Conformance compares with protowire-go, pinned
// here too because that comparison needs Go.
public class WriterTests
{
    // #19: what the reference writes, and every branch reads back.
    [Theory]
    [InlineData(0, 0, "0s")]
    [InlineData(1, 500_000_000, "1.5s")]
    [InlineData(-1, -500_000_000, "-1.5s")]
    [InlineData(0, 100, "100ns")]
    [InlineData(0, 1_500, "1.5µs")]
    [InlineData(0, 1_234_567, "1.234567ms")]
    [InlineData(60, 0, "1m0s")]
    [InlineData(5400, 500_000_000, "1h30m0.5s")]
    public void Duration_WritesAsGoDoes_AndReadsBack(long seconds, int nanos, string text)
    {
        var config = new Config { Timeout = new Google.Protobuf.WellKnownTypes.Duration { Seconds = seconds, Nanos = nanos } };
        string written = new Encoder().Marshal(config);
        Assert.Equal($"timeout = {text}\n", written);

        var got = new Config();
        new Decoder().Unmarshal(written, got);
        Assert.Equal(seconds, got.Timeout.Seconds);
        Assert.Equal(nanos, got.Timeout.Nanos);
    }

    // #35 and #36: one rule for a string key, shared with keyed entry names.
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a.b", "a.b")]
    [InlineData("true", "\"true\"")]
    [InlineData("false", "\"false\"")]
    [InlineData("null", "\"null\"")]
    [InlineData("123", "\"123\"")]
    [InlineData(".e", "\".e\"")]
    [InlineData("1.5", "\"1.5\"")]
    [InlineData("a b", "\"a b\"")]
    public void MapKey_Spelling(string key, string spelled)
    {
        var config = new Config();
        config.Labels[key] = "v";
        Assert.Equal($"labels = {{\n  {spelled}: \"v\"\n}}\n", new Encoder().Marshal(config));
    }

    [Theory]
    [InlineData("labels = {\n  \"true\": \"v\"\n}\n", "\"true\"")]
    [InlineData("labels = {\n  \"a.b\": \"v\"\n}\n", "a.b")]
    [InlineData("labels = {\n  \"plain\": \"v\"\n}\n", "plain")]
    [InlineData("labels = {\n  123: \"v\"\n}\n", "123")]
    public void Format_MapKey_Spelling(string doc, string spelled) =>
        Assert.Equal($"labels = {{\n  {spelled}: \"v\"\n}}\n", Format.FormatDocument(Parser.Parse(doc)));

    // #47: testdata/bigfloat/enc-*, and its digit rule.
    [Theory]
    [InlineData("0p64", "0p64")]
    [InlineData("-0p64", "-0p64")]
    [InlineData("0", "0")]
    [InlineData("-0", "-0")]
    [InlineData("1.5p53", "1.5p53")]
    [InlineData("0.1p53", "0.10000000000000001p53")]
    [InlineData("1000p64", "1000p64")]
    [InlineData("1.79769313486231590773e+308p64", "1.79769313486231590773e+308p64")]
    public void BigFloat_Literal(string literal, string written)
    {
        var v = BigFloatText.Parse(literal, 4096, 16384);
        Assert.Equal(written, BigFloatText.Format(v, 4096, 16384));
    }

    [Theory]
    [InlineData("1p0", "not a positive bit count")]
    [InlineData("1p99999999999999999999999", "not a positive bit count")]
    [InlineData("1p13604", "renders to 4097 digits")]
    [InlineData("1e646456992", "MaxBigFloatExponent")]
    [InlineData("1e-646456992", "below the wire's exponent range")]
    public void BigFloat_Refused(string literal, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => BigFloatText.Parse(literal, 4096, 16384)).Message);

    // #100: a literal far past MaxBigFloatExponent is refused in protowire-go's
    // words, which come from math/big: its exact binary exponent, or that it
    // leaves big.Float's range or the wire's, or that its exponent is too long
    // for big.Float to scan. Printed by protowire-go f8a01b1 for `bf = <literal>`.
    [Theory]
    [InlineData("1e49325", "big float literal 1e49325 has binary exponent 163855, and MaxBigFloatExponent=16384 bounds its magnitude")]
    [InlineData("1e646456992", "big float literal 1e646456992 has binary exponent 2147483644, and MaxBigFloatExponent=16384 bounds its magnitude")]
    [InlineData("1e646456993", "big float literal 1e646456993 is above big.Float's range")]
    [InlineData("1e-646456992", "big float literal 1e-646456992 is below the wire's exponent range")]
    [InlineData("1e-646457012", "big float literal 1e-646457012 is below big.Float's range")]
    [InlineData("1e2147483647", "invalid big float: 1e2147483647")]
    [InlineData("0e99999999999999999999", "invalid big float: 0e99999999999999999999")]
    public void BigFloat_RefusedAsTheReferenceRefuses(string literal, string message) =>
        Assert.Equal(message, Assert.Throws<FormatException>(() => BigFloatText.Parse(literal, 4096, 16384)).Message);

    // A zero ignores its exponent, however large, if it scans (#100).
    [Fact]
    public void BigFloat_ZeroWithAHugeExponent() =>
        Assert.True(BigFloatText.Parse("0e2147483648", 4096, 16384).Mantissa.IsZero);

    // protowire-go's TestPow2Digits: where a float64 is a digit high.
    [Theory]
    [InlineData(1u, 2)]
    [InlineData(64u, 21)]
    [InlineData(53u, 17)]
    [InlineData(13603u, 4096)]
    public void BigFloat_DigitRule(uint prec, int digits) => Assert.Equal(digits, BigFloatText.Digits(prec));

    [Theory]
    [InlineData(1e21, "1e+21")]
    [InlineData(100000.0, "100000")]
    [InlineData(1000000.0, "1e+06")]
    [InlineData(123456789.0, "1.23456789e+08")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(double.PositiveInfinity, "inf")]
    [InlineData(double.NegativeInfinity, "-inf")]
    [InlineData(double.NaN, "nan")]
    public void Float_WritesAsGoDoes(double value, string text) =>
        Assert.Equal(text, GoFormat.Float(value, single: false));

    [Fact]
    public void Writer_RefusesWhatNoReaderWouldTake()
    {
        var deep = new Config();
        Assert.Equal("", new Encoder { MaxNestingDepth = 0 }.Marshal(deep));
        deep.Tags.Add("t");
        Assert.ThrowsAny<PxfException>(() => new Encoder { MaxNestingDepth = 0 }.Marshal(deep));
    }
}
