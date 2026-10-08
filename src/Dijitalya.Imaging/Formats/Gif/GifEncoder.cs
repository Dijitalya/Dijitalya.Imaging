namespace Dijitalya.Imaging.Formats.Gif;

/// <summary>
/// Encodes a still GIF (89a): one image of at most 256 colours with, when some pixels are
/// transparent, a graphic control extension naming the transparent entry. No other extension and
/// no comment is written. See <see cref="GifQuantizer"/> for how the colours are chosen.
/// </summary>
internal static class GifEncoder
{
    private const int MaxDimension = ushort.MaxValue;

    public static byte[] Encode(RasterImage image)
    {
        if (image.Width > MaxDimension || image.Height > MaxDimension)
            throw new NotSupportedException($"GIF pictures can be at most {MaxDimension} pixels wide and high.");

        var quantized = GifQuantizer.Quantize(image);

        // The colour table holds a power of two entries, at least two.
        var tableBits = 1;

        while ((1 << tableBits) < quantized.ColorCount)
            tableBits++;

        using var output = new MemoryStream();
        output.Write("GIF89a"u8);

        // Logical screen: the picture's size, a global colour table of 2^tableBits entries, 8 bits per channel.
        WriteUInt16(output, image.Width);
        WriteUInt16(output, image.Height);
        output.WriteByte((byte)(0x80 | (7 << 4) | (tableBits - 1)));
        output.WriteByte(0);
        output.WriteByte(0);

        var table = new byte[3 << tableBits];
        quantized.Palette.CopyTo(table, 0);
        output.Write(table);

        if (quantized.TransparentIndex >= 0)
        {
            // Graphic control extension: no disposal, no delay, the transparent entry.
            output.Write([0x21, 0xF9, 4, 1, 0, 0, (byte)quantized.TransparentIndex, 0]);
        }

        // Image descriptor: the whole screen, no local table, not interlaced.
        output.WriteByte(0x2C);
        WriteUInt16(output, 0);
        WriteUInt16(output, 0);
        WriteUInt16(output, image.Width);
        WriteUInt16(output, image.Height);
        output.WriteByte(0);

        GifLzwEncoder.Encode(quantized.Indexes, Math.Max(2, tableBits), output);
        output.WriteByte(0x3B);

        return output.ToArray();
    }

    private static void WriteUInt16(Stream output, int value)
    {
        output.WriteByte((byte)value);
        output.WriteByte((byte)(value >> 8));
    }
}
