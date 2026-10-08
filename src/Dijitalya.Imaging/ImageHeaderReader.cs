using System.Buffers.Binary;
using Dijitalya.Imaging.Formats.Webp;

namespace Dijitalya.Imaging;

/// <summary>
/// Reads the size of a picture from its header alone, without decoding it, so an upload can be
/// measured before the memory for its pixels is spent. The size is the one stored in the file (before
/// any EXIF orientation is applied); for a GIF it is the logical screen.
/// </summary>
internal static class ImageHeaderReader
{
    // JPEG start-of-frame markers: every SOFn except DHT (C4), JPG (C8) and DAC (CC).
    private static readonly bool[] IsStartOfFrame = CreateStartOfFrameTable();

    /// <summary>The width and height in the header, or null when the header is missing, cut off or invalid.</summary>
    public static (int Width, int Height)? Read(ReadOnlySpan<byte> data, ImageFormat format)
    {
        var size = format switch
        {
            ImageFormat.Jpeg => ReadJpeg(data),
            ImageFormat.Png => ReadPng(data),
            ImageFormat.Gif => ReadGif(data),
            ImageFormat.Bmp => ReadBmp(data),
            _ => ReadWebp(data),
        };

        return size is { Width: > 0, Height: > 0 } ? size : null;
    }

    private static (int Width, int Height)? ReadJpeg(ReadOnlySpan<byte> data)
    {
        var position = 2;

        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            var marker = data[position + 1];

            // Fill bytes, and markers that stand alone without a length.
            if (marker is 0xFF or 0x01 or 0xD8 || marker is >= 0xD0 and <= 0xD7)
            {
                position += marker == 0xFF ? 1 : 2;
                continue;
            }

            // A scan or the end before any frame header: there is no size to read.
            if (marker is 0xDA or 0xD9)
                return null;

            var length = (data[position + 2] << 8) | data[position + 3];

            if (length < 2)
                return null;

            if (IsStartOfFrame[marker])
            {
                if (position + 9 > data.Length)
                    return null;

                var height = (data[position + 5] << 8) | data[position + 6];
                var width = (data[position + 7] << 8) | data[position + 8];

                return (width, height);
            }

            position += 2 + length;
        }

        return null;
    }

    private static (int Width, int Height)? ReadPng(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;

        var width = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);

        return width > int.MaxValue || height > int.MaxValue ? null : ((int)width, (int)height);
    }

    private static (int Width, int Height)? ReadGif(ReadOnlySpan<byte> data) =>
        data.Length < 10 ? null : (BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));

    private static (int Width, int Height)? ReadBmp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 26)
            return null;

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);

        if (headerSize == 12)
            return (BinaryPrimitives.ReadUInt16LittleEndian(data[18..]), BinaryPrimitives.ReadUInt16LittleEndian(data[20..]));

        var width = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);

        // A negative height marks rows stored top-down.
        return height == int.MinValue ? null : (width, Math.Abs(height));
    }

    private static (int Width, int Height)? ReadWebp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30)
            return null;

        var tag = data.Slice(12, 4);
        var payload = 20;

        if (tag.SequenceEqual("VP8X"u8))
        {
            var width = 1 + (data[payload + 4] | (data[payload + 5] << 8) | (data[payload + 6] << 16));
            var height = 1 + (data[payload + 7] | (data[payload + 8] << 8) | (data[payload + 9] << 16));

            return (width, height);
        }

        try
        {
            var bytes = data[payload..].ToArray();

            if (tag.SequenceEqual("VP8 "u8))
                return Vp8Decoder.ReadSize(bytes, 0, bytes.Length);

            if (tag.SequenceEqual("VP8L"u8))
                return Vp8LDecoder.ReadSize(bytes, 0, bytes.Length);
        }
        catch (InvalidImageContentException)
        {
        }

        return null;
    }

    private static bool[] CreateStartOfFrameTable()
    {
        var table = new bool[256];

        for (var marker = 0xC0; marker <= 0xCF; marker++)
            table[marker] = marker is not (0xC4 or 0xC8 or 0xCC);

        return table;
    }
}
