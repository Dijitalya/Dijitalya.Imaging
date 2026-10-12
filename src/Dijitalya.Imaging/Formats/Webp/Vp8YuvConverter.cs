namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Turns the decoded Y, U and V planes of a lossy WebP into RGBA the way libwebp does by default:
/// chroma is upsampled with its "fancy" filter (weights 9-3-3-1 between the four nearest chroma
/// samples) and converted with its fixed-point BT.601 formulas, so the pixels match libwebp's.
/// </summary>
internal static class Vp8YuvConverter
{
    public static void ToRgba(byte[] luma, int lumaStride, byte[] u, byte[] v, int chromaStride, int width, int height, byte[] rgba)
    {
        // The first row mirrors its chroma row; then rows go in pairs between two chroma rows.
        UpsampleLinePair(luma, 0, -1, u, v, 0, 0, rgba, 0, -1, width);

        for (var y = 1; y + 1 < height; y += 2)
        {
            DecodeContext.Checkpoint();
            var topChroma = ((y - 1) >> 1) * chromaStride;
            var bottomChroma = ((y + 1) >> 1) * chromaStride;
            UpsampleLinePair(luma, y * lumaStride, (y + 1) * lumaStride, u, v, topChroma, bottomChroma, rgba, y * width * 4, (y + 1) * width * 4, width);
        }

        // An even height leaves the last row, which mirrors its chroma row too.
        if ((height & 1) == 0 && height > 1)
        {
            var chroma = ((height - 1) >> 1) * chromaStride;
            UpsampleLinePair(luma, (height - 1) * lumaStride, -1, u, v, chroma, chroma, rgba, (height - 1) * width * 4, -1, width);
        }
    }

    // Writes the row of luma 'top' between chroma rows 'topChroma' (nearer) and 'bottomChroma',
    // and, when 'bottom' is not -1, the row of luma 'bottom' with the chroma rows the other way round.
    private static void UpsampleLinePair(
        byte[] luma, int top, int bottom, byte[] u, byte[] v, int topChroma, int bottomChroma,
        byte[] rgba, int topTarget, int bottomTarget, int length)
    {
        var lastPair = (length - 1) >> 1;
        int topLeftU = u[topChroma], topLeftV = v[topChroma];
        int leftU = u[bottomChroma], leftV = v[bottomChroma];

        Write(rgba, topTarget, luma[top], ((3 * topLeftU) + leftU + 2) >> 2, ((3 * topLeftV) + leftV + 2) >> 2);

        if (bottom >= 0)
            Write(rgba, bottomTarget, luma[bottom], ((3 * leftU) + topLeftU + 2) >> 2, ((3 * leftV) + topLeftV + 2) >> 2);

        for (var x = 1; x <= lastPair; x++)
        {
            int topU = u[topChroma + x], topV = v[topChroma + x];
            int currentU = u[bottomChroma + x], currentV = v[bottomChroma + x];

            var averageU = topLeftU + topU + leftU + currentU + 8;
            var averageV = topLeftV + topV + leftV + currentV + 8;
            var diagonal12U = (averageU + (2 * (topU + leftU))) >> 3;
            var diagonal12V = (averageV + (2 * (topV + leftV))) >> 3;
            var diagonal03U = (averageU + (2 * (topLeftU + currentU))) >> 3;
            var diagonal03V = (averageV + (2 * (topLeftV + currentV))) >> 3;

            Write(rgba, topTarget + (((2 * x) - 1) * 4), luma[top + (2 * x) - 1], (diagonal12U + topLeftU) >> 1, (diagonal12V + topLeftV) >> 1);
            Write(rgba, topTarget + (2 * x * 4), luma[top + (2 * x)], (diagonal03U + topU) >> 1, (diagonal03V + topV) >> 1);

            if (bottom >= 0)
            {
                Write(rgba, bottomTarget + (((2 * x) - 1) * 4), luma[bottom + (2 * x) - 1], (diagonal03U + leftU) >> 1, (diagonal03V + leftV) >> 1);
                Write(rgba, bottomTarget + (2 * x * 4), luma[bottom + (2 * x)], (diagonal12U + currentU) >> 1, (diagonal12V + currentV) >> 1);
            }

            topLeftU = topU;
            topLeftV = topV;
            leftU = currentU;
            leftV = currentV;
        }

        if ((length & 1) == 0)
        {
            Write(rgba, topTarget + ((length - 1) * 4), luma[top + length - 1], ((3 * topLeftU) + leftU + 2) >> 2, ((3 * topLeftV) + leftV + 2) >> 2);

            if (bottom >= 0)
                Write(rgba, bottomTarget + ((length - 1) * 4), luma[bottom + length - 1], ((3 * leftU) + topLeftU + 2) >> 2, ((3 * leftV) + topLeftV + 2) >> 2);
        }
    }

    private static void Write(byte[] rgba, int target, int y, int u, int v)
    {
        rgba[target] = Clip8(MultiplyHigh(y, 19077) + MultiplyHigh(v, 26149) - 14234);
        rgba[target + 1] = Clip8(MultiplyHigh(y, 19077) - MultiplyHigh(u, 6419) - MultiplyHigh(v, 13320) + 8708);
        rgba[target + 2] = Clip8(MultiplyHigh(y, 19077) + MultiplyHigh(u, 33050) - 17685);
        rgba[target + 3] = 255;
    }

    private static int MultiplyHigh(int value, int coefficient) => (value * coefficient) >> 8;

    // The values carry 6 fractional bits.
    private static byte Clip8(int value) => (value & ~16383) == 0 ? (byte)(value >> 6) : value < 0 ? (byte)0 : (byte)255;
}
