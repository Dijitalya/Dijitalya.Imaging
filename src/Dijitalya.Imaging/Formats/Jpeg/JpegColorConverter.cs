namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>
/// Turns the sample planes of a JPEG into RGBA: subsampled components are upsampled bilinearly
/// (sample centres aligned, as libjpeg's "fancy" upsampling does), then the colour space is converted.
/// </summary>
internal static class JpegColorConverter
{
    private const int Shift = 16;
    private const int Half = 1 << (Shift - 1);
    private static readonly int CrToR = Fix(1.40200);
    private static readonly int CbToB = Fix(1.77200);
    private static readonly int CrToG = Fix(0.71414);
    private static readonly int CbToG = Fix(0.34414);

    /// <param name="frame">The frame the planes belong to.</param>
    /// <param name="planes">The samples of each component, <see cref="JpegComponent.BlocksPerLineForMcu"/> * 8 bytes per row.</param>
    /// <param name="colorSpace">What the components hold.</param>
    public static RasterImage Convert(JpegFrame frame, byte[][] planes, JpegColorSpace colorSpace)
    {
        var width = frame.Width;
        var height = frame.Height;
        var full = new byte[frame.Components.Count][];

        for (var index = 0; index < full.Length; index++)
            full[index] = Upsample(frame, frame.Components[index], planes[index]);

        var image = new RasterImage(width, height);
        var pixels = image.Pixels;
        var count = width * height;

        switch (colorSpace)
        {
            case JpegColorSpace.Grayscale:
            {
                var gray = full[0];

                for (var index = 0; index < count; index++)
                {
                    if ((index & 4095) == 0) DecodeContext.Checkpoint();
                    var target = index * 4;
                    pixels[target] = pixels[target + 1] = pixels[target + 2] = gray[index];
                    pixels[target + 3] = 255;
                }

                break;
            }

            case JpegColorSpace.Rgb:
                for (var index = 0; index < count; index++)
                {
                    if ((index & 4095) == 0) DecodeContext.Checkpoint();
                    var target = index * 4;
                    pixels[target] = full[0][index];
                    pixels[target + 1] = full[1][index];
                    pixels[target + 2] = full[2][index];
                    pixels[target + 3] = 255;
                }

                break;

            case JpegColorSpace.YCbCr:
                for (var index = 0; index < count; index++)
                {
                    if ((index & 4095) == 0) DecodeContext.Checkpoint();
                    WriteYCbCr(pixels, index * 4, full[0][index], full[1][index], full[2][index]);
                }

                break;

            case JpegColorSpace.Cmyk:
                for (var index = 0; index < count; index++)
                {
                    if ((index & 4095) == 0) DecodeContext.Checkpoint();
                    var target = index * 4;
                    var white = 255 - full[3][index];
                    pixels[target] = Multiply(255 - full[0][index], white);
                    pixels[target + 1] = Multiply(255 - full[1][index], white);
                    pixels[target + 2] = Multiply(255 - full[2][index], white);
                    pixels[target + 3] = 255;
                }

                break;

            case JpegColorSpace.InvertedCmyk:
                for (var index = 0; index < count; index++)
                {
                    if ((index & 4095) == 0) DecodeContext.Checkpoint();
                    var target = index * 4;
                    var white = full[3][index];
                    pixels[target] = Multiply(full[0][index], white);
                    pixels[target + 1] = Multiply(full[1][index], white);
                    pixels[target + 2] = Multiply(full[2][index], white);
                    pixels[target + 3] = 255;
                }

                break;

            default:
                for (var index = 0; index < count; index++)
                {
                    if ((index & 4095) == 0) DecodeContext.Checkpoint();
                    // YCC carries R, G, B; libjpeg turns them into inverted C, M, Y as 255 - R, ...,
                    // which are then laid over the inverted K like any Adobe CMYK.
                    var target = index * 4;
                    WriteYCbCr(pixels, target, full[0][index], full[1][index], full[2][index]);
                    var white = full[3][index];
                    pixels[target] = Multiply(255 - pixels[target], white);
                    pixels[target + 1] = Multiply(255 - pixels[target + 1], white);
                    pixels[target + 2] = Multiply(255 - pixels[target + 2], white);
                    pixels[target + 3] = 255;
                }

                break;
        }

        return image;
    }

    private static void WriteYCbCr(byte[] pixels, int target, int luma, int blueDifference, int redDifference)
    {
        var cb = blueDifference - 128;
        var cr = redDifference - 128;

        pixels[target] = Clamp(luma + (((CrToR * cr) + Half) >> Shift));
        pixels[target + 1] = Clamp(luma + (((-CbToG * cb) - (CrToG * cr) + Half) >> Shift));
        pixels[target + 2] = Clamp(luma + (((CbToB * cb) + Half) >> Shift));
        pixels[target + 3] = 255;
    }

    /// <summary>The component's samples at the full size of the picture, one byte per pixel.</summary>
    private static byte[] Upsample(JpegFrame frame, JpegComponent component, byte[] plane)
    {
        var width = frame.Width;
        var height = frame.Height;
        var stride = component.BlocksPerLineForMcu * 8;
        var result = DecodeContext.Allocate<byte>(checked(width * height));

        if (component.HorizontalFactor == frame.MaxHorizontalFactor && component.VerticalFactor == frame.MaxVerticalFactor)
        {
            for (var y = 0; y < height; y++)
            {
                DecodeContext.Checkpoint();
                Array.Copy(plane, y * stride, result, y * width, width);
            }

            return result;
        }

        var (left, right, horizontalWeight) = Positions(width, component.HorizontalFactor, frame.MaxHorizontalFactor, component.SampleWidth);
        var (top, bottom, verticalWeight) = Positions(height, component.VerticalFactor, frame.MaxVerticalFactor, component.SampleHeight);

        for (var y = 0; y < height; y++)
        {
            DecodeContext.Checkpoint();
            var upper = top[y] * stride;
            var lower = bottom[y] * stride;
            var weightY = verticalWeight[y];

            for (var x = 0; x < width; x++)
            {
                var weightX = horizontalWeight[x];
                var above = (plane[upper + left[x]] * (256 - weightX)) + (plane[upper + right[x]] * weightX);
                var below = (plane[lower + left[x]] * (256 - weightX)) + (plane[lower + right[x]] * weightX);

                result[(y * width) + x] = (byte)(((above * (256 - weightY)) + (below * weightY) + 32768) >> 16);
            }
        }

        return result;
    }

    /// <summary>For each output pixel along one axis: the two samples around its centre and the weight (0 to 256) of the second.</summary>
    private static (int[] First, int[] Second, int[] Weight) Positions(int length, int factor, int maxFactor, int sampleLength)
    {
        var first = DecodeContext.Allocate<int>(checked(length));
        var second = DecodeContext.Allocate<int>(checked(length));
        var weight = DecodeContext.Allocate<int>(checked(length));
        var ratio = (double)factor / maxFactor;

        for (var index = 0; index < length; index++)
        {
            var position = ((index + 0.5) * ratio) - 0.5;

            if (position <= 0)
            {
                first[index] = second[index] = 0;
                continue;
            }

            var lower = (int)Math.Floor(position);

            if (lower >= sampleLength - 1)
            {
                first[index] = second[index] = sampleLength - 1;
                continue;
            }

            first[index] = lower;
            second[index] = lower + 1;
            weight[index] = (int)Math.Round((position - lower) * 256);
        }

        return (first, second, weight);
    }

    private static byte Multiply(int value, int factor) => (byte)(((value * factor) + 127) / 255);

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static int Fix(double value) => (int)((value * (1 << Shift)) + 0.5);
}
