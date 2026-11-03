// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf.Tests.Bench;

namespace Protowire.Pxf.Tests;

// Draft -01 § Mandatory Limits (#32): a lowered limit refuses what the default
// accepts, and the default refuses an input one byte past it.
public class LimitTests
{
    private static void Decode(string doc, PxfLimits limits) =>
        new Decoder { Limits = limits }.Unmarshal(doc, new Config());

    [Fact]
    public void MaxMessageSize()
    {
        const string doc = "hostname = \"h\"\n";
        Decode(doc, PxfLimits.Default);
        var e = Assert.Throws<PxfException>(() => Decode(doc, new PxfLimits { MaxMessageSize = doc.Length - 1 }));
        Assert.Contains("exceeds MaxMessageSize", e.Message);
        Assert.Contains("exceeds MaxMessageSize", Assert.Throws<PxfException>(
            () => Parser.Parse(doc, new PxfLimits { MaxMessageSize = doc.Length - 1 })).Message);
    }

    [Fact]
    public void MaxMessageSize_DefaultRefusesOnePast()
    {
        string doc = new('#', (64 << 20) + 1);
        Assert.Contains("MaxMessageSize=67108864", Assert.Throws<PxfException>(() => Decode(doc, PxfLimits.Default)).Message);
    }

    [Fact]
    public void MaxMessageSize_CountsBytes_NotChars()
    {
        const string doc = "hostname = \"éééé\"\n";
        var e = Assert.Throws<PxfException>(() => Decode(doc, new PxfLimits { MaxMessageSize = doc.Length }));
        Assert.Contains($"input of {doc.Length + 4} bytes", e.Message);
    }

    [Fact]
    public void MaxBytesLiteralLength()
    {
        var limits = new PxfLimits { MaxBytesLiteralLength = 3 };
        const string short3 = "x = b\"AAAA\"\n";
        const string long6 = "x = b\"AAAAAAAA\"\n";
        Parser.Parse(short3, limits);
        Parser.Parse(long6);
        Assert.Contains("MaxBytesLiteralLength=3", Assert.Throws<PxfException>(() => Parser.Parse(long6, limits)).Message);
    }

    [Fact]
    public void MaxRepeatedCount_List()
    {
        const string doc = "tags = [\"a\", \"b\", \"c\"]\n";
        Decode(doc, new PxfLimits { MaxRepeatedCount = 3 });
        Assert.Contains("MaxRepeatedCount=2", Assert.Throws<PxfException>(
            () => Decode(doc, new PxfLimits { MaxRepeatedCount = 2 })).Message);
    }

    [Fact]
    public void MaxRepeatedCount_Map()
    {
        const string doc = "labels = { a: \"1\" b: \"2\" }\n";
        Decode(doc, new PxfLimits { MaxRepeatedCount = 2 });
        Assert.Contains("MaxRepeatedCount=1", Assert.Throws<PxfException>(
            () => Decode(doc, new PxfLimits { MaxRepeatedCount = 1 })).Message);
    }

    [Fact]
    public void MaxNestingDepth()
    {
        const string doc = "tls { verify = true }\n";
        Decode(doc, new PxfLimits { MaxNestingDepth = 1 });
        Assert.Contains("MaxNestingDepth=0", Assert.Throws<PxfException>(
            () => new Decoder { Limits = new PxfLimits { MaxNestingDepth = 0 } }.Unmarshal(doc, new Config())).Message);
    }
}
