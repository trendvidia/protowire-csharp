// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Conformance.Schemas.Text;
using Protowire.Pxf;

namespace Protowire.Conformance;

// PXF `nan` decodes to the NaN Go's strconv.ParseFloat gives: positive and
// quiet. Its bits reach the PB wire, where the write gate compares them with
// protowire-go's (testdata/write/golden/Scalars.float-nan.binpb, #82).
public class NaNBitsTests
{
    [Fact]
    public void NanIsGosQuietNaN()
    {
        var msg = new Scalars();
        new Decoder().Unmarshal("f_double = nan\nf_float = nan\n", msg);
        Assert.Equal(0x7FF8000000000000, BitConverter.DoubleToInt64Bits(msg.FDouble));
        Assert.Equal(0x7FC00000, BitConverter.SingleToInt32Bits(msg.FFloat));
        Assert.Equal("09000000000000f87f150000c07f", Convert.ToHexString(Google.Protobuf.MessageExtensions.ToByteArray(msg)).ToLowerInvariant());
    }
}
