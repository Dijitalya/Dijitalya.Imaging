using System.Buffers.Binary;
using System.Numerics;

namespace Dijitalya.Imaging.Formats.Bmp;

/// <summary>
/// Decodes Windows and OS/2 bitmaps: the core, info, V2 to V5 and OS/2 2.x headers; 1, 2, 4 and 8-bit
/// palettes, 16, 24 and 32-bit pixels; uncompressed, RLE8, RLE4 and bit-field data; bottom-up and
/// top-down rows. Pixels an RLE bitmap skips stay transparent. A 32-bit bitmap without an alpha mask
/// is opaque unless its fourth bytes actually carry values.
/// </summary>
internal static class BmpDecoder
{
    private const int Rgb = 0;
    private const int Rle8 = 1;
    private const int Rle4 = 2;
    private const int BitFields = 3;
    private const int AlphaBitFields = 6;

    public static DecodedImage Decode(byte[] data)
    {
        var span = data.AsSpan();
        var pixelOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(span[10..]);
        var headerSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(span[14..]), int.MaxValue);

        if (14L + headerSize > data.Length)
            throw new InvalidImageContentException("The BMP header is cut off.");

        long width, height;
        int bitsPerPixel, compression = Rgb, colorsUsed = 0, paletteEntrySize = 4;
        uint redMask = 0, greenMask = 0, blueMask = 0, alphaMask = 0;
        var paletteStart = 14 + headerSize;

        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(span[18..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(span[20..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(span[24..]);
            paletteEntrySize = 3;
        }
        else
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(span[18..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(span[22..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(span[28..]);

            if (headerSize >= 20)
                compression = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(span[30..]), int.MaxValue);

            if (headerSize >= 36)
                colorsUsed = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(span[46..]), 256);

            // OS/2 2.x reuses compression values 3 and 4 for Huffman 1D and RLE24, which are not supported.
            if ((headerSize is 16 or 64) && compression is BitFields or 4)
                throw new InvalidImageContentException("This OS/2 bitmap compression is not supported.");

            if (compression is BitFields or AlphaBitFields)
            {
                var maskCount = compression == AlphaBitFields ? 4 : 3;

                if (headerSize == 40)
                    paletteStart += maskCount * 4;

                if (54 + (maskCount * 4) > data.Length)
                    throw new InvalidImageContentException("The BMP bit masks are cut off.");

                redMask = BinaryPrimitives.ReadUInt32LittleEndian(span[54..]);
                greenMask = BinaryPrimitives.ReadUInt32LittleEndian(span[58..]);
                blueMask = BinaryPrimitives.ReadUInt32LittleEndian(span[62..]);

                if (maskCount == 4 || headerSize >= 56)
                    alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(span[66..]);
            }
        }

        var topDown = height < 0;
        height = Math.Abs(height);

        ImageLimits.EnsureSize(width, height);

        var palette = bitsPerPixel <= 8
            ? ReadPalette(span, paletteStart, colorsUsed > 0 ? colorsUsed : 1 << bitsPerPixel, paletteEntrySize, pixelOffset)
            : [];

        if (pixelOffset <= 0 || pixelOffset >= data.Length)
            pixelOffset = paletteStart + (palette.Length / 3 * paletteEntrySize);

        var image = new RasterImage((int)width, (int)height);
        var context = new BmpContext(data, (int)pixelOffset, image, topDown, palette);

        switch (compression)
        {
            case Rgb when bitsPerPixel is 1 or 2 or 4 or 8:
                DecodeIndexed(context, bitsPerPixel);
                break;

            case Rgb when bitsPerPixel == 16:
                DecodeMasked(context, 16, 0x7C00, 0x03E0, 0x001F, 0);
                break;

            case Rgb when bitsPerPixel == 24:
                DecodeTruecolor(context, 3, useAlpha: false);
                break;

            case Rgb when bitsPerPixel == 32:
                DecodeTruecolor(context, 4, useAlpha: HasAlphaBytes(context));
                break;

            case BitFields or AlphaBitFields when bitsPerPixel is 16 or 32:
                if (redMask == 0 && greenMask == 0 && blueMask == 0)
                {
                    (redMask, greenMask, blueMask) = bitsPerPixel == 16
                        ? (0x7C00u, 0x03E0u, 0x001Fu)
                        : (0x00FF0000u, 0x0000FF00u, 0x000000FFu);
                }

                DecodeMasked(context, bitsPerPixel, redMask, greenMask, blueMask, alphaMask);
                break;

            case Rle8 when bitsPerPixel == 8:
                DecodeRunLength(context, fourBit: false);
                break;

            case Rle4 when bitsPerPixel == 4:
                DecodeRunLength(context, fourBit: true);
                break;

            default:
                throw new InvalidImageContentException("This BMP pixel format or compression is not supported.");
        }

        return new DecodedImage(image, ImageFormat.Bmp, 1);
    }

    /// <summary>The palette as R, G, B triples; entries the file does not hold are left black.</summary>
    private static byte[] ReadPalette(ReadOnlySpan<byte> data, int start, int count, int entrySize, long pixelOffset)
    {
        var palette = new byte[count * 3];
        var limit = pixelOffset > start && pixelOffset <= data.Length ? (int)pixelOffset : data.Length;

        for (var index = 0; index < count; index++)
        {
            var source = start + (index * entrySize);

            if (source + 3 > limit)
                break;

            palette[index * 3] = data[source + 2];
            palette[(index * 3) + 1] = data[source + 1];
            palette[(index * 3) + 2] = data[source];
        }

        return palette;
    }

    private static long Stride(int width, int bitsPerPixel) => (((long)width * bitsPerPixel) + 31) / 32 * 4;

    private static void DecodeIndexed(BmpContext context, int bitsPerPixel)
    {
        var image = context.Image;
        var stride = Stride(image.Width, bitsPerPixel);
        var mask = (1 << bitsPerPixel) - 1;

        for (var row = 0; row < image.Height; row++)
        {
            var start = context.PixelOffset + (row * stride);

            if (start + stride > context.Data.Length)
                break;

            for (var x = 0; x < image.Width; x++)
            {
                var bit = (long)x * bitsPerPixel;
                var value = context.Data[start + (bit >> 3)];
                var index = (value >> (8 - bitsPerPixel - (int)(bit & 7))) & mask;
                context.SetIndexed(x, row, index);
            }
        }
    }

    private static void DecodeTruecolor(BmpContext context, int bytesPerPixel, bool useAlpha)
    {
        var image = context.Image;
        var stride = Stride(image.Width, bytesPerPixel * 8);
        var data = context.Data;

        for (var row = 0; row < image.Height; row++)
        {
            var start = context.PixelOffset + (row * stride);

            if (start + stride > data.Length)
                break;

            for (var x = 0; x < image.Width; x++)
            {
                var source = start + (x * bytesPerPixel);
                context.SetColor(x, row, data[source + 2], data[source + 1], data[source], useAlpha ? data[source + 3] : (byte)255);
            }
        }
    }

    private static bool HasAlphaBytes(BmpContext context)
    {
        var image = context.Image;
        var stride = Stride(image.Width, 32);
        var data = context.Data;

        for (var row = 0; row < image.Height; row++)
        {
            var start = context.PixelOffset + (row * stride);

            if (start + stride > data.Length)
                return false;

            for (var x = 0; x < image.Width; x++)
            {
                if (data[start + (x * 4) + 3] != 0)
                    return true;
            }
        }

        return false;
    }

    private static void DecodeMasked(BmpContext context, int bitsPerPixel, uint redMask, uint greenMask, uint blueMask, uint alphaMask)
    {
        var image = context.Image;
        var stride = Stride(image.Width, bitsPerPixel);
        var data = context.Data;
        var bytesPerPixel = bitsPerPixel / 8;

        for (var row = 0; row < image.Height; row++)
        {
            var start = context.PixelOffset + (row * stride);

            if (start + stride > data.Length)
                break;

            for (var x = 0; x < image.Width; x++)
            {
                var source = (int)(start + (x * bytesPerPixel));
                var pixel = bytesPerPixel == 2
                    ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(source));

                context.SetColor(
                    x, row,
                    Channel(pixel, redMask, 0), Channel(pixel, greenMask, 0), Channel(pixel, blueMask, 0),
                    Channel(pixel, alphaMask, 255));
            }
        }
    }

    /// <summary>The value of a bit field scaled to 0..255; <paramref name="absent"/> when the mask is empty.</summary>
    private static byte Channel(uint pixel, uint mask, byte absent)
    {
        if (mask == 0)
            return absent;

        var shift = BitOperations.TrailingZeroCount(mask);
        var maximum = mask >> shift;
        var value = (pixel & mask) >> shift;

        return (byte)(((value * 255) + (maximum / 2)) / maximum);
    }

    private static void DecodeRunLength(BmpContext context, bool fourBit)
    {
        var image = context.Image;
        var data = context.Data;
        var position = (int)context.PixelOffset;
        var x = 0;
        var row = 0;

        while (position + 1 < data.Length && row < image.Height)
        {
            var count = data[position++];
            var value = data[position++];

            if (count > 0)
            {
                for (var index = 0; index < count; index++, x++)
                {
                    if (x < image.Width)
                        context.SetIndexed(x, row, fourBit ? (index % 2 == 0 ? value >> 4 : value & 15) : value);
                }

                continue;
            }

            switch (value)
            {
                case 0:
                    x = 0;
                    row++;
                    break;

                case 1:
                    return;

                case 2:
                    if (position + 1 >= data.Length)
                        return;

                    x += data[position++];
                    row += data[position++];
                    break;

                default:
                {
                    var bytes = fourBit ? (value + 1) / 2 : value;

                    if (position + bytes > data.Length)
                        return;

                    for (var index = 0; index < value; index++, x++)
                    {
                        if (x >= image.Width)
                            continue;

                        var source = data[position + (fourBit ? index / 2 : index)];
                        context.SetIndexed(x, row, fourBit ? (index % 2 == 0 ? source >> 4 : source & 15) : source);
                    }

                    // An absolute run is padded to a whole 16-bit word.
                    position += bytes + (bytes & 1);
                    break;
                }
            }
        }
    }
}
