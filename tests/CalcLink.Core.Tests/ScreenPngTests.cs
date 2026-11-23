using System.Buffers.Binary;
using System.IO.Compression;
using CalcLink.Core;
using Xunit;

namespace CalcLink.Core.Tests;

public class ScreenPngTests
{
    [Fact]
    public void Crc_matches_the_PNG_reference_value() =>
        // The IEND chunk's CRC is fixed: AE 42 60 82.
        Assert.Equal(0xAE426082u, ScreenPng.Crc(0xFFFFFFFF, "IEND"u8) ^ 0xFFFFFFFF);

    [Fact]
    public void Encode_writes_a_valid_RGB_png_with_the_right_pixels()
    {
        // 2×1: a black pixel and a white one (BGRA).
        var image = new ScreenImage(2, 1, [0, 0, 0, 255, 255, 255, 255, 255]);

        byte[] png = ScreenPng.Encode(image, scale: 2);

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));  // width ×2
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));  // height ×2
        Assert.Equal(2, png[25]);                                               // RGB

        // Inflate IDAT: two rows of [filter 0, B B W W] in RGB.
        int idat = IndexOf(png, "IDAT"u8);
        int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idat - 4));
        using var z = new ZLibStream(new MemoryStream(png, idat + 4, len), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        byte[] row = [0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 255, 255];
        Assert.Equal([.. row, .. row], raw.ToArray());
        Assert.EndsWith("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);
}
