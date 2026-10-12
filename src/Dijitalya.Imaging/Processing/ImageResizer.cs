namespace Dijitalya.Imaging.Processing;

/// <summary>
/// Bicubic resizing. Colours are weighted by their alpha while they are mixed (premultiplied), so a
/// transparent pixel's colour never bleeds into its neighbours as a dark or coloured fringe.
/// </summary>
public static class ImageResizer
{
    // Scratch storage is bounded independently of the source and target aspect ratios.
    private const int MaxRingBytes = 16 * 1024 * 1024;

    /// <summary>A copy resized to exactly the requested dimensions.</summary>
    public static RasterImage Resize(RasterImage source, int width, int height) => Resize(source, width, height, default);

    /// <summary>A copy resized to the requested dimensions, with cooperative cancellation.</summary>
    public static RasterImage Resize(RasterImage source, int width, int height, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ImageLimits.EnsureSize(width, height);
        cancellationToken.ThrowIfCancellationRequested();

        if (width == source.Width && height == source.Height)
            return source.Clone();

        return ResizeCore(source, width, height,
            ResamplingKernelMap.Create(source.Width, width),
            ResamplingKernelMap.Create(source.Height, height), cancellationToken);
    }

    private static RasterImage ResizeCore(RasterImage source, int width, int height,
        ResamplingKernelMap columns, ResamplingKernelMap rows, CancellationToken cancellationToken)
    {
        // Apply the cheaper axis first. This avoids a huge horizontal intermediate when a thin
        // portrait is stretched into a thin landscape. Both orders retain premultiplied alpha.
        var verticalFirst = (long)height * source.Width < (long)width * source.Height;
        var first = verticalFirst ? rows : columns;
        var second = verticalFirst ? columns : rows;
        var lineLength = verticalFirst ? height : width;
        var lineCount = verticalFirst ? width : height;
        var sourceLength = verticalFirst ? source.Height : source.Width;
        var stride = checked(lineLength * 4);
        var ringSize = Math.Min(second.MaxCount, Math.Max(1, MaxRingBytes / sizeof(float) / stride));
        var pool = System.Buffers.ArrayPool<float>.Shared;
        float[]? ring = null, premultiplied = null, accumulator = null;

        try
        {
            ring = pool.Rent(checked(ringSize * stride));
            premultiplied = pool.Rent(checked(sourceLength * 4));
            accumulator = pool.Rent(stride);
            var ringRows = new int[ringSize];
            Array.Fill(ringRows, -1);
            var destination = new RasterImage(width, height);
            var target = destination.Pixels;

            for (var line = 0; line < lineCount; line++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = second.Starts[line];
                var count = second.Counts[line];
                var offset = second.Offsets[line];
                accumulator.AsSpan(0, stride).Clear();

                for (var tap = 0; tap < count; tap++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceLine = start + tap;
                    var slot = sourceLine % ringSize;
                    var row = ring.AsSpan(slot * stride, stride);

                    if (ringRows[slot] != sourceLine)
                    {
                        ResizeLine(source, sourceLine, first, verticalFirst, premultiplied, row);
                        ringRows[slot] = sourceLine;
                    }

                    AddWeighted(row, accumulator.AsSpan(0, stride), second.Weights[offset + tap]);
                }

                for (var pixel = 0; pixel < lineLength; pixel++)
                {
                    var index = pixel * 4;
                    var alpha = accumulator[index + 3];
                    var output = verticalFirst ? ((pixel * width) + line) * 4 : (line * stride) + index;
                    if (alpha <= 0.5f)
                        continue;

                    var factor = 255f / alpha;
                    target[output] = ToByte(accumulator[index] * factor);
                    target[output + 1] = ToByte(accumulator[index + 1] * factor);
                    target[output + 2] = ToByte(accumulator[index + 2] * factor);
                    target[output + 3] = ToByte(alpha);
                }
            }

            return destination;
        }
        finally
        {
            // Clear pooled pixel data so another pool consumer cannot observe this image.
            if (ring is not null) pool.Return(ring, clearArray: true);
            if (premultiplied is not null) pool.Return(premultiplied, clearArray: true);
            if (accumulator is not null) pool.Return(accumulator, clearArray: true);
        }
    }

