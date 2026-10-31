// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Numerics;
using Protowire.Pb;
using Protowire.Pxf.Tests.Presence;

namespace Protowire.Pxf.Tests;

// HARDENING.md § Mandatory limits: MaxNumericLiteralDigits (4096) counts the
// significant digits of a literal's mantissa, so exactly 4096 is within it
// and 4097 is not. Leading zeros, the sign, the point, an exponent and a
// precision suffix are not counted (protowire#410). The corpus rows are
// pxf/long-numeric-4096 (accept) and pxf/long-numeric (reject). Generated
// messages, POCOs and (pxf.default) strings reach the big-number parsers by
// separate paths, so each one is pinned on both sides of the bound.
public class NumericLiteralLimitTests
{
    private const int Limit = 4096;

    public class Poco
    {
        public BigInteger BigInt { get; set; }
        public Protowire.Pb.Decimal? Dec { get; set; }
        public Protowire.Pb.BigFloat? BigFloat { get; set; }
    }

    private static string Digits(int n) => new('7', n);

    private static BigInteger Abs(BigNumbers m) =>
        new(m.BigInt.Abs.Span, isUnsigned: true, isBigEndian: true);

    private static void AssertDigitsRejected(Action decode, int digits)
    {
        var e = Assert.Throws<PxfException>(decode);
        Assert.Contains($"numeric literal has {digits} digits", e.Message);
        Assert.Contains("MaxNumericLiteralDigits=4096", e.Message);
    }

    [Fact]
    public void Generated_BigInt_AtTheBound_DecodesToThatValue()
    {
        var got = new BigNumbers();
        new Decoder().Unmarshal($"big_int = {Digits(Limit)}", got);
        Assert.Equal(BigInteger.Parse(Digits(Limit)), Abs(got));
    }

    [Fact]
    public void Generated_BigInt_OnePast_Rejected() => AssertDigitsRejected(
        () => new Decoder().Unmarshal($"big_int = {Digits(Limit + 1)}", new BigNumbers()), Limit + 1);

    [Fact]
    public void Generated_Decimal_AtTheBound_Accepted()
    {
        var got = new BigNumbers();
        new Decoder().Unmarshal($"dec = {Digits(Limit - 2)}.77", got);
        Assert.Equal(2, got.Dec.Scale);
    }

    [Fact]
    public void Generated_Decimal_OnePast_Rejected() => AssertDigitsRejected(
        () => new Decoder().Unmarshal($"dec = {Digits(Limit - 2)}.777", new BigNumbers()), Limit + 1);

    [Fact]
    public void Generated_Decimal_ScaleBound()
    {
        var got = new BigNumbers();
        new Decoder().Unmarshal($"dec = 0.{new string('0', Limit - 1)}1", got);
        Assert.Equal(Limit, got.Dec.Scale);

        var e = Assert.Throws<PxfException>(() =>
            new Decoder().Unmarshal($"dec = 0.{new string('0', Limit)}1", new BigNumbers()));
        Assert.Contains("pxf.Decimal scale 4097 exceeds MaxNumericLiteralDigits=4096", e.Message);
    }

    [Fact]
    public void Generated_BigFloat_AtTheBound_Accepted() =>
        new Decoder().Unmarshal($"big_float = {Digits(Limit - 2)}.77", new BigNumbers());

    [Fact]
    public void Generated_BigFloat_OnePast_Rejected() => AssertDigitsRejected(
        () => new Decoder().Unmarshal($"big_float = {Digits(Limit - 2)}.777", new BigNumbers()), Limit + 1);

    [Fact]
    public void Poco_BigInt_AtTheBound_DecodesToThatValue()
    {
        var got = new Poco();
        new Decoder().Unmarshal($"big_int = {Digits(Limit)}", got);
        Assert.Equal(BigInteger.Parse(Digits(Limit)), got.BigInt);
    }

    [Fact]
    public void Poco_BigInt_OnePast_Rejected() => AssertDigitsRejected(
        () => new Decoder().Unmarshal($"big_int = {Digits(Limit + 1)}", new Poco()), Limit + 1);

