namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// The transforms of VP8 (RFC 6386 section 14) in libwebp's integer form: the 4x4 DCT and the
/// Walsh-Hadamard transform of the second-order luma DCs, forward for the encoder and inverse for
/// both. Pixels live in a work buffer whose rows are <see cref="Vp8Predictor.Stride"/> bytes apart.
/// </summary>
internal static class Vp8Transform
{
    private const int Stride = Vp8Predictor.Stride;

    /// <summary>Adds a luma block's residual by the kind its coefficient code (the top two bits) names.</summary>
    public static void AddLuma(uint code, short[] coefficients, int coefficientOffset, byte[] buffer, int offset)
    {
        switch (code >> 30)
        {
            case 3:
            case 2:
                // libwebp has a shortcut for two AC coefficients; it gives the same result as the full transform.
                AddFull(coefficients, coefficientOffset, buffer, offset);
                break;

            case 1:
                AddDcOnly(coefficients[coefficientOffset], buffer, offset);
                break;
        }
    }

    /// <summary>Adds the residual of the four blocks of one chroma plane, given its 8-bit coefficient code.</summary>
    public static void AddChroma(uint code, short[] coefficients, int coefficientOffset, byte[] buffer, int offset)
    {
        if ((code & 0xFF) == 0)
            return;

        if ((code & 0xAA) != 0)
        {
            AddFull(coefficients, coefficientOffset, buffer, offset);
            AddFull(coefficients, coefficientOffset + 16, buffer, offset + 4);
            AddFull(coefficients, coefficientOffset + 32, buffer, offset + (4 * Stride));
            AddFull(coefficients, coefficientOffset + 48, buffer, offset + (4 * Stride) + 4);

            return;
        }

        for (var block = 0; block < 4; block++)
        {
            var dc = coefficients[coefficientOffset + (block * 16)];

            if (dc != 0)
                AddDcOnly(dc, buffer, offset + ((block >> 1) * 4 * Stride) + ((block & 1) * 4));
        }
    }

    /// <summary>
    /// Undoes the Walsh-Hadamard transform of the 16 second-order coefficients and stores the result
    /// as the DC coefficient of each of the 16 luma blocks.
    /// </summary>
    public static void InverseWalshHadamard(ReadOnlySpan<short> input, short[] coefficients)
    {
        Span<int> temp = stackalloc int[16];

        for (var i = 0; i < 4; i++)
        {
            var a0 = input[i] + input[12 + i];
            var a1 = input[4 + i] + input[8 + i];
            var a2 = input[4 + i] - input[8 + i];
            var a3 = input[i] - input[12 + i];
            temp[i] = a0 + a1;
            temp[8 + i] = a0 - a1;
            temp[4 + i] = a3 + a2;
            temp[12 + i] = a3 - a2;
        }

        var output = 0;

        for (var i = 0; i < 4; i++)
        {
            var dc = temp[i * 4] + 3;
            var a0 = dc + temp[3 + (i * 4)];
            var a1 = temp[1 + (i * 4)] + temp[2 + (i * 4)];
            var a2 = temp[1 + (i * 4)] - temp[2 + (i * 4)];
            var a3 = dc - temp[3 + (i * 4)];
            coefficients[output] = (short)((a0 + a1) >> 3);
            coefficients[output + 16] = (short)((a3 + a2) >> 3);
            coefficients[output + 32] = (short)((a0 - a1) >> 3);
            coefficients[output + 48] = (short)((a3 - a2) >> 3);
            output += 64;
        }
    }

