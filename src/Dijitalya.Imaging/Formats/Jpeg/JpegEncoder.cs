namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>
/// Encodes baseline JPEG (JFIF, YCbCr) with the example tables of T.81 Annex K, the quantisation
/// tables scaled by quality as libjpeg does. Chroma is subsampled 2x2 (4:2:0) below quality 91 and
/// kept whole (4:4:4) from 91 up. JPEG has no transparency: transparent pixels are laid on white.
/// An ICC profile, when given, is embedded in APP2 segments.
/// </summary>
internal static class JpegEncoder
{
    private const int FullQualityThreshold = 91;

    // A segment holds at most 65535 bytes, including its length and the 14-byte ICC_PROFILE header.
    private const int MaxIccChunk = 65535 - 2 - 14;

    private static readonly JpegHuffmanEncoderTable DcLuminance = new(JpegTables.DcLuminanceCounts, JpegTables.DcLuminanceSymbols);
    private static readonly JpegHuffmanEncoderTable AcLuminance = new(JpegTables.AcLuminanceCounts, JpegTables.AcLuminanceSymbols);
    private static readonly JpegHuffmanEncoderTable DcChrominance = new(JpegTables.DcChrominanceCounts, JpegTables.DcChrominanceSymbols);
    private static readonly JpegHuffmanEncoderTable AcChrominance = new(JpegTables.AcChrominanceCounts, JpegTables.AcChrominanceSymbols);

    public static byte[] Encode(RasterImage image, int quality, byte[]? iccProfile = null)
    {
        var subsample = quality < FullQualityThreshold;
        var luminanceTable = ScaleTable(JpegTables.LuminanceQuantization, quality);
        var chrominanceTable = ScaleTable(JpegTables.ChrominanceQuantization, quality);
        var mcuSize = subsample ? 16 : 8;
        var paddedWidth = (image.Width + mcuSize - 1) / mcuSize * mcuSize;
        var paddedHeight = (image.Height + mcuSize - 1) / mcuSize * mcuSize;

        var (luma, blueDifference, redDifference) = ToYCbCr(image, paddedWidth, paddedHeight);
        var chromaWidth = paddedWidth;

        if (subsample)
        {
            blueDifference = Halve(blueDifference, paddedWidth, paddedHeight);
            redDifference = Halve(redDifference, paddedWidth, paddedHeight);
            chromaWidth = paddedWidth / 2;
        }

        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        WriteJfif(output);

        if (iccProfile is not null)
            WriteIccProfile(output, iccProfile);

        WriteQuantizationTables(output, luminanceTable, chrominanceTable);
        WriteFrame(output, image.Width, image.Height, subsample);
        WriteHuffmanTables(output);
        WriteScanHeader(output);

        var writer = new JpegBitWriter(output);
        var samples = new float[64];
        var coefficients = new int[64];
        var workspace = new float[64];
        int lumaPrediction = 0, blueDifferencePrediction = 0, redDifferencePrediction = 0;

        for (var mcuY = 0; mcuY < paddedHeight; mcuY += mcuSize)
        {
            for (var mcuX = 0; mcuX < paddedWidth; mcuX += mcuSize)
            {
                for (var blockY = 0; blockY < mcuSize; blockY += 8)
                {
                    for (var blockX = 0; blockX < mcuSize; blockX += 8)
                    {
                        lumaPrediction = EncodeBlock(
                            writer, luma, paddedWidth, mcuX + blockX, mcuY + blockY, luminanceTable,
                            lumaPrediction, DcLuminance, AcLuminance, samples, coefficients, workspace);
                    }
                }

                var chromaX = subsample ? mcuX / 2 : mcuX;
                var chromaY = subsample ? mcuY / 2 : mcuY;

                blueDifferencePrediction = EncodeBlock(
                    writer, blueDifference, chromaWidth, chromaX, chromaY, chrominanceTable,
                    blueDifferencePrediction, DcChrominance, AcChrominance, samples, coefficients, workspace);
                redDifferencePrediction = EncodeBlock(
                    writer, redDifference, chromaWidth, chromaX, chromaY, chrominanceTable,
                    redDifferencePrediction, DcChrominance, AcChrominance, samples, coefficients, workspace);
            }
        }

        writer.Flush();
        output.Write([0xFF, 0xD9]);

        return output.ToArray();
    }

    /// <summary>libjpeg's quality scaling: 50 keeps the example table, lower qualities enlarge it, higher ones shrink it.</summary>
    internal static int[] ScaleTable(int[] table, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        var scale = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var scaled = new int[64];

        for (var index = 0; index < 64; index++)
            scaled[index] = Math.Clamp(((table[index] * scale) + 50) / 100, 1, 255);

        return scaled;
    }

