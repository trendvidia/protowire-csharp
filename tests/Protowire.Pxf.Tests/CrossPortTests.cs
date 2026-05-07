// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf;
using Protowire.Pxf.Tests.Bench;
using Google.Protobuf;

namespace Protowire.Pxf.Tests;

public class CrossPortTests
{
    [Fact]
    public void TestCrossPort_PxfToBinary()
    {
        string pxfText = File.ReadAllText("testdata/bench-test.pxf");
        
        var decoder = new Decoder();
        var config = new Config();
        decoder.Unmarshal(pxfText, config);

        Assert.Equal("web-01.prod.example.com", config.Hostname);
        Assert.Equal(8443, config.Port);
        Assert.True(config.Enabled);
        Assert.Equal(0.85, config.Weight);
        Assert.Equal(Status.Serving, config.Status);
        Assert.Contains("production", config.Tags);
        Assert.Equal("/etc/ssl/certs/server.pem", config.Tls.CertFile);
        Assert.Equal("production", config.Labels["env"]);
        Assert.Equal(3, config.Endpoints.Count);
        Assert.Equal("/api/v1/users", config.Endpoints[0].Path);
        Assert.Equal(30, config.Timeout.Seconds);
    }

    [Fact]
    public void TestCrossPort_RoundTrip()
    {
        var config = new Config
        {
            Hostname = "test-host",
            Port = 1234,
            Enabled = true,
            Status = Status.Serving,
            Tls = new TLS { CertFile = "cert.pem", Verify = true },
            Timeout = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromSeconds(15))
        };
        config.Tags.Add("t1");
        config.Labels.Add("k1", "v1");
        config.Endpoints.Add(new Endpoint { Path = "/p1", Method = "GET" });

        var encoder = new Encoder();
        string actualPxf = encoder.Marshal(config);

        var decoder = new Decoder();
        var got = new Config();
        decoder.Unmarshal(actualPxf, got);

        Assert.Equal(config, got);
    }
}
