namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Encodes ARGB pixels as a VP8L image stream (RFC 9649), which is how the alpha of a lossy WebP is
/// stored. It uses a colour table, with several pixels packed per byte, when the pixels have few
/// distinct values, LZ77 backward references found through hash chains, and one set of prefix codes
/// built from the symbol counts.
/// </summary>
internal static class Vp8LEncoder
{
    private const int LiteralCodes = 256;
    private const int LengthCodes = 24;
    private const int DistanceCodes = 40;
    private const int MaxLength = 4096;
    private const int MinMatch = 3;
    private const int MaxDistance = (1 << 20) - 120;
    private const int HashBits = 16;
    private const int MaxChain = 32;
    private const int ColorIndexingTransform = 3;

    /// <summary>
    /// Writes the pixels as the main image of a VP8L stream, without the VP8L header (as an ALPH
    /// chunk holds it). With <paramref name="usePalette"/> and at most 256 distinct values, the
    /// pixels are written as indexes into a colour table.
    /// </summary>
    public static byte[] EncodeImageStream(uint[] pixels, int width, int height, bool usePalette)
    {
        var writer = new Vp8LBitWriter();
        var palette = usePalette ? FindPalette(pixels) : null;

        if (palette is not null)
        {
            var bits = palette.Length > 16 ? 0 : palette.Length > 4 ? 1 : palette.Length > 2 ? 2 : 3;

            writer.WriteBits(1, 1);
            writer.WriteBits(ColorIndexingTransform, 2);
            writer.WriteBits((uint)(palette.Length - 1), 8);

            // The table is stored as differences between successive entries.
            var table = new uint[palette.Length];
            table[0] = palette[0];

            for (var i = 1; i < palette.Length; i++)
                table[i] = SubtractPixels(palette[i], palette[i - 1]);

            WriteEntropyCodedImage(writer, table, palette.Length, isMainImage: false);

            pixels = PackIndexes(pixels, width, height, palette, bits, out width);
        }

        writer.WriteBits(0, 1);
        WriteEntropyCodedImage(writer, pixels, width, isMainImage: true);

        return writer.ToArray();
    }

    private static uint[]? FindPalette(uint[] pixels)
    {
        var colors = new HashSet<uint>();

        foreach (var pixel in pixels)
        {
            if (colors.Add(pixel) && colors.Count > 256)
                return null;
        }

        var palette = colors.ToArray();
        Array.Sort(palette);

        return palette;
    }

    // Replaces each pixel by its index in the palette, several indexes per pixel when they fit.
    private static uint[] PackIndexes(uint[] pixels, int width, int height, uint[] palette, int bits, out int packedWidth)
    {
        var index = new Dictionary<uint, uint>(palette.Length);

        for (var i = 0; i < palette.Length; i++)
            index[palette[i]] = (uint)i;

        var perPixel = 1 << bits;
        var bitsPerIndex = 8 >> bits;
        packedWidth = (width + perPixel - 1) >> bits;
        var packed = new uint[packedWidth * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = index[pixels[(y * width) + x]];
                packed[(y * packedWidth) + (x >> bits)] |= value << (8 + ((x & (perPixel - 1)) * bitsPerIndex));
            }
        }

