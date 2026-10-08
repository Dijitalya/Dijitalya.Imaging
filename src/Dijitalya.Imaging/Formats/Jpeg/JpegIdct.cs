namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>
/// Dequantisation and the inverse DCT of one 8x8 block, done separably (rows, then columns) in
/// floating point. A row whose AC coefficients are all zero, which most rows of a photograph are,
/// takes a shortcut: its result is a constant.
/// </summary>
internal static class JpegIdct
{
    /// <param name="coefficients">The coefficients; the block is the 64 values at <paramref name="offset"/>, in natural order.</param>
    /// <param name="offset">Where the block starts in <paramref name="coefficients"/>.</param>
    /// <param name="quantization">The quantisation table of the component, natural order.</param>
    /// <param name="output">The samples; the block is written at <paramref name="outputOffset"/>, <paramref name="stride"/> bytes per row.</param>
    /// <param name="outputOffset">Where the top-left sample of the block goes in <paramref name="output"/>.</param>
    /// <param name="stride">The bytes per row of <paramref name="output"/>.</param>
    /// <param name="workspace">64 floats of scratch space.</param>
    public static void Transform(short[] coefficients, int offset, int[] quantization, byte[] output, int outputOffset, int stride, float[] workspace)
    {
        var basis = JpegTables.Basis;

        for (var row = 0; row < 8; row++)
        {
            var source = offset + (row * 8);
            var dc = coefficients[source] * quantization[row * 8];
            var hasAc = false;

            for (var column = 1; column < 8; column++)
            {
                if (coefficients[source + column] != 0)
                {
                    hasAc = true;
                    break;
                }
            }

            if (!hasAc)
            {
                var value = dc * basis[0];

                for (var x = 0; x < 8; x++)
                    workspace[(row * 8) + x] = value;

                continue;
            }

            for (var x = 0; x < 8; x++)
            {
                var sum = 0f;

                for (var u = 0; u < 8; u++)
                {
                    var coefficient = coefficients[source + u];

                    if (coefficient != 0)
                        sum += coefficient * quantization[(row * 8) + u] * basis[(u * 8) + x];
                }

                workspace[(row * 8) + x] = sum;
            }
        }

        for (var x = 0; x < 8; x++)
        {
            for (var y = 0; y < 8; y++)
            {
                var sum = 0f;

                for (var v = 0; v < 8; v++)
                    sum += workspace[(v * 8) + x] * basis[(v * 8) + y];

                var sample = (int)MathF.Round(sum + 128f);
                output[outputOffset + (y * stride) + x] = (byte)Math.Clamp(sample, 0, 255);
            }
        }
    }
}
