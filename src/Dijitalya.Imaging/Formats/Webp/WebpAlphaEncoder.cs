namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Writes the ALPH chunk of a lossy WebP: the alpha plane compressed losslessly with VP8L. Masks with
/// few alpha levels go through a colour table that packs several pixels per symbol; other planes go
/// through the spatial filter (none, horizontal, vertical or gradient) whose residuals look cheapest.
/// The smaller of the two results is kept.
/// </summary>
internal static class WebpAlphaEncoder
{
    private const int LosslessCompression = 1;
    private const int PaletteLimit = 16;

    public static byte[] Encode(RasterImage image)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = image.Pixels;
        var alpha = new byte[width * height];
        var levels = new HashSet<byte>();

        for (var i = 0; i < alpha.Length; i++)
        {
            alpha[i] = pixels[(i * 4) + 3];

            if (levels.Count <= PaletteLimit)
                levels.Add(alpha[i]);
        }

        byte[]? best = null;
        var bestFilter = 0;

        if (levels.Count <= PaletteLimit)
        {
            best = Vp8LEncoder.EncodeImageStream(ToGreen(alpha), width, height, usePalette: true);
            bestFilter = 0;
        }

        var filter = CheapestFilter(alpha, width, height);
        var filtered = Vp8LEncoder.EncodeImageStream(ToGreen(Filter(alpha, width, height, filter)), width, height, usePalette: false);

        if (best is null || filtered.Length < best.Length)
        {
            best = filtered;
            bestFilter = filter;
        }

        var output = new byte[1 + best.Length];
        output[0] = (byte)(LosslessCompression | (bestFilter << 2));
        best.CopyTo(output, 1);

        return output;
    }

    // VP8L carries the alpha plane in the green channel.
    private static uint[] ToGreen(byte[] values)
    {
        var argb = new uint[values.Length];

        for (var i = 0; i < values.Length; i++)
            argb[i] = (uint)values[i] << 8;

        return argb;
    }

    // Picks the filter whose residuals have the lowest order-0 entropy.
    private static int CheapestFilter(byte[] alpha, int width, int height)
    {
        var best = 0;
        var bestEntropy = double.MaxValue;

        for (var filter = 0; filter < 4; filter++)
        {
            var counts = new long[256];

            foreach (var value in Filter(alpha, width, height, filter))
                counts[value]++;

            var entropy = 0.0;

            foreach (var count in counts)
            {
                if (count > 0)
                    entropy -= count * Math.Log2((double)count / alpha.Length);
            }

            if (entropy < bestEntropy)
            {
                bestEntropy = entropy;
                best = filter;
            }
        }

        return best;
    }

    // The forward filters whose inverses the decoder applies (WebpAlphaDecoder).
    private static byte[] Filter(byte[] alpha, int width, int height, int filter)
    {
        if (filter == 0)
            return alpha;

        var output = new byte[alpha.Length];

        for (var y = 0; y < height; y++)
        {
            var row = y * width;

            for (var x = 0; x < width; x++)
            {
                int prediction;

                if (y == 0)
                    prediction = x == 0 ? 0 : alpha[row + x - 1];
                else if (x == 0)
                    prediction = alpha[row - width];
                else
                {
                    prediction = filter switch
                    {
                        1 => alpha[row + x - 1],
                        2 => alpha[row - width + x],
                        _ => Math.Clamp(alpha[row + x - 1] + alpha[row - width + x] - alpha[row - width + x - 1], 0, 255),
                    };
                }

                output[row + x] = (byte)(alpha[row + x] - prediction);
            }
        }

        return output;
    }
}
