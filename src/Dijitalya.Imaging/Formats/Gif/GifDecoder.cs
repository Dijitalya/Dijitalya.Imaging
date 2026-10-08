using System.Buffers.Binary;

namespace Dijitalya.Imaging.Formats.Gif;

/// <summary>
/// Decodes the first frame of a GIF (87a and 89a) onto a canvas the size of the logical screen,
/// with the transparency of its graphic control extension and with interlacing undone. Every block
/// is read through its length bytes with bounds checks, so a block without its terminator ends the
/// reading instead of looping forever, a known way for a crafted GIF to hang a decoder.
/// </summary>
internal static class GifDecoder
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;
    private const byte GraphicControlLabel = 0xF9;

    public static DecodedImage Decode(byte[] data)
    {
        if (data.Length < 13)
            throw new InvalidImageContentException("The GIF is too short.");

        int screenWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        int screenHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var packed = data[10];
        var position = 13;
        byte[]? globalTable = null;

        if ((packed & 0x80) != 0)
        {
            var size = 3 * (2 << (packed & 7));

            if (position + size > data.Length)
                throw new InvalidImageContentException("The GIF colour table is cut off.");

            globalTable = data.AsSpan(position, size).ToArray();
            position += size;
        }

        var transparentIndex = -1;

        while (position < data.Length)
        {
            var block = data[position++];

            if (block == ExtensionIntroducer)
            {
                if (position >= data.Length)
                    break;

                var label = data[position++];

                if (label == GraphicControlLabel && position + 5 < data.Length && data[position] >= 4)
                    transparentIndex = (data[position + 1] & 1) != 0 ? data[position + 4] : -1;

                position = SkipSubBlocks(data, position);
            }
            else if (block == ImageSeparator)
            {
                return DecodeFrame(data, position, screenWidth, screenHeight, globalTable, transparentIndex);
            }
            else if (block == Trailer)
            {
                break;
            }
            else
            {
                throw new InvalidImageContentException("The GIF holds an unknown block.");
            }
        }

        throw new InvalidImageContentException("The GIF has no image.");
    }

    private static DecodedImage DecodeFrame(byte[] data, int position, int screenWidth, int screenHeight, byte[]? globalTable, int transparentIndex)
    {
        if (position + 9 > data.Length)
            throw new InvalidImageContentException("The GIF image descriptor is cut off.");

        int left = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
        int top = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 2));
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 4));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 6));
        var packed = data[position + 8];
        position += 9;

        ImageLimits.EnsureSize(width, height);

        // A screen that is missing or smaller than the frame would cut the picture: the frame decides then.
        if (screenWidth == 0 || screenHeight == 0 || left + width > screenWidth || top + height > screenHeight)
        {
            screenWidth = Math.Max(screenWidth, left + width);
            screenHeight = Math.Max(screenHeight, top + height);
        }

        // A screen beyond the limits, while the frame itself is within them: the frame alone is the picture.
        if (!ImageLimits.IsAllowed(screenWidth, screenHeight))
        {
            screenWidth = width;
            screenHeight = height;
            left = top = 0;
        }

        var table = globalTable;

        if ((packed & 0x80) != 0)
        {
            var size = 3 * (2 << (packed & 7));

            if (position + size > data.Length)
                throw new InvalidImageContentException("The GIF colour table is cut off.");

            table = data.AsSpan(position, size).ToArray();
            position += size;
        }

        if (position >= data.Length)
            throw new InvalidImageContentException("The GIF image data is missing.");

        var minimumCodeSize = data[position++];
        var compressed = ReadSubBlocks(data, position);
        var indexes = new byte[width * height];
        var count = GifLzwDecoder.Decode(compressed, minimumCodeSize, indexes);

        if (count == 0)
            throw new InvalidImageContentException("The GIF image data cannot be decoded.");

        var image = new RasterImage(screenWidth, screenHeight);
        var pixels = image.Pixels;
        var interlaced = (packed & 0x40) != 0;
        var rows = interlaced ? InterlacedRows(height) : null;

        for (var row = 0; row < height; row++)
        {
            var y = top + (rows?[row] ?? row);

            for (var column = 0; column < width; column++)
            {
                var index = (row * width) + column;

                if (index >= count)
                    break;

                var colorIndex = indexes[index];

                if (colorIndex == transparentIndex)
                    continue;

                var target = ((y * screenWidth) + left + column) * 4;
                var entry = colorIndex * 3;

                if (table is not null && entry + 2 < table.Length)
                {
                    pixels[target] = table[entry];
                    pixels[target + 1] = table[entry + 1];
                    pixels[target + 2] = table[entry + 2];
                }

                pixels[target + 3] = 255;
            }
        }

        return new DecodedImage(image, ImageFormat.Gif, 1);
    }

    /// <summary>The picture row each stored row of an interlaced image belongs to: every 8th from 0, every 8th from 4, every 4th from 2, every 2nd from 1.</summary>
    private static int[] InterlacedRows(int height)
    {
        var rows = new int[height];
        var stored = 0;

        foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
        {
            for (var row = start; row < height; row += step)
                rows[stored++] = row;
        }

        return rows;
    }

    /// <summary>Skips a chain of sub-blocks; returns the position after its terminator, or the end of the data.</summary>
    private static int SkipSubBlocks(byte[] data, int position)
    {
        while (position < data.Length)
        {
            var size = data[position++];

            if (size == 0)
                return position;

            position += size;
        }

        return data.Length;
    }

    /// <summary>The data of a chain of sub-blocks without their length bytes; a chain cut off by the end of the file ends there.</summary>
    private static byte[] ReadSubBlocks(byte[] data, int position)
    {
        using var output = new MemoryStream();

        while (position < data.Length)
        {
            var size = data[position++];

            if (size == 0)
                break;

            var available = Math.Min(size, data.Length - position);
            output.Write(data, position, available);
            position += size;
        }

        return output.ToArray();
    }
}
