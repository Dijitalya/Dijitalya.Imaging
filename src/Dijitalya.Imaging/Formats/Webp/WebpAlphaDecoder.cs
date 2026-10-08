namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Decodes the ALPH chunk that gives a lossy WebP its transparency: raw or VP8L-compressed alpha
/// values, then the spatial filter (none, horizontal, vertical or gradient) undone row by row, as
/// libwebp does it.
/// </summary>
internal static class WebpAlphaDecoder
{
    private const int NoCompression = 0;
    private const int LosslessCompression = 1;
    private const int HorizontalFilter = 1;
    private const int VerticalFilter = 2;
    private const int GradientFilter = 3;

    /// <summary>Writes the alpha of the ALPH payload into the A bytes of <paramref name="image"/>.</summary>
    public static void Apply(byte[] data, int start, int length, RasterImage image)
    {
        if (length <= 1)
            throw new InvalidImageContentException("The WebP alpha chunk is empty.");

        var header = data[start];
        var method = header & 3;
        var filter = (header >> 2) & 3;
        var preprocessing = (header >> 4) & 3;
        var reserved = (header >> 6) & 3;

        if (method > LosslessCompression || preprocessing > 1 || reserved != 0)
            throw new InvalidImageContentException("The WebP alpha chunk header is invalid.");

        var width = image.Width;
        var height = image.Height;
        byte[] filtered;

        if (method == NoCompression)
        {
            if (length - 1 < (long)width * height)
                throw new InvalidImageContentException("The WebP alpha chunk is cut off.");

            filtered = data.AsSpan(start + 1, width * height).ToArray();
        }
        else
        {
            filtered = Vp8LDecoder.DecodeAlpha(data, start + 1, length - 1, width, height);
        }

        var alpha = new byte[width * height];

        for (var y = 0; y < height; y++)
            Unfilter(filter, filtered, alpha, y, width);

        var pixels = image.Pixels;

        for (var index = 0; index < alpha.Length; index++)
            pixels[(index * 4) + 3] = alpha[index];
    }

    private static void Unfilter(int filter, byte[] input, byte[] output, int y, int width)
    {
        var row = y * width;
        var previous = y > 0 ? row - width : -1;

        if (filter == 0)
        {
            Array.Copy(input, row, output, row, width);
            return;
        }

        // Every filter falls back to the horizontal one on the first row.
        if (filter == HorizontalFilter || previous < 0)
        {
            var prediction = previous < 0 ? 0 : output[previous];

            for (var x = 0; x < width; x++)
            {
                output[row + x] = (byte)(prediction + input[row + x]);
                prediction = output[row + x];
            }

            return;
        }

        if (filter == VerticalFilter)
        {
            for (var x = 0; x < width; x++)
                output[row + x] = (byte)(output[previous + x] + input[row + x]);

            return;
        }

        if (filter == GradientFilter)
        {
            int top = output[previous];
            var topLeft = top;
            var left = top;

            for (var x = 0; x < width; x++)
            {
                top = output[previous + x];
                left = (byte)(input[row + x] + GradientPredictor(left, top, topLeft));
                topLeft = top;
                output[row + x] = (byte)left;
            }
        }
    }

    private static int GradientPredictor(int left, int top, int topLeft)
    {
        var gradient = left + top - topLeft;

        return gradient < 0 ? 0 : gradient > 255 ? 255 : gradient;
    }
}
