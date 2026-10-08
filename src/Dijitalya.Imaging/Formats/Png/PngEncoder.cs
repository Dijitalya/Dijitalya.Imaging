using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Dijitalya.Imaging.Common;

namespace Dijitalya.Imaging.Formats.Png;

/// <summary>
/// Encodes PNG losslessly in the smallest of three shapes: indexed (a palette, with tRNS) when the
/// picture has at most 256 colours — logos and icons usually do — otherwise truecolour, with an alpha
/// channel only when some pixel is not opaque. Truecolour rows get the filter that suits them best.
/// The only metadata written is an ICC profile, when one is given.
/// </summary>
internal static class PngEncoder
{
    private const int MaxPaletteSize = 256;

    public static byte[] Encode(RasterImage image, byte[]? iccProfile = null)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var palette = TryBuildPalette(image, out var indexes);
        byte[] raw;
        int colorType;

        if (palette is not null)
        {
            colorType = PngDecoder.Indexed;
            raw = IndexedRows(image, indexes!);
        }
        else
        {
            var hasAlpha = image.HasTransparency();
            colorType = hasAlpha ? PngDecoder.TruecolorAlpha : PngDecoder.Truecolor;
            raw = FilteredRows(image, hasAlpha ? 4 : 3);
        }

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 8;
        header[9] = (byte)colorType;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(output, "IHDR"u8, header);

        if (iccProfile is not null)
            WriteChunk(output, "iCCP"u8, IccChunk(iccProfile));

        if (palette is not null)
        {
            var colors = new byte[palette.Count * 3];
            var alphas = new byte[palette.Count];
            var lastTransparent = -1;

            for (var index = 0; index < palette.Count; index++)
            {
                var color = palette[index];
                colors[index * 3] = (byte)color;
                colors[(index * 3) + 1] = (byte)(color >> 8);
                colors[(index * 3) + 2] = (byte)(color >> 16);
                alphas[index] = (byte)(color >> 24);

                if (alphas[index] != 255)
                    lastTransparent = index;
            }

            WriteChunk(output, "PLTE"u8, colors);

            if (lastTransparent >= 0)
                WriteChunk(output, "tRNS"u8, alphas.AsSpan(0, lastTransparent + 1));
        }

        WriteChunk(output, "IDAT"u8, Deflate(raw));
        WriteChunk(output, "IEND"u8, []);

        return output.ToArray();
    }

    /// <summary>
    /// The colours of the picture when there are at most 256 of them, ordered so the transparent ones
    /// come first and the tRNS chunk stays short; null otherwise.
    /// </summary>
    private static List<uint>? TryBuildPalette(RasterImage image, out byte[]? indexes)
    {
        indexes = null;
        var pixels = MemoryMarshal.Cast<byte, uint>(image.Pixels.AsSpan());
        var seen = new HashSet<uint>();

        foreach (var pixel in pixels)
        {
            if (seen.Add(pixel) && seen.Count > MaxPaletteSize)
                return null;
        }

        // A pixel is R, G, B, A in memory, so on a little-endian machine alpha is the top byte; the
        // palette is built from the bytes themselves to stay correct on any machine.
        var palette = new List<uint>(seen.Count);
        var byColor = new Dictionary<uint, int>(seen.Count);
        var bytes = image.Pixels;

        foreach (var pass in new[] { true, false })
        {
            for (var index = 0; index < bytes.Length; index += 4)
            {
                var color = (uint)(bytes[index] | (bytes[index + 1] << 8) | (bytes[index + 2] << 16) | (bytes[index + 3] << 24));

                if ((bytes[index + 3] != 255) == pass && !byColor.ContainsKey(color))
                {
                    byColor[color] = palette.Count;
                    palette.Add(color);
                }
            }
        }

        indexes = new byte[image.Width * image.Height];

        for (var index = 0; index < indexes.Length; index++)
        {
            var offset = index * 4;
            var color = (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
            indexes[index] = (byte)byColor[color];
        }

        return palette;
    }

    private static byte[] IndexedRows(RasterImage image, byte[] indexes)
    {
        // Filtering does not help palette indexes; every row is stored as it is (filter 0).
        var raw = new byte[image.Height * (image.Width + 1)];

        for (var y = 0; y < image.Height; y++)
            Array.Copy(indexes, y * image.Width, raw, (y * (image.Width + 1)) + 1, image.Width);

        return raw;
    }

    private static byte[] FilteredRows(RasterImage image, int channels)
    {
        var rowLength = image.Width * channels;
        var raw = new byte[image.Height * (rowLength + 1)];
        var previous = new byte[rowLength];
        var current = new byte[rowLength];
        var candidate = new byte[rowLength];
        var best = new byte[rowLength];
        var pixels = image.Pixels;

        for (var y = 0; y < image.Height; y++)
        {
            var source = y * image.Width * 4;

            for (var x = 0; x < image.Width; x++)
            {
                for (var channel = 0; channel < channels; channel++)
                    current[(x * channels) + channel] = pixels[source + (x * 4) + channel];
            }

            // The filter whose output has the smallest sum of absolute values (as signed bytes): the
            // heuristic the PNG specification recommends.
            var bestFilter = 0;
            var bestScore = long.MaxValue;

            for (var filter = 0; filter <= 4; filter++)
            {
                var score = Filter(filter, current, previous, channels, candidate);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestFilter = filter;
                    (best, candidate) = (candidate, best);
                }
            }

            var target = y * (rowLength + 1);
            raw[target] = (byte)bestFilter;
            Array.Copy(best, 0, raw, target + 1, rowLength);

            (previous, current) = (current, previous);
        }

        return raw;
    }

    private static long Filter(int filter, byte[] row, byte[] previous, int bytesPerPixel, byte[] output)
    {
        long score = 0;

        for (var index = 0; index < row.Length; index++)
        {
            var left = index >= bytesPerPixel ? row[index - bytesPerPixel] : 0;
            var above = previous[index];
            var upperLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : 0;

            var value = filter switch
            {
                0 => row[index],
                1 => row[index] - left,
                2 => row[index] - above,
                3 => row[index] - ((left + above) >> 1),
                _ => row[index] - PngDecoder.Paeth(left, above, upperLeft),
            };

            var filtered = (byte)value;
            output[index] = filtered;
            score += Math.Abs((int)(sbyte)filtered);
        }

        return score;
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var compressed = new MemoryStream();

        using (var deflater = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(raw);
        }

        return compressed.ToArray();
    }

    // iCCP: a profile name, a zero byte, compression method 0 and the zlib-compressed profile.
    private static byte[] IccChunk(byte[] profile)
    {
        using var body = new MemoryStream();
        body.Write("ICC profile\0"u8);
        body.WriteByte(0);

        using (var zlib = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(profile);

        return body.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        Span<byte> number = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)body.Length);
        output.Write(number);
        output.Write(type);
        output.Write(body);

        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, type), body) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }
}