        return packed;
    }

    private static void WriteEntropyCodedImage(Vp8LBitWriter writer, uint[] pixels, int width, bool isMainImage)
    {
        // No colour cache.
        writer.WriteBits(0, 1);

        // One set of prefix codes for the whole image.
        if (isMainImage)
            writer.WriteBits(0, 1);

        var (kinds, values) = FindBackwardReferences(pixels, width);
        var distanceCodes = new Vp8LDistanceCoder(width);

        var green = new long[LiteralCodes + LengthCodes];
        var red = new long[256];
        var blue = new long[256];
        var alpha = new long[256];
        var distance = new long[DistanceCodes];

        for (var i = 0; i < kinds.Count; i++)
        {
            if (kinds[i] == 0)
            {
                var pixel = values[i];
                green[(pixel >> 8) & 0xFF]++;
                red[(pixel >> 16) & 0xFF]++;
                blue[pixel & 0xFF]++;
                alpha[pixel >> 24]++;
            }
            else
            {
                green[LiteralCodes + PrefixCode(kinds[i]).Code]++;
                distance[PrefixCode(distanceCodes.Code((int)values[i])).Code]++;
            }
        }

        var greenCode = Vp8LEncoderCode.Build(green);
        var redCode = Vp8LEncoderCode.Build(red);
        var blueCode = Vp8LEncoderCode.Build(blue);
        var alphaCode = Vp8LEncoderCode.Build(alpha);
        var distanceCode = Vp8LEncoderCode.Build(distance);

        greenCode.WriteDescription(writer);
        redCode.WriteDescription(writer);
        blueCode.WriteDescription(writer);
        alphaCode.WriteDescription(writer);
        distanceCode.WriteDescription(writer);

        for (var i = 0; i < kinds.Count; i++)
        {
            if (kinds[i] == 0)
            {
                var pixel = values[i];
                greenCode.WriteSymbol(writer, (int)((pixel >> 8) & 0xFF));
                redCode.WriteSymbol(writer, (int)((pixel >> 16) & 0xFF));
                blueCode.WriteSymbol(writer, (int)(pixel & 0xFF));
                alphaCode.WriteSymbol(writer, (int)(pixel >> 24));
                continue;
            }

            var length = PrefixCode(kinds[i]);
            greenCode.WriteSymbol(writer, LiteralCodes + length.Code);
            writer.WriteBits((uint)length.ExtraValue, length.ExtraBits);

            var distancePrefix = PrefixCode(distanceCodes.Code((int)values[i]));
            distanceCode.WriteSymbol(writer, distancePrefix.Code);
            writer.WriteBits((uint)distancePrefix.ExtraValue, distancePrefix.ExtraBits);
        }
    }

    // Greedy LZ77 parse. Each entry is a literal (kind 0, value = pixel) or a copy (kind = length, value = distance).
    private static (List<int> Kinds, List<uint> Values) FindBackwardReferences(uint[] pixels, int width)
    {
        var kinds = new List<int>();
        var values = new List<uint>();
        var count = pixels.Length;
        var head = new int[1 << HashBits];
        var chain = new int[count];
        Array.Fill(head, -1);

        var i = 0;

        while (i < count)
        {
            var bestLength = 0;
            var bestDistance = 0;
            var limit = Math.Min(MaxLength, count - i);

            if (limit >= MinMatch)
            {
                // The pixel to the left and the one above have the cheapest distance codes.
                TryCandidate(pixels, i, 1, limit, ref bestLength, ref bestDistance);
                TryCandidate(pixels, i, width, limit, ref bestLength, ref bestDistance);

                var candidate = head[Hash(pixels, i)];

                for (var steps = 0; candidate >= 0 && steps < MaxChain && bestLength < limit; steps++)
                {
                    var distanceToCandidate = i - candidate;

                    if (distanceToCandidate > MaxDistance)
                        break;

                    TryCandidate(pixels, i, distanceToCandidate, limit, ref bestLength, ref bestDistance);
                    candidate = chain[candidate];
                }
            }

            var step = bestLength >= MinMatch ? bestLength : 1;

            if (step > 1)
            {
                kinds.Add(bestLength);
                values.Add((uint)bestDistance);
            }
            else
            {
                kinds.Add(0);
                values.Add(pixels[i]);
            }

            for (var end = i + step; i < end; i++)
            {
                if (i + 1 < count)
                {
                    var hash = Hash(pixels, i);
                    chain[i] = head[hash];
                    head[hash] = i;
                }
            }
        }

        return (kinds, values);
    }

    private static void TryCandidate(uint[] pixels, int position, int distance, int limit, ref int bestLength, ref int bestDistance)
    {
        if (distance <= 0 || distance > position || distance > MaxDistance)
            return;

        var source = position - distance;

        // Quick rejection: the candidate must at least beat the best match at its last pixel.
        if (bestLength > 0 && (bestLength >= limit || pixels[source + bestLength] != pixels[position + bestLength]))
            return;

        var length = 0;

        while (length < limit && pixels[source + length] == pixels[position + length])
            length++;

        if (length > bestLength)
        {
            bestLength = length;
            bestDistance = distance;
        }
    }

    private static int Hash(uint[] pixels, int position)
    {
        var next = position + 1 < pixels.Length ? pixels[position + 1] : 0;

        return (int)(((pixels[position] * 0x1E35A7BDu) ^ (next * 0x9E3779B1u)) >> (32 - HashBits));
    }

    // The prefix code of a length or distance value (at least 1): the symbol and its extra bits.
    private static (int Code, int ExtraBits, int ExtraValue) PrefixCode(int value)
    {
        var d = value - 1;

        if (d < 2)
            return (d, 0, 0);

        var highest = 31 - int.LeadingZeroCount(d);
        var second = (d >> (highest - 1)) & 1;
        var extraBits = highest - 1;

        return ((2 * highest) + second, extraBits, d & ((1 << extraBits) - 1));
    }

    private static uint SubtractPixels(uint a, uint b)
    {
        var alphaGreen = 0x00FF00FFu + (a & 0xFF00FF00u) - (b & 0xFF00FF00u);
        var redBlue = 0xFF00FF00u + (a & 0x00FF00FFu) - (b & 0x00FF00FFu);

        return (alphaGreen & 0xFF00FF00u) | (redBlue & 0x00FF00FFu);
    }
}
