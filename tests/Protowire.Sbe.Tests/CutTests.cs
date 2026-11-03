// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Text;
using Google.Protobuf;
using Protowire.Sbe.Tests.Utf8;

namespace Protowire.Sbe.Tests;

// Draft -01 § SBE Annotations (trendvidia/protowire#495, #79 here): an
// overlong string is cut at the last whole UTF-8 character at or before the
// limit and NUL-padded, so a strict decoder still reads it; a bytes field is
// cut at the limit. Ported from protowire-go's
// TestOverlongStringCutOnCharacterBoundary (encoding/sbe/utf8_test.go).
public class CutTests
{
    private static readonly Codec Codec = new(SbeUtf8Reflection.Descriptor);

    // Header 8; block: text 0-8, raw 8-16, code.text 16-24; group header 4;
    // the one row's tag 28-36.
    private const int Block = 8;

    private static readonly (string Name, int At)[] Sites =
        [("root field", Block), ("composite field", Block + 16), ("group field", Block + 28)];

    private static byte[] Marshal(string text)
    {
        var msg = new Cut
        {
            Text = text,
            Raw = ByteString.CopyFromUtf8(text),
            Code = new CutCode { Text = text },
        };
        msg.Rows.Add(new CutRow { Tag = text });
        return Codec.Marshal(msg);
    }

    [Theory]
    [InlineData("2-byte character across the limit", "ABCDEFGé", "ABCDEFG")]
    [InlineData("3-byte character across the limit", "ABCDEF€", "ABCDEF")]
    [InlineData("3-byte character, one byte over", "ABCDE€x", "ABCDE€")]
    [InlineData("4-byte character across the limit", "ABCDE😀", "ABCDE")]
    [InlineData("limit between two 4-byte characters", "😀😀😀", "😀😀")]
    [InlineData("cut already on a boundary", "ABCDEFGHI", "ABCDEFGH")]
    [InlineData("fits exactly", "ABCDEFé", "ABCDEFé")]
    public void OverlongStringIsCutOnACharacterBoundary(string name, string input, string text)
    {
        _ = name;
        var frame = Marshal(input);
        var want = new byte[8];
        Encoding.UTF8.GetBytes(text, want);
        foreach (var (site, at) in Sites)
        {
            Assert.True(want.AsSpan().SequenceEqual(frame.AsSpan(at, 8)),
                $"{site}: want {Convert.ToHexString(want)}, got {Convert.ToHexString(frame, at, 8)}");
        }
        Assert.Equal(Encoding.UTF8.GetBytes(input)[..8], frame[(Block + 8)..(Block + 16)]);

        var got = new Cut();
        Codec.Unmarshal(frame, got);
        Assert.Equal(text, got.Text);
        Assert.Equal(text, got.Code.Text);
        Assert.Equal(text, got.Rows[0].Tag);
    }

    [Fact]
    public void TheIssuesCaseByteForByte()
    {
        var frame = Marshal("ABCDEFGé");
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x00 }, frame[Block..(Block + 8)]);
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0xC3 }, frame[(Block + 8)..(Block + 16)]);
    }

    [Fact]
    public void ALimitShorterThanTheFirstCharacterLeavesTheFieldEmpty()
    {
        var frame = Codec.Marshal(new CutTiny { Text = "😀" });
        Assert.Equal(new byte[] { 0, 0, 0 }, frame[Block..(Block + 3)]);
        var got = new CutTiny();
        Codec.Unmarshal(frame, got);
        Assert.Equal("", got.Text);
    }
}
