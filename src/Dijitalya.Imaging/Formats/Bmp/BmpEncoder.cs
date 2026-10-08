using System.Buffers.Binary;

namespace Dijitalya.Imaging.Formats.Bmp;

/// <summary>
/// Encodes an uncompressed bitmap, rows bottom-up: 24 bits per pixel with the classic 40-byte
/// header for an opaque picture, 32 bits per pixel with a version 4 header and an alpha mask when
/// some pixel is not opaque, so transparency survives.
/// </summary>
internal static class BmpEncoder
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int V4HeaderSize = 108;
    private const int BitFields = 3;

    // 72 dots per inch, in dots per metre.
    private const int Resolution = 2835;

    public static byte[] Encode(RasterImage image)
    {
        var hasAlpha = image.HasTransparency();
        var bytesPerPixel = hasAlpha ? 4 : 3;
        var headerSize = hasAlpha ? V4HeaderSize : InfoHeaderSize;
        var stride = ((image.Width * bytesPerPixel) + 3) & ~3;
        var pixelOffset = FileHeaderSize + headerSize;
        var fileSize = pixelOffset + ((long)stride * image.Height);

        if (fileSize > int.MaxValue)
            throw new NotSupportedException("The picture is too large for a bitmap file.");

        var output = new byte[fileSize];
        var span = output.AsSpan();

        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], (int)fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], pixelOffset);

        var header = span[FileHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(header, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], image.Height);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], (short)(bytesPerPixel * 8));
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], hasAlpha ? BitFields : 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * image.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], Resolution);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], Resolution);

        if (hasAlpha)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 0xFF000000);

            // Colour space "sRGB".
            BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 0x73524742);
        }

        var pixels = image.Pixels;

        for (var y = 0; y < image.Height; y++)
        {
            var source = (image.Height - 1 - y) * image.Width * 4;
            var target = pixelOffset + (y * stride);

            for (var x = 0; x < image.Width; x++, source += 4, target += bytesPerPixel)
            {
                output[target] = pixels[source + 2];
                output[target + 1] = pixels[source + 1];
                output[target + 2] = pixels[source];

                if (hasAlpha)
                    output[target + 3] = pixels[source + 3];
            }
        }

        return output;
    }
}