    /// <summary>The Y, Cb and Cr planes of the picture, padded to whole MCUs by repeating the last column and row.</summary>
    private static (float[] Luma, float[] BlueDifference, float[] RedDifference) ToYCbCr(RasterImage image, int paddedWidth, int paddedHeight)
    {
        var luma = new float[paddedWidth * paddedHeight];
        var blueDifference = new float[luma.Length];
        var redDifference = new float[luma.Length];
        var pixels = image.Pixels;

        for (var y = 0; y < paddedHeight; y++)
        {
            var sourceY = Math.Min(y, image.Height - 1);

            for (var x = 0; x < paddedWidth; x++)
            {
                var sourceX = Math.Min(x, image.Width - 1);
                var source = ((sourceY * image.Width) + sourceX) * 4;
                var alpha = pixels[source + 3];
                float red = pixels[source], green = pixels[source + 1], blue = pixels[source + 2];

                if (alpha != 255)
                {
                    var opacity = alpha / 255f;
                    var white = 255f * (1 - opacity);
                    red = (red * opacity) + white;
                    green = (green * opacity) + white;
                    blue = (blue * opacity) + white;
                }

                var target = (y * paddedWidth) + x;
                luma[target] = (0.299f * red) + (0.587f * green) + (0.114f * blue);
                blueDifference[target] = (-0.168736f * red) - (0.331264f * green) + (0.5f * blue) + 128f;
                redDifference[target] = (0.5f * red) - (0.418688f * green) - (0.081312f * blue) + 128f;
            }
        }

        return (luma, blueDifference, redDifference);
    }

    /// <summary>Averages every 2x2 square of a plane.</summary>
    private static float[] Halve(float[] plane, int width, int height)
    {
        var halfWidth = width / 2;
        var halved = new float[halfWidth * (height / 2)];

        for (var y = 0; y < height / 2; y++)
        {
            for (var x = 0; x < halfWidth; x++)
            {
                var source = (y * 2 * width) + (x * 2);
                halved[(y * halfWidth) + x] = (plane[source] + plane[source + 1] + plane[source + width] + plane[source + width + 1]) / 4f;
            }
        }

        return halved;
    }

    private static int EncodeBlock(
        JpegBitWriter writer, float[] plane, int width, int left, int top, int[] quantization, int prediction,
        JpegHuffmanEncoderTable dcTable, JpegHuffmanEncoderTable acTable, float[] samples, int[] coefficients, float[] workspace)
    {
        for (var y = 0; y < 8; y++)
        {
            var row = ((top + y) * width) + left;

            for (var x = 0; x < 8; x++)
                samples[(y * 8) + x] = plane[row + x] - 128f;
        }

        JpegForwardDct.Transform(samples, quantization, coefficients, workspace);

        var dc = coefficients[0];
        WriteValue(writer, dcTable, 0, dc - prediction);

        var run = 0;

        for (var index = 1; index < 64; index++)
        {
            var coefficient = coefficients[JpegTables.ZigZag[index]];

            if (coefficient == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                acTable.Write(writer, 0xF0);
                run -= 16;
            }

            WriteValue(writer, acTable, run, coefficient);
            run = 0;
        }

        if (run > 0)
            acTable.Write(writer, 0x00);

        return dc;
    }

    /// <summary>Writes a value as its size category (with a zero run before it for AC) and its extra bits (T.81 F.1.2).</summary>
    private static void WriteValue(JpegBitWriter writer, JpegHuffmanEncoderTable table, int run, int value)
    {
        var magnitude = Math.Abs(value);
        var size = 0;

        while (magnitude > 0)
        {
            size++;
            magnitude >>= 1;
        }

        table.Write(writer, (run << 4) | size);

        if (size > 0)
            writer.Write(value >= 0 ? value : value + (1 << size) - 1, size);
    }

    private static void WriteJfif(Stream output) =>
        output.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);

    private static void WriteIccProfile(Stream output, byte[] profile)
    {
        var count = (profile.Length + MaxIccChunk - 1) / MaxIccChunk;

        // The format numbers the parts in one byte; a profile that needs more is left out.
        if (count is 0 or > 255)
            return;

        for (var part = 0; part < count; part++)
        {
            var offset = part * MaxIccChunk;
            var length = Math.Min(MaxIccChunk, profile.Length - offset);
            var segmentLength = 2 + 14 + length;

            output.Write([0xFF, 0xE2, (byte)(segmentLength >> 8), (byte)segmentLength]);
            output.Write("ICC_PROFILE\0"u8);
            output.Write([(byte)(part + 1), (byte)count]);
            output.Write(profile, offset, length);
        }
    }

    private static void WriteQuantizationTables(Stream output, int[] luminance, int[] chrominance)
    {
        output.Write([0xFF, 0xDB, 0x00, 0x84]);

        foreach (var (index, table) in new[] { (0, luminance), (1, chrominance) })
        {
            output.WriteByte((byte)index);

            for (var position = 0; position < 64; position++)
                output.WriteByte((byte)table[JpegTables.ZigZag[position]]);
        }
    }

    private static void WriteFrame(Stream output, int width, int height, bool subsample)
    {
        output.Write(
        [
            0xFF, 0xC0, 0x00, 0x11, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            0x03,
            0x01, subsample ? (byte)0x22 : (byte)0x11, 0x00,
            0x02, 0x11, 0x01,
            0x03, 0x11, 0x01,
        ]);
    }

    private static void WriteHuffmanTables(Stream output)
    {
        var tables = new[] { (0x00, DcLuminance), (0x10, AcLuminance), (0x01, DcChrominance), (0x11, AcChrominance) };
        var length = 2;

        foreach (var (_, table) in tables)
            length += 17 + table.Symbols.Length;

        output.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)length]);

        foreach (var (classAndIndex, table) in tables)
        {
            output.WriteByte((byte)classAndIndex);
            output.Write(table.Counts);
            output.Write(table.Symbols);
        }
    }

    private static void WriteScanHeader(Stream output) =>
        output.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
}
