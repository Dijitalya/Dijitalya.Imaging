namespace Dijitalya.Imaging.Processing;

/// <summary>
/// For every pixel of one axis of the resized picture: the run of source pixels it is made of and
/// their weights. The kernel is bicubic (Catmull-Rom, a = -0.5) and is widened by the scale when
/// shrinking, so every source pixel contributes; near the edges the window is cut to the picture
/// and the remaining weights are normalised to 1.
/// </summary>
internal sealed class ResamplingKernelMap
{
    private const double Radius = 2.0;

    private ResamplingKernelMap(int[] starts, int[] counts, int[] offsets, float[] weights, int maxCount)
    {
        Starts = starts;
        Counts = counts;
        Offsets = offsets;
        Weights = weights;
        MaxCount = maxCount;
    }

    /// <summary>The first source pixel of each destination pixel.</summary>
    public int[] Starts { get; }

    /// <summary>How many consecutive source pixels each destination pixel reads.</summary>
    public int[] Counts { get; }

    /// <summary>Where the weights of each destination pixel begin in <see cref="Weights"/>.</summary>
    public int[] Offsets { get; }

    public float[] Weights { get; }

    /// <summary>The longest run of source pixels any destination pixel reads.</summary>
    public int MaxCount { get; }

    public static ResamplingKernelMap Create(int sourceLength, int destinationLength)
    {
        var scale = (double)sourceLength / destinationLength;
        var filterScale = Math.Max(1.0, scale);
        var support = Radius * filterScale;

        var starts = new int[destinationLength];
        var counts = new int[destinationLength];
        var offsets = new int[destinationLength];
        var weights = new List<float>(destinationLength * ((int)Math.Ceiling(support * 2) + 1));
        var maxCount = 1;

        for (var destination = 0; destination < destinationLength; destination++)
        {
            var center = ((destination + 0.5) * scale) - 0.5;
            var left = Math.Max(0, (int)Math.Ceiling(center - support));
            var right = Math.Min(sourceLength - 1, (int)Math.Floor(center + support));

            if (right < left)
            {
                left = right = Math.Clamp((int)Math.Round(center), 0, sourceLength - 1);
            }

            var first = weights.Count;
            var sum = 0.0;

            for (var source = left; source <= right; source++)
            {
                var weight = Kernel((source - center) / filterScale);
                weights.Add((float)weight);
                sum += weight;
            }

            if (Math.Abs(sum) < 1e-9)
            {
                // Only possible at an extreme edge; the nearest pixel stands in.
                for (var index = first; index < weights.Count; index++)
                    weights[index] = 0;

                var nearest = Math.Clamp((int)Math.Round(center), left, right);
                weights[first + (nearest - left)] = 1;
            }
            else
            {
                for (var index = first; index < weights.Count; index++)
                    weights[index] = (float)(weights[index] / sum);
            }

            starts[destination] = left;
            counts[destination] = right - left + 1;
            offsets[destination] = first;
            maxCount = Math.Max(maxCount, right - left + 1);
        }

        return new ResamplingKernelMap(starts, counts, offsets, [.. weights], maxCount);
    }

    private static double Kernel(double x)
    {
        x = Math.Abs(x);

        if (x < 1.0)
            return (((1.5 * x) - 2.5) * x * x) + 1.0;

        if (x < 2.0)
            return (((((-0.5 * x) + 2.5) * x) - 4.0) * x) + 2.0;

        return 0.0;
    }
}
