// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Protowire.Pxf;
using Protowire.Sbe.Tests.Bench;

namespace Protowire.Pxf.Tests;

/// <summary>
/// uint32 / uint64 fields on the reflection decode path (#26): the
/// generated <c>bench.v1.Order</c> carries both, and protowire's shared
/// <c>testdata/sbe-bench.pxf</c> was rejected at <c>order_id</c> with
/// "unsupported type System.UInt64" before the branches existed.
/// </summary>
public class UnsignedTests
{
    [Fact]
    public void Uint64AndUint32FieldsDecode()
    {
        const string input = """
            order_id = 18446744073709551615
            quantity = 4294967295
            fills = [
              { fill_qty = 7 fill_id = 5001 }
            ]
            """;
        var got = new Order();
        new Decoder().Unmarshal(input, got);
        Assert.Equal(ulong.MaxValue, got.OrderId);
        Assert.Equal(uint.MaxValue, got.Quantity);
        Assert.Equal(7u, got.Fills[0].FillQty);
        Assert.Equal(5001UL, got.Fills[0].FillId);
    }

    [Fact]
    public void NegativeIsRejectedForUnsigned()
    {
        Assert.ThrowsAny<Exception>(() => new Decoder().Unmarshal("order_id = -1", new Order()));
    }

    [Fact]
    public void UnsignedRoundTripThroughTheEncoder()
    {
        var orig = new Order { OrderId = ulong.MaxValue, Quantity = 3 };
        var got = new Order();
        new Decoder().Unmarshal(new Encoder().Marshal(orig), got);
        Assert.Equal(orig.OrderId, got.OrderId);
        Assert.Equal(orig.Quantity, got.Quantity);
    }
}
