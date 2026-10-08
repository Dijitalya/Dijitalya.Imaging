using System.Buffers.Binary;

namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Writes WebP files: a lossy VP8 picture in the simple container, or, when the picture has
/// transparency or an ICC profile comes with it, in the extended container, with an ALPH chunk
/// holding the alpha losslessly and an ICCP chunk holding the profile.
/// </summary>
internal static class WebpEncoder
{
    private const byte IccFlag = 0x20;
    private const byte AlphaFlag = 0x10;

    public static byte[] Encode(RasterImage image, int quality, byte[]? iccProfile = null)
    {
        var frame = Vp8Encoder.Encode(image, quality);
        var hasAlpha = image.HasTransparency();

        if (!hasAlpha && iccProfile is null)
            return Container([("VP8 ", frame)]);

        var extended = new byte[10];
        extended[0] = (byte)((hasAlpha ? AlphaFlag : 0) | (iccProfile is not null ? IccFlag : 0));
        WriteUInt24(extended, 4, image.Width - 1);
        WriteUInt24(extended, 7, image.Height - 1);

        // The chunk order is fixed: VP8X, ICCP, ALPH, then the image data.
        var chunks = new List<(string Tag, byte[] Payload)> { ("VP8X", extended) };

        if (iccProfile is not null)
            chunks.Add(("ICCP", iccProfile));

        if (hasAlpha)
            chunks.Add(("ALPH", WebpAlphaEncoder.Encode(image)));

        chunks.Add(("VP8 ", frame));

        return Container([.. chunks]);
    }

    private static byte[] Container((string Tag, byte[] Payload)[] chunks)
    {
        var size = 4L;

        foreach (var (_, payload) in chunks)
            size += 8 + payload.Length + (payload.Length & 1);

        if (size > uint.MaxValue - 8)
            throw new NotSupportedException("The picture is too large for a WebP file.");

        var output = new byte[8 + size];
        "RIFF"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)size);
        "WEBP"u8.CopyTo(output.AsSpan(8));
        var position = 12;

        foreach (var (tag, payload) in chunks)
        {
            for (var i = 0; i < 4; i++)
                output[position + i] = (byte)tag[i];

            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(position + 4), (uint)payload.Length);
            payload.CopyTo(output, position + 8);
            position += 8 + payload.Length + (payload.Length & 1);
        }

        return output;
    }

    private static void WriteUInt24(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
    }
}
