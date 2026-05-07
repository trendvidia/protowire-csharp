// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf;
using Protowire.Envelopes;

namespace Protowire.Pxf.Tests;

public class PxfTests
{
    [Fact]
    public void TestPxfRoundTrip_Envelope()
    {
        var ae = new AppError { Code = "TEST_ERROR", Message = "Something went wrong" }
            .WithField("f1", "ERR1", "msg1")
            .WithMeta("k1", "v1");
        var orig = new Protowire.Envelopes.Envelope { Status = 200, Error = ae, Data = new byte[] { 1, 2, 3 } };

        var encoder = new Encoder();
        var pxfText = encoder.Marshal(orig);

        var decoder = new Decoder();
        var got = new Protowire.Envelopes.Envelope();
        decoder.Unmarshal(pxfText, got);

        Assert.Equal(orig.Status, got.Status);
        Assert.Equal(orig.Error?.Code, got.Error?.Code);
        Assert.Equal(orig.Error?.Message, got.Error?.Message);
        Assert.Equal(orig.Error?.Details?.Count, got.Error?.Details?.Count);
        Assert.Equal(orig.Error?.Metadata?["k1"], got.Error?.Metadata?["k1"]);
        Assert.Equal(orig.Data, got.Data);
    }

    [Fact]
    public void TestPxf_ManualText()
    {
        string input = @"
status = 404
transport_error = ""not found""
";
        var decoder = new Decoder();
        var got = new Protowire.Envelopes.Envelope();
        decoder.Unmarshal(input, got);

        Assert.Equal(404, got.Status);
        Assert.Equal("not found", got.TransportError);
    }

    public class RequiredMsg
    {
        [PxfRequired] public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    [Fact]
    public void TestPxf_RequiredField()
    {
        string input = "age = 25";
        var decoder = new Decoder();
        var got = new RequiredMsg();
        
        // This should currently NOT throw, but we want it TO throw after implementation.
        // For now, let's just assert that it doesn't throw if we are in "pre-implementation" state,
        // or just write the expected final test.
        Assert.Throws<PxfException>(() => decoder.Unmarshal(input, got));
    }

    public class BigNumMsg
    {
        public System.Numerics.BigInteger BigInt { get; set; }
        public Protowire.Pb.Decimal Dec { get; set; } = null!;
        public Protowire.Pb.BigFloat Float { get; set; } = null!;
    }

    [Fact]
    public void TestPxf_BigNumbers()
    {
        string input = @"
big_int = 123456789012345678901234567890
dec = -123.45
float = 0.00123
";
        var decoder = new Decoder();
        var got = new BigNumMsg();
        decoder.Unmarshal(input, got);

        Assert.Equal(System.Numerics.BigInteger.Parse("123456789012345678901234567890"), got.BigInt);
        Assert.Equal(System.Numerics.BigInteger.Parse("12345"), got.Dec.Unscaled);
        Assert.Equal(2, got.Dec.Scale);
        Assert.True(got.Dec.Negative);
        Assert.Equal(System.Numerics.BigInteger.Parse("123"), got.Float.Mantissa);
        Assert.Equal(-5, got.Float.Exponent);
    }

    public class DurationMsg
    {
        public TimeSpan Timeout { get; set; }
    }

    [Fact]
    public void TestPxf_Duration()
    {
        string input = "timeout = 1h30m";
        var decoder = new Decoder();
        var got = new DurationMsg();
        decoder.Unmarshal(input, got);

        Assert.Equal(TimeSpan.FromMinutes(90), got.Timeout);
    }

    public class AnyMsg
    {
        public Google.Protobuf.WellKnownTypes.Any Details { get; set; } = null!;
    }

    [Fact]
    public void TestPxf_Any()
    {
        var endpoint = new Protowire.Pxf.Tests.Bench.Endpoint { Path = "/api", Method = "GET" };
        var details = Google.Protobuf.WellKnownTypes.Any.Pack(endpoint);
        var msg = new AnyMsg { Details = details };

        var registry = Google.Protobuf.Reflection.TypeRegistry.FromFiles(Protowire.Pxf.Tests.Bench.BenchTestReflection.Descriptor);
        var encoder = new Encoder { Registry = registry };
        var pxfText = encoder.Marshal(msg);

        // The output should contain @type and the fields of Endpoint
        Assert.Contains("@type type.googleapis.com/bench.v1.Endpoint", pxfText);
        Assert.Contains("path = \"/api\"", pxfText);

        var decoder = new Decoder { Registry = registry };
        var got = new AnyMsg();
        decoder.Unmarshal(pxfText, got);

        Assert.NotNull(got.Details);
        var unpacked = got.Details.Unpack<Protowire.Pxf.Tests.Bench.Endpoint>();
        Assert.Equal("/api", unpacked.Path);
    }
}
