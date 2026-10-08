namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>The forward DCT of one 8x8 block and its quantisation, in floating point.</summary>
internal static class JpegForwardDct
{
    /// <param name="samples">64 samples, row by row, already shifted to be centred on 0.</param>
    /// <param name="quantization">The quantisation table, natural order.</param>
    /// <param name="coefficients">Receives the 64 quantised coefficients, natural order.</param>
    /// <param name="workspace">64 floats of scratch space.</param>
    public static void Transform(float[] samples, int[] quantization, int[] coefficients, float[] workspace)
    {
        var basis = JpegTables.Basis;

        for (var y = 0; y < 8; y++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0f;

                for (var x = 0; x < 8; x++)
                    sum += samples[(y * 8) + x] * basis[(u * 8) + x];

                workspace[(y * 8) + u] = sum;
            }
        }

        for (var u = 0; u < 8; u++)
        {
            for (var v = 0; v < 8; v++)
            {
                var sum = 0f;

                for (var y = 0; y < 8; y++)
                    sum += workspace[(y * 8) + u] * basis[(v * 8) + y];

                var index = (v * 8) + u;
                coefficients[index] = (int)MathF.Round(sum / quantization[index]);
            }
        }
    }
}