    /// <summary>Transforms the residual of a 4x4 block (source minus prediction) into 16 coefficients.</summary>
    public static void Forward(byte[] source, int sourceOffset, byte[] prediction, int predictionOffset, short[] output, int outputOffset)
    {
        Span<int> temp = stackalloc int[16];

        for (var i = 0; i < 4; i++)
        {
            var s = sourceOffset + (i * Stride);
            var r = predictionOffset + (i * Stride);
            var d0 = source[s] - prediction[r];
            var d1 = source[s + 1] - prediction[r + 1];
            var d2 = source[s + 2] - prediction[r + 2];
            var d3 = source[s + 3] - prediction[r + 3];
            var a0 = d0 + d3;
            var a1 = d1 + d2;
            var a2 = d1 - d2;
            var a3 = d0 - d3;
            temp[(i * 4) + 0] = (a0 + a1) * 8;
            temp[(i * 4) + 1] = ((a2 * 2217) + (a3 * 5352) + 1812) >> 9;
            temp[(i * 4) + 2] = (a0 - a1) * 8;
            temp[(i * 4) + 3] = ((a3 * 2217) - (a2 * 5352) + 937) >> 9;
        }

        for (var i = 0; i < 4; i++)
        {
            var a0 = temp[i] + temp[12 + i];
            var a1 = temp[4 + i] + temp[8 + i];
            var a2 = temp[4 + i] - temp[8 + i];
            var a3 = temp[i] - temp[12 + i];
            output[outputOffset + i] = (short)((a0 + a1 + 7) >> 4);
            output[outputOffset + 4 + i] = (short)((((a2 * 2217) + (a3 * 5352) + 12000) >> 16) + (a3 != 0 ? 1 : 0));
            output[outputOffset + 8 + i] = (short)((a0 - a1 + 7) >> 4);
            output[outputOffset + 12 + i] = (short)(((a3 * 2217) - (a2 * 5352) + 51000) >> 16);
        }
    }

    /// <summary>
    /// The forward Walsh-Hadamard transform of the DC coefficients of the 16 luma blocks, which are
    /// at every 16th position of <paramref name="coefficients"/>.
    /// </summary>
    public static void ForwardWalshHadamard(short[] coefficients, Span<short> output)
    {
        Span<int> temp = stackalloc int[16];

        for (var i = 0; i < 4; i++)
        {
            var row = i * 64;
            var a0 = coefficients[row] + coefficients[row + 32];
            var a1 = coefficients[row + 16] + coefficients[row + 48];
            var a2 = coefficients[row + 16] - coefficients[row + 48];
            var a3 = coefficients[row] - coefficients[row + 32];
            temp[(i * 4) + 0] = a0 + a1;
            temp[(i * 4) + 1] = a3 + a2;
            temp[(i * 4) + 2] = a3 - a2;
            temp[(i * 4) + 3] = a0 - a1;
        }

        for (var i = 0; i < 4; i++)
        {
            var a0 = temp[i] + temp[8 + i];
            var a1 = temp[4 + i] + temp[12 + i];
            var a2 = temp[4 + i] - temp[12 + i];
            var a3 = temp[i] - temp[8 + i];
            output[i] = (short)((a0 + a1) >> 1);
            output[4 + i] = (short)((a3 + a2) >> 1);
            output[8 + i] = (short)((a3 - a2) >> 1);
            output[12 + i] = (short)((a0 - a1) >> 1);
        }
    }

    /// <summary>Adds the inverse transform of 16 dequantised coefficients onto a predicted block.</summary>
    public static void AddFull(short[] input, int inputOffset, byte[] buffer, int offset)
    {
        Span<int> temp = stackalloc int[16];

        for (var i = 0; i < 4; i++)
        {
            var in0 = input[inputOffset + i];
            var in4 = input[inputOffset + 4 + i];
            var in8 = input[inputOffset + 8 + i];
            var in12 = input[inputOffset + 12 + i];
            var a = in0 + in8;
            var b = in0 - in8;
            var c = Mul2(in4) - Mul1(in12);
            var d = Mul1(in4) + Mul2(in12);
            temp[(i * 4) + 0] = a + d;
            temp[(i * 4) + 1] = b + c;
            temp[(i * 4) + 2] = b - c;
            temp[(i * 4) + 3] = a - d;
        }

        for (var i = 0; i < 4; i++)
        {
            var dc = temp[i] + 4;
            var a = dc + temp[8 + i];
            var b = dc - temp[8 + i];
            var c = Mul2(temp[4 + i]) - Mul1(temp[12 + i]);
            var d = Mul1(temp[4 + i]) + Mul2(temp[12 + i]);
            var row = offset + (i * Stride);
            Store(buffer, row, a + d);
            Store(buffer, row + 1, b + c);
            Store(buffer, row + 2, b - c);
            Store(buffer, row + 3, a - d);
        }
    }

    private static void AddDcOnly(int dc, byte[] buffer, int offset)
    {
        var value = dc + 4;

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
                Store(buffer, offset + (y * Stride) + x, value);
        }
    }

    private static void Store(byte[] buffer, int index, int value) =>
        buffer[index] = ClipToByte(buffer[index] + (value >> 3));

    private static byte ClipToByte(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;

    private static int Mul1(int value) => ((value * 20091) >> 16) + value;

    private static int Mul2(int value) => (value * 35468) >> 16;
}
