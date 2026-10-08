namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// The loop filters of VP8 (RFC 6386 section 15) in libwebp's form: the simple filter, which only
/// touches luma, and the normal filter with its macroblock-edge and inner-edge variants. They work in
/// place on a plane; "step" is the distance between the pixels across the edge and "advance" the
/// distance between successive filtered positions along it.
/// </summary>
internal static class Vp8LoopFilter
{
    /// <summary>Filters one macroblock of the luma plane (and, for the normal filter, of both chroma planes).</summary>
    public static void FilterMacroblock(
        bool simple, Vp8FilterStrength strength, bool inner, bool hasLeft, bool hasTop,
        byte[] luma, int lumaOffset, int lumaStride,
        byte[] u, byte[] v, int chromaOffset, int chromaStride)
    {
        var limit = strength.Limit;

        if (limit == 0)
            return;

        if (simple)
        {
            if (hasLeft)
                SimpleFilter(luma, lumaOffset, 1, lumaStride, 16, limit + 4);

            if (inner)
            {
                for (var edge = 4; edge < 16; edge += 4)
                    SimpleFilter(luma, lumaOffset + edge, 1, lumaStride, 16, limit);
            }

            if (hasTop)
                SimpleFilter(luma, lumaOffset, lumaStride, 1, 16, limit + 4);

            if (inner)
            {
                for (var edge = 4; edge < 16; edge += 4)
                    SimpleFilter(luma, lumaOffset + (edge * lumaStride), lumaStride, 1, 16, limit);
            }

            return;
        }

        var innerLevel = strength.InnerLevel;
        var hev = strength.HighEdgeVarianceThreshold;

        if (hasLeft)
        {
            EdgeFilter(luma, lumaOffset, 1, lumaStride, 16, limit + 4, innerLevel, hev);
            EdgeFilter(u, chromaOffset, 1, chromaStride, 8, limit + 4, innerLevel, hev);
            EdgeFilter(v, chromaOffset, 1, chromaStride, 8, limit + 4, innerLevel, hev);
        }

        if (inner)
        {
            for (var edge = 4; edge < 16; edge += 4)
                InnerFilter(luma, lumaOffset + edge, 1, lumaStride, 16, limit, innerLevel, hev);

            InnerFilter(u, chromaOffset + 4, 1, chromaStride, 8, limit, innerLevel, hev);
            InnerFilter(v, chromaOffset + 4, 1, chromaStride, 8, limit, innerLevel, hev);
        }

        if (hasTop)
        {
            EdgeFilter(luma, lumaOffset, lumaStride, 1, 16, limit + 4, innerLevel, hev);
            EdgeFilter(u, chromaOffset, chromaStride, 1, 8, limit + 4, innerLevel, hev);
            EdgeFilter(v, chromaOffset, chromaStride, 1, 8, limit + 4, innerLevel, hev);
        }

        if (inner)
        {
            for (var edge = 4; edge < 16; edge += 4)
                InnerFilter(luma, lumaOffset + (edge * lumaStride), lumaStride, 1, 16, limit, innerLevel, hev);

            InnerFilter(u, chromaOffset + (4 * chromaStride), chromaStride, 1, 8, limit, innerLevel, hev);
            InnerFilter(v, chromaOffset + (4 * chromaStride), chromaStride, 1, 8, limit, innerLevel, hev);
        }
    }

    private static void SimpleFilter(byte[] plane, int offset, int step, int advance, int count, int threshold)
    {
        var threshold2 = (2 * threshold) + 1;

        for (var i = 0; i < count; i++)
        {
            var position = offset + (i * advance);

            if (NeedsFilter(plane, position, step, threshold2))
                Filter2(plane, position, step);
        }
    }

    // Macroblock edges: up to three pixels on each side change.
    private static void EdgeFilter(byte[] plane, int offset, int step, int advance, int count, int threshold, int innerLevel, int hevThreshold)
    {
        var threshold2 = (2 * threshold) + 1;

        for (var i = 0; i < count; i++)
        {
            var position = offset + (i * advance);

            if (!NeedsFilter2(plane, position, step, threshold2, innerLevel))
                continue;

            if (HighEdgeVariance(plane, position, step, hevThreshold))
                Filter2(plane, position, step);
            else
                Filter6(plane, position, step);
        }
    }

