using Protowire.Sbe;
using Protowire.Sbe.Tests.Bench;

namespace Protowire.Sbe.Tests;

public class SbeTests
{
    [Fact]
    public void TestSbeRoundTrip_Order()
    {
        var codec = new Codec(SbeBenchReflection.Descriptor);

        var order = new Order
        {
            OrderId = 123,
            Symbol = "AAPL",
            Price = 15000,
            Quantity = 100,
            Side = Side.Buy,
            Active = true,
            Weight = 1.23,
            Score = 4.56f
        };
        order.Fills.Add(new Order.Types.Fill { FillId = 1, FillPrice = 14900, FillQty = 50 });
        order.Fills.Add(new Order.Types.Fill { FillId = 2, FillPrice = 15100, FillQty = 50 });

        var data = codec.Marshal(order);
        var got = new Order();
        codec.Unmarshal(data, got);

        Assert.Equal(order.OrderId, got.OrderId);
        Assert.Equal("AAPL", got.Symbol); // AAPL followed by nulls in SBE, but trimmed in unmarshal
        Assert.Equal(order.Price, got.Price);
        Assert.Equal(order.Quantity, got.Quantity);
        Assert.Equal(order.Side, got.Side);
        Assert.Equal(order.Active, got.Active);
        Assert.Equal(order.Weight, got.Weight);
        Assert.Equal(order.Score, got.Score);
        Assert.Equal(order.Fills.Count, got.Fills.Count);
        Assert.Equal(order.Fills[0].FillId, got.Fills[0].FillId);
    }

    [Fact]
    public void TestSbeView_Order()
    {
        var codec = new Codec(SbeBenchReflection.Descriptor);

        var order = new Order
        {
            OrderId = 123,
            Symbol = "AAPL",
            Price = 15000,
            Quantity = 100,
            Side = Side.Buy,
            Active = true,
            Weight = 1.23,
            Score = 4.56f
        };
        order.Fills.Add(new Order.Types.Fill { FillId = 1, FillPrice = 14900, FillQty = 50 });

        var data = codec.Marshal(order);
        var view = codec.CreateView(data);

        Assert.Equal(123UL, view.Uint("order_id"));
        Assert.Equal("AAPL", view.String("symbol"));
        Assert.Equal(15000L, view.Int("price"));
        Assert.Equal(100U, view.Uint("quantity"));
        Assert.Equal((int)Side.Buy, view.Enum("side"));
        Assert.True(view.Bool("active"));
        Assert.Equal(1.23, view.Float("weight"));
        Assert.Equal(4.56f, (float)view.Float("score"));

        var fills = view.Group("fills");
        Assert.Equal(1, fills.Count);
        var f1 = fills.Entry(0);
        Assert.Equal(1UL, f1.Uint("fill_id"));
        Assert.Equal(14900L, f1.Int("fill_price"));
        Assert.Equal(50U, f1.Uint("fill_qty"));
    }

    [Fact]
    public void TestSbeExtra_RepeatedAndMap()
    {
        var codec = new Codec(SbeExtraReflection.Descriptor);
        var extra = new SbeExtra
        {
            Id = 1,
            Tags = { 10, 20, 30 },
            Metadata = { { "k1", "v1" }, { "k2", "v2" } }
        };

        var data = codec.Marshal(extra);
        var got = new SbeExtra();
        codec.Unmarshal(data, got);

        Assert.Equal(extra.Id, got.Id);
        Assert.Equal(extra.Tags, got.Tags);
        Assert.Equal(extra.Metadata, got.Metadata);
    }

    [Fact]
    public void TestSbeOneof()
    {
        var codec = new Codec(SbeOneofReflection.Descriptor);
        var msg = new SbeOneof { Id = 1, Name = "test" };

        var data = codec.Marshal(msg);
        var got = new SbeOneof();
        codec.Unmarshal(data, got);

        Assert.Equal(1UL, got.Id);
        Assert.Equal("test", got.Name);
        Assert.Equal(SbeOneof.ChoiceOneofCase.Name, got.ChoiceCase);

        msg = new SbeOneof { Id = 2, Code = 123 };
        data = codec.Marshal(msg);
        got = new SbeOneof();
        codec.Unmarshal(data, got);

        Assert.Equal(2UL, got.Id);
        Assert.Equal(123, got.Code);
        Assert.Equal(SbeOneof.ChoiceOneofCase.Code, got.ChoiceCase);
    }
}
