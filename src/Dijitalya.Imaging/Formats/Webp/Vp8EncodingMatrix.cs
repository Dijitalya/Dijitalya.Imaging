namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// How the VP8 encoder quantises one kind of block (luma, second-order luma or chroma): per
/// coefficient position the step, its fixed-point inverse, the rounding bias, the threshold under
/// which a coefficient becomes zero and a slight boost of high frequencies, as libwebp sets them up.
/// </summary>
internal sealed class Vp8EncodingMatrix
{
    private const int FixBits = 17;
    private const int SharpenBits = 11;
    private const int MaxLevel = 2047;

    private static readonly int[] FrequencySharpening = [0, 30, 60, 90, 30, 60, 90, 90, 60, 90, 90, 90, 90, 90, 90, 90];

    private readonly int[] _step = new int[16];
    private readonly int[] _inverse = new int[16];
    private readonly int[] _bias = new int[16];
    private readonly int[] _zeroThreshold = new int[16];
    private readonly int[] _sharpen = new int[16];

    /// <param name="dcStep">The quantiser step of the DC coefficient.</param>
    /// <param name="acStep">The quantiser step of the other coefficients.</param>
    /// <param name="dcBias">Rounding bias of the DC coefficient, in 1/256.</param>
    /// <param name="acBias">Rounding bias of the other coefficients, in 1/256.</param>
    /// <param name="sharpen">Whether high frequencies are slightly boosted (luma only).</param>
    public Vp8EncodingMatrix(int dcStep, int acStep, int dcBias, int acBias, bool sharpen)
    {
        var sum = 0;

        for (var i = 0; i < 16; i++)
        {
            var step = i == 0 ? dcStep : acStep;
            _step[i] = step;
            _inverse[i] = (1 << FixBits) / step;
            _bias[i] = (i == 0 ? dcBias : acBias) << (FixBits - 8);
            _zeroThreshold[i] = ((1 << FixBits) - 1 - _bias[i]) / _inverse[i];
            _sharpen[i] = sharpen ? (FrequencySharpening[i] * step) >> SharpenBits : 0;
            sum += step;
        }

        AverageStep = (sum + 8) >> 4;
    }

    /// <summary>The mean quantiser step over the 16 positions, which sets the encoder's penalties.</summary>
    public int AverageStep { get; }

    /// <summary>
    /// Quantises the 16 coefficients of <paramref name="block"/> (raster order) in place, leaving the
    /// dequantised values there, and stores the levels in zigzag order in <paramref name="levels"/>.
    /// Returns the zigzag index of the last non-zero level, or -1.
    /// </summary>
    public int Quantize(Span<short> block, Span<short> levels)
    {
        var last = -1;

        for (var n = 0; n < 16; n++)
        {
            var j = Vp8Tables.ZigZag[n];
            int value = block[j];
            var negative = value < 0;
            var coefficient = (negative ? -value : value) + _sharpen[j];

            if (coefficient > _zeroThreshold[j])
            {
                var level = (int)((((long)coefficient * _inverse[j]) + _bias[j]) >> FixBits);

                if (level > MaxLevel)
                    level = MaxLevel;

                if (negative)
                    level = -level;

                block[j] = (short)(level * _step[j]);
                levels[n] = (short)level;

                if (level != 0)
                    last = n;
            }
            else
            {
                block[j] = 0;
                levels[n] = 0;
            }
        }

        return last;
    }
}