    [Fact]
    public void Poco_BigInt_SignIsNotADigit()
    {
        var got = new Poco();
        new Decoder().Unmarshal($"big_int = -{Digits(Limit)}", got);
        Assert.Equal(-BigInteger.Parse(Digits(Limit)), got.BigInt);
    }

    [Fact]
    public void Poco_BigInt_LeadingZerosAreNotDigits()
    {
        var got = new Poco();
        new Decoder().Unmarshal($"big_int = {new string('0', 5000)}{Digits(Limit)}", got);
        Assert.Equal(BigInteger.Parse(Digits(Limit)), got.BigInt);

        AssertDigitsRejected(
            () => new Decoder().Unmarshal($"big_int = {new string('0', 5000)}{Digits(Limit + 1)}", new Poco()),
            Limit + 1);
    }

    [Fact]
    public void Poco_Decimal_AtTheBound_Accepted()
    {
        var got = new Poco();
        new Decoder().Unmarshal($"dec = {Digits(Limit - 2)}.77", got);
        Assert.Equal(2, got.Dec!.Scale);
    }

    [Fact]
    public void Poco_Decimal_OnePast_Rejected() => AssertDigitsRejected(
        () => new Decoder().Unmarshal($"dec = {Digits(Limit - 2)}.777", new Poco()), Limit + 1);

    [Fact]
    public void Poco_Decimal_ScaleBound()
    {
        var got = new Poco();
        new Decoder().Unmarshal($"dec = 0.{new string('0', Limit - 1)}1", got);
        Assert.Equal(Limit, got.Dec!.Scale);

        var e = Assert.Throws<PxfException>(() =>
            new Decoder().Unmarshal($"dec = 0.{new string('0', Limit)}1", new Poco()));
        Assert.Contains("pxf.Decimal scale 4097 exceeds MaxNumericLiteralDigits=4096", e.Message);
    }

    [Fact]
    public void Poco_BigFloat_AtTheBound_Accepted() =>
        new Decoder().Unmarshal($"big_float = {Digits(Limit - 2)}.77", new Poco());

    [Fact]
    public void Poco_BigFloat_OnePast_Rejected() => AssertDigitsRejected(
        () => new Decoder().Unmarshal($"big_float = {Digits(Limit - 2)}.777", new Poco()), Limit + 1);

    // A (pxf.default) string never passes the lexer.
    [Theory]
    [InlineData("big_int")]
    [InlineData("dec")]
    [InlineData("big_float")]
    public void Default_BothSidesOfTheBound(string field)
    {
        var fd = BigNumbers.Descriptor.FindFieldByName(field);
        string Literal(int digits) => field == "big_int" ? Digits(digits) : Digits(digits - 1) + ".7";

        Decoder.ApplyMessageDefault(new BigNumbers(), fd, Literal(Limit));

        AssertDigitsRejected(
            () => Decoder.ApplyMessageDefault(new BigNumbers(), fd, Literal(Limit + 1)), Limit + 1);
    }

    [Fact]
    public void Default_DecimalScaleBound()
    {
        var fd = BigNumbers.Descriptor.FindFieldByName("dec");
        Decoder.ApplyMessageDefault(new BigNumbers(), fd, $"0.{new string('0', Limit - 1)}1");

        var e = Assert.Throws<PxfException>(() =>
            Decoder.ApplyMessageDefault(new BigNumbers(), fd, $"0.{new string('0', Limit)}1"));
        Assert.Contains("pxf.Decimal scale 4097", e.Message);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("007", 1)]
    [InlineData("-1.50", 3)]
    [InlineData("0.0012", 2)]
    [InlineData("12e5000", 2)]
    [InlineData("1.5E-9", 2)]
    [InlineData("12p13603", 2)]
    public void CountDigits_CountsSignificantMantissaDigits(string literal, int want) =>
        Assert.Equal(want, Decoder.CountDigits(literal));
}
