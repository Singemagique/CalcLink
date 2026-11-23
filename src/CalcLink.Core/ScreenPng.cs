using System.Buffers.Binary;
using System.IO.Compression;

namespace CalcLink.Core;

/// <summary>Encodes a <see cref="ScreenImage"/> as PNG (8-bit RGB), without any imaging library.</summary>
public static class ScreenPng
{
    private static readonly uint[] s_crcTable = BuildCrcTable();

    /// <param name="image">The captured screen.</param>
    /// <param name="scale">Each calculator pixel becomes scale × scale PNG pixels (1–16).</param>
    public static byte[] Encode(ScreenImage image, int scale = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        scale = Math.Clamp(scale, 1, 16);
        int w = image.Width * scale, h = image.Height * scale;

        // Raw scanlines: filter byte 0, then RGB.
        byte[] raw = new byte[h * (1 + w * 3)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            int sy = y / scale;
            for (int x = 0; x < w; x++)
            {
                int s = (sy * image.Width + x / scale) * 4; // BGRA
                raw[o++] = image.Bgra[s + 2];
                raw[o++] = image.Bgra[s + 1];
                raw[o++] = image.Bgra[s];
            }
        }

        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // colour type: RGB
        WriteChunk(ms, "IHDR"u8, ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
            {
                zs.Write(raw);
            }
            WriteChunk(ms, "IDAT"u8, z.ToArray());
        }
        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    internal static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            t[n] = c;
        }
        return t;
    }
}