    // Edges inside a macroblock: up to two pixels on each side change.
    private static void InnerFilter(byte[] plane, int offset, int step, int advance, int count, int threshold, int innerLevel, int hevThreshold)
    {
        var threshold2 = (2 * threshold) + 1;

        for (var i = 0; i < count; i++)
        {
            var position = offset + (i * advance);

            if (!NeedsFilter2(plane, position, step, threshold2, innerLevel))
                continue;

            if (HighEdgeVariance(plane, position, step, hevThreshold))
                Filter2(plane, position, step);
            else
                Filter4(plane, position, step);
        }
    }

    private static void Filter2(byte[] p, int position, int step)
    {
        int p1 = p[position - (2 * step)];
        int p0 = p[position - step];
        int q0 = p[position];
        int q1 = p[position + step];
        var a = (3 * (q0 - p0)) + SignedClip1(p1 - q1);
        var a1 = SignedClip2((a + 4) >> 3);
        var a2 = SignedClip2((a + 3) >> 3);
        p[position - step] = Clip1(p0 + a2);
        p[position] = Clip1(q0 - a1);
    }

    private static void Filter4(byte[] p, int position, int step)
    {
        int p1 = p[position - (2 * step)];
        int p0 = p[position - step];
        int q0 = p[position];
        int q1 = p[position + step];
        var a = 3 * (q0 - p0);
        var a1 = SignedClip2((a + 4) >> 3);
        var a2 = SignedClip2((a + 3) >> 3);
        var a3 = (a1 + 1) >> 1;
        p[position - (2 * step)] = Clip1(p1 + a3);
        p[position - step] = Clip1(p0 + a2);
        p[position] = Clip1(q0 - a1);
        p[position + step] = Clip1(q1 - a3);
    }

    private static void Filter6(byte[] p, int position, int step)
    {
        int p2 = p[position - (3 * step)];
        int p1 = p[position - (2 * step)];
        int p0 = p[position - step];
        int q0 = p[position];
        int q1 = p[position + step];
        int q2 = p[position + (2 * step)];
        var a = SignedClip1((3 * (q0 - p0)) + SignedClip1(p1 - q1));
        var a1 = ((27 * a) + 63) >> 7;
        var a2 = ((18 * a) + 63) >> 7;
        var a3 = ((9 * a) + 63) >> 7;
        p[position - (3 * step)] = Clip1(p2 + a3);
        p[position - (2 * step)] = Clip1(p1 + a2);
        p[position - step] = Clip1(p0 + a1);
        p[position] = Clip1(q0 - a1);
        p[position + step] = Clip1(q1 - a2);
        p[position + (2 * step)] = Clip1(q2 - a3);
    }

    private static bool HighEdgeVariance(byte[] p, int position, int step, int threshold)
    {
        int p1 = p[position - (2 * step)];
        int p0 = p[position - step];
        int q0 = p[position];
        int q1 = p[position + step];

        return Math.Abs(p1 - p0) > threshold || Math.Abs(q1 - q0) > threshold;
    }

    private static bool NeedsFilter(byte[] p, int position, int step, int threshold)
    {
        int p1 = p[position - (2 * step)];
        int p0 = p[position - step];
        int q0 = p[position];
        int q1 = p[position + step];

        return (4 * Math.Abs(p0 - q0)) + Math.Abs(p1 - q1) <= threshold;
    }

    private static bool NeedsFilter2(byte[] p, int position, int step, int threshold, int innerLevel)
    {
        int p3 = p[position - (4 * step)];
        int p2 = p[position - (3 * step)];
        int p1 = p[position - (2 * step)];
        int p0 = p[position - step];
        int q0 = p[position];
        int q1 = p[position + step];
        int q2 = p[position + (2 * step)];
        int q3 = p[position + (3 * step)];

        if ((4 * Math.Abs(p0 - q0)) + Math.Abs(p1 - q1) > threshold)
            return false;

        return Math.Abs(p3 - p2) <= innerLevel && Math.Abs(p2 - p1) <= innerLevel &&
               Math.Abs(p1 - p0) <= innerLevel && Math.Abs(q3 - q2) <= innerLevel &&
               Math.Abs(q2 - q1) <= innerLevel && Math.Abs(q1 - q0) <= innerLevel;
    }

    // Clamps to [-128, 127].
    private static int SignedClip1(int value) => value < -128 ? -128 : value > 127 ? 127 : value;

    // Clamps to [-16, 15].
    private static int SignedClip2(int value) => value < -16 ? -16 : value > 15 ? 15 : value;

    private static byte Clip1(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;
}