    private static void AddWeighted(ReadOnlySpan<float> row, Span<float> accumulator, float weight)
    {
        var index = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            var weights = new System.Numerics.Vector<float>(weight);
            var count = System.Numerics.Vector<float>.Count;
            for (; index <= row.Length - count; index += count)
            {
                var values = new System.Numerics.Vector<float>(row.Slice(index, count));
                var sum = new System.Numerics.Vector<float>(accumulator.Slice(index, count));
                (sum + values * weights).CopyTo(accumulator.Slice(index, count));
            }
        }

        for (; index < row.Length; index++)
            accumulator[index] += row[index] * weight;
    }

    /// <summary>
    /// A copy of the picture fitted into a box as <paramref name="mode"/> says. With
    /// <see cref="ResizeMode.Max"/> a width or height of 0 is worked out from the other, and both 0
    /// leave the size as it is.
    /// </summary>
    public static RasterImage ResizeToBox(RasterImage source, int width, int height, ResizeMode mode) =>
        ResizeToBox(source, width, height, mode, default);

    /// <summary>Fits a copy into a box, with cooperative cancellation.</summary>
    public static RasterImage ResizeToBox(RasterImage source, int width, int height, ResizeMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        if (mode is not (ResizeMode.Max or ResizeMode.Crop or ResizeMode.Pad))
            throw new ArgumentOutOfRangeException(nameof(mode));
        cancellationToken.ThrowIfCancellationRequested();

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

            return Resize(source, targetWidth, targetHeight, cancellationToken);
        }

        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);

        ImageLimits.EnsureSize(width, height);

        if (mode == ResizeMode.Crop)
        {
            var ratio = Math.Max((double)width / source.Width, (double)height / source.Height);
            var scaledWidth = Math.Max(width, (long)Math.Round(source.Width * ratio));
            var scaledHeight = Math.Max(height, (long)Math.Round(source.Height * ratio));
            // Evaluate only the requested window of the virtual resized image. The sampling
            // coordinates match resize-then-crop without allocating the discarded pixels.
            var columns = ResamplingKernelMap.Create(source.Width, width,
                (double)source.Width / scaledWidth, (scaledWidth - width) / 2);
            var rows = ResamplingKernelMap.Create(source.Height, height,
                (double)source.Height / scaledHeight, (scaledHeight - height) / 2);
            return ResizeCore(source, width, height, columns, rows, cancellationToken);
        }

        var fit = Math.Min((double)width / source.Width, (double)height / source.Height);
        var fitted = Resize(source, Math.Min(width, Scale(source.Width, fit)), Math.Min(height, Scale(source.Height, fit)), cancellationToken);
        var canvas = new RasterImage(width, height);

        ImageTransforms.Paste(fitted, canvas, (width - fitted.Width) / 2, (height - fitted.Height) / 2);

        return canvas;
    }

    private static void ResizeLine(RasterImage source, int sourceLine, ResamplingKernelMap columns, bool vertical, float[] premultiplied, Span<float> output)
    {
        var pixels = source.Pixels;
        var sourceLength = vertical ? source.Height : source.Width;

        for (var x = 0; x < sourceLength; x++)
        {
            var index = x * 4;
            var input = vertical ? ((x * source.Width) + sourceLine) * 4 : ((sourceLine * source.Width) + x) * 4;
            var alpha = pixels[input + 3];
            var factor = alpha / 255f;

            premultiplied[index] = pixels[input] * factor;
            premultiplied[index + 1] = pixels[input + 1] * factor;
            premultiplied[index + 2] = pixels[input + 2] * factor;
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

    private static int Scale(int length, double ratio)
    {
        var scaled = Math.Max(1, Math.Round(length * ratio));
        if (scaled > ImageLimits.MaxDimension)
            throw new InvalidImageContentException("The resized dimension exceeds the supported limit.");
        return (int)scaled;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
}
