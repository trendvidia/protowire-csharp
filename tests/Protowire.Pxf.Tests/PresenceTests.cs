// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Protowire.Pxf;
using Protowire.Pxf.Tests.Presence;

namespace Protowire.Pxf.Tests;

/// <summary>
/// Tests for <see cref="Decoder.UnmarshalFull"/>: per-field presence tracking,
/// <c>(pxf.required)</c> validation, <c>(pxf.default)</c> application
/// (including WKT messages), and the <c>_null</c> FieldMask round-trip.
/// </summary>
public class PresenceTests
{
    [Fact]
    public void Result_TracksSetNullAbsent()
    {
        string input = """
            name = "alice"
            role = null
            """;
        var decoder = new Decoder();
        var got = new User();
        var result = decoder.UnmarshalFull(input, got);

        Assert.True(result.IsSet("name"));
        Assert.False(result.IsNull("name"));
        Assert.False(result.IsAbsent("name"));

        Assert.True(result.IsNull("role"));
        Assert.False(result.IsSet("role"));
        Assert.False(result.IsAbsent("role"));

        Assert.True(result.IsAbsent("admin"));

        Assert.Contains("role", result.NullFields());
        Assert.Contains("name", result.SetFields());
    }

    [Fact]
    public void Required_AbsentField_Throws()
    {
        // role and name are both required-ish (name is required); only role given.
        string input = "role = \"admin\"";
        var decoder = new Decoder();
        var got = new User();
        var ex = Assert.Throws<PxfException>(() => decoder.UnmarshalFull(input, got));
        Assert.Contains("required", ex.Message);
        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public void Required_PresentField_Succeeds()
    {
        string input = "name = \"bob\"";
        var decoder = new Decoder();
        var got = new User();
        var result = decoder.UnmarshalFull(input, got);
        Assert.Equal("bob", got.Name);
        Assert.True(result.IsSet("name"));
    }

    [Fact]
    public void Default_AppliedToAbsent_NotToNull()
    {
        // role absent → "viewer"; priority null → stays 0 + remembered as null.
        string input = """
            name = "alice"
            priority = null
            """;
        var decoder = new Decoder();
        var got = new User();
        var result = decoder.UnmarshalFull(input, got);

        Assert.Equal("viewer", got.Role);     // (pxf.default) = "viewer" applied
        Assert.Equal(0, got.Priority);        // null → no default applied
        Assert.True(result.IsNull("priority"));
        Assert.True(result.IsAbsent("role")); // absent before defaults; no markPresent for default
    }

    [Fact]
    public void Defaults_ScalarsAndWkts()
    {
        var decoder = new Decoder();
        var got = new Defaults();
        decoder.UnmarshalFull("", got);

        Assert.Equal("hello", got.S);
        Assert.True(got.B);
        Assert.Equal(-7, got.I32);
        Assert.Equal(9_000_000_000L, got.I64);
        Assert.Equal(42u, got.U32);
        Assert.Equal(18_000_000_000UL, got.U64);
        Assert.Equal(0.5f, got.F32);
        Assert.Equal(2.5, got.F64);
        Assert.Equal(new byte[] { 1, 2, 3 }, got.By.ToByteArray());
        Assert.Equal(Color.Red, got.Color);

        Assert.NotNull(got.Ts);
        Assert.Equal(1704164645L, got.Ts.Seconds); // 2024-01-02T03:04:05Z

        Assert.NotNull(got.Dur);
        // 1h30m == 5400s
        Assert.Equal(5400L, got.Dur.Seconds);

        Assert.NotNull(got.Wrapped);
        Assert.Equal(99, got.Wrapped.Value);
    }

    [Fact]
    public void NullField_PopulatesNullMask()
    {
        string input = """
            name = "alice"
            role = null
            admin = null
            """;
        var decoder = new Decoder();
        var got = new User();
        decoder.UnmarshalFull(input, got);

        Assert.NotNull(got.Null);
        Assert.Equal(2, got.Null.Paths.Count);
        Assert.Contains("role", got.Null.Paths);
        Assert.Contains("admin", got.Null.Paths);
    }

    [Fact]
    public void NullMaskField_NotValidatedAsRequired()
    {
        // The reserved _null FieldMask field must be skipped by PostDecode
        // even though it lives at the top of the message.
        string input = "name = \"x\"";
        var decoder = new Decoder();
        var got = new User();
        // No throw → _null was skipped (it has no annotations anyway, but this
        // also guards against accidentally recursing into it).
        var result = decoder.UnmarshalFull(input, got);
        Assert.True(result.IsAbsent("_null"));
    }

    [Fact]
    public void Required_NestedField_Throws()
    {
        // inner is present (block) but inner.label is required and absent.
        string input = "inner { }";
        var decoder = new Decoder();
        var got = new Nested();
        var ex = Assert.Throws<PxfException>(() => decoder.UnmarshalFull(input, got));
        Assert.Contains("inner.label", ex.Message);
    }

    [Fact]
    public void Required_NestedField_Absent_DoesNotRecurse()
    {
        // inner is absent → PostDecode does not recurse into it,
        // so inner.label's required check does not fire.
        string input = "";
        var decoder = new Decoder();
        var got = new Nested();
        decoder.UnmarshalFull(input, got);
        Assert.Null(got.Inner);
    }
}
