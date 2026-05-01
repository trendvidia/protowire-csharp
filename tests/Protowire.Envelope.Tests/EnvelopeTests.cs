using Protowire.Envelopes;
using Protowire.Pb;

namespace Protowire.Envelope.Tests;

public class EnvelopeTests
{
    [Fact]
    public void TestBinaryRoundTrip_OK()
    {
        var orig = Protowire.Envelopes.Envelope.OK(200, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var data = Pb.Pb.Marshal(orig);
        var got = new Protowire.Envelopes.Envelope();
        Pb.Pb.Unmarshal(data, got);

        Assert.Equal(orig.Status, got.Status);
        Assert.Equal(orig.Data, got.Data);
        Assert.True(got.IsOK);
    }

    [Fact]
    public void TestBinaryRoundTrip_TransportErr()
    {
        var orig = Protowire.Envelopes.Envelope.TransportErr("connection refused");

        var data = Pb.Pb.Marshal(orig);
        var got = new Protowire.Envelopes.Envelope();
        Pb.Pb.Unmarshal(data, got);

        Assert.Equal(orig.TransportError, got.TransportError);
        Assert.True(got.IsTransportError);
    }

    [Fact]
    public void TestBinaryRoundTrip_AppError_WithFieldsAndMetadata()
    {
        var ae = new AppError { Code = "INSUFFICIENT_FUNDS", Message = "balance too low", Args = new List<string> { "$3.50", "$10.00" } }
            .WithField("amount", "MIN_VALUE", "below minimum", "10.00")
            .WithField("currency", "INVALID", "unsupported currency")
            .WithMeta("request_id", "req-123")
            .WithMeta("retry_after", "30");
        var orig = new Protowire.Envelopes.Envelope { Status = 402, Error = ae };

        var data = Pb.Pb.Marshal(orig);
        var got = new Protowire.Envelopes.Envelope();
        Pb.Pb.Unmarshal(data, got);

        Assert.Equal(orig.Status, got.Status);
        Assert.Equal(orig.Error?.Code, got.Error?.Code);
        Assert.Equal(orig.Error?.Message, got.Error?.Message);
        Assert.Equal(orig.Error?.Args, got.Error?.Args);
        Assert.Equal(orig.Error?.Details?.Count, got.Error?.Details?.Count);
        Assert.Equal(orig.Error?.Metadata?["request_id"], got.Error?.Metadata?["request_id"]);
        Assert.True(got.IsAppError);
    }
}
