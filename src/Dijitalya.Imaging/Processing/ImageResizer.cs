namespace Dijitalya.Imaging.Processing;

/// <summary>
/// Bicubic resizing. Colours are weighted by their alpha while they are mixed (premultiplied), so a
/// transparent pixel's colour never bleeds into its neighbours as a dark or coloured fringe.
/// </summary>
public static class ImageResizer
{
    /// <summary>A copy of the picture resized to exactly <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static RasterImage Resize(RasterImage source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (width == source.Width && height == source.Height)
            return source.Clone();

        var destination = new RasterImage(width, height);
        var columns = ResamplingKernelMap.Create(source.Width, width);
        var rows = ResamplingKernelMap.Create(source.Height, height);

        // Rows resized horizontally are kept in a ring as long as the rows of the result still need
        // them; the windows of consecutive result rows only ever move down, so each source row is
        // resized once and memory stays at a few rows however large the picture is.
        var ringSize = rows.MaxCount;
        var stride = width * 4;
        var ring = new float[ringSize * stride];
        var ringRows = new int[ringSize];
        Array.Fill(ringRows, -1);

        var premultiplied = new float[source.Width * 4];
        var accumulator = new float[stride];
        var target = destination.Pixels;

        for (var y = 0; y < height; y++)
        {
            var start = rows.Starts[y];
            var count = rows.Counts[y];
            var offset = rows.Offsets[y];

            Array.Clear(accumulator);

            for (var tap = 0; tap < count; tap++)
            {
                var sourceRow = start + tap;
                var slot = sourceRow % ringSize;

                if (ringRows[slot] != sourceRow)
                {
                    ResizeRow(source, sourceRow, columns, premultiplied, ring.AsSpan(slot * stride, stride));
                    ringRows[slot] = sourceRow;
                }

                var weight = rows.Weights[offset + tap];
                var row = ring.AsSpan(slot * stride, stride);

                for (var index = 0; index < stride; index++)
                    accumulator[index] += row[index] * weight;
            }

            var rowStart = y * stride;

            for (var x = 0; x < width; x++)
            {
                var index = x * 4;
                var alpha = Math.Clamp(accumulator[index + 3], 0f, 255f);
                var output = rowStart + index;

                if (alpha <= 0.5f)
                {
                    target[output] = target[output + 1] = target[output + 2] = target[output + 3] = 0;
                    continue;
                }

                var factor = 255f / alpha;
                target[output] = ToByte(accumulator[index] * factor);
                target[output + 1] = ToByte(accumulator[index + 1] * factor);
                target[output + 2] = ToByte(accumulator[index + 2] * factor);
                target[output + 3] = ToByte(alpha);
            }
        }

        return destination;
    }

    /// <summary>
    /// A copy of the picture fitted into a box as <paramref name="mode"/> says. With
    /// <see cref="ResizeMode.Max"/> a width or height of 0 is worked out from the other, and both 0
    /// leave the size as it is.
    /// </summary>
    public static RasterImage ResizeToBox(RasterImage source, int width, int height, ResizeMode mode)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        if (mode == ResizeMode.Max)
        {
            if (width == 0 && height == 0)
                return source.Clone();

            int targetWidth, targetHeight;

            if (width == 0)
            {
                targetHeight = height;
                targetWidth = Scale(source.Width, (double)height / source.Height);
            }
            else if (height == 0)
            {
                targetWidth = width;
                targetHeight = Scale(source.Height, (double)width / source.Width);
            }
            else
            {
                var ratio = Math.Min((double)width / source.Width, (double)height / source.Height);
                targetWidth = Scale(source.Width, ratio);
                targetHeight = Scale(source.Height, ratio);
            }

            return Resize(source, targetWidth, targetHeight);
        }

        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);

        if (mode == ResizeMode.Crop)
        {
            var ratio = Math.Max((double)width / source.Width, (double)height / source.Height);
            var scaledWidth = Math.Max(width, Scale(source.Width, ratio));
            var scaledHeight = Math.Max(height, Scale(source.Height, ratio));
            var scaled = Resize(source, scaledWidth, scaledHeight);

            return ImageTransforms.Crop(scaled, (scaledWidth - width) / 2, (scaledHeight - height) / 2, width, height);
        }

        var fit = Math.Min((double)width / source.Width, (double)height / source.Height);
        var fitted = Resize(source, Math.Min(width, Scale(source.Width, fit)), Math.Min(height, Scale(source.Height, fit)));
        var canvas = new RasterImage(width, height);

        ImageTransforms.Paste(fitted, canvas, (width - fitted.Width) / 2, (height - fitted.Height) / 2);

        return canvas;
    }

    private static void ResizeRow(RasterImage source, int sourceRow, ResamplingKernelMap columns, float[] premultiplied, Span<float> output)
    {
        var pixels = source.Pixels;
        var rowStart = sourceRow * source.Width * 4;

        for (var x = 0; x < source.Width; x++)
        {
            var index = x * 4;
            var alpha = pixels[rowStart + index + 3];
            var factor = alpha / 255f;

            premultiplied[index] = pixels[rowStart + index] * factor;
            premultiplied[index + 1] = pixels[rowStart + index + 1] * factor;
            premultiplied[index + 2] = pixels[rowStart + index + 2] * factor;
            premultiplied[index + 3] = alpha;
        }

        var width = columns.Starts.Length;

        for (var x = 0; x < width; x++)
        {
            var start = columns.Starts[x];
            var count = columns.Counts[x];
            var offset = columns.Offsets[x];
            float red = 0, green = 0, blue = 0, alpha = 0;

            for (var tap = 0; tap < count; tap++)
            {
                var weight = columns.Weights[offset + tap];
                var index = (start + tap) * 4;

                red += premultiplied[index] * weight;
                green += premultiplied[index + 1] * weight;
                blue += premultiplied[index + 2] * weight;
                alpha += premultiplied[index + 3] * weight;
            }

            var target = x * 4;
            output[target] = red;
            output[target + 1] = green;
            output[target + 2] = blue;
            output[target + 3] = alpha;
        }
    }

    private static int Scale(int length, double ratio) => Math.Max(1, (int)Math.Round(length * ratio));

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
}
