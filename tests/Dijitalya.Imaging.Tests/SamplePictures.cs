using Dijitalya.Imaging;

namespace Dijitalya.Imaging.Tests;

/// <summary>Pictures built in code for the codec tests, and ways to compare them.</summary>
internal static class SamplePictures
{
    /// <summary>Smooth colour gradients with a little texture: what photographs mostly are.</summary>
    public static RasterImage Photo(int width, int height, bool withAlpha = false)
    {
        var image = new RasterImage(width, height);
        var pixels = image.Pixels;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = ((y * width) + x) * 4;
                pixels[index] = (byte)(128 + (100 * Math.Sin(x / 9.0)));
                pixels[index + 1] = (byte)(128 + (100 * Math.Cos(y / 7.0)));
                pixels[index + 2] = (byte)(128 + (60 * Math.Sin((x + y) / 11.0)));
                pixels[index + 3] = withAlpha ? (byte)(x < width / 4 ? 0 : x < width / 2 ? 128 : 255) : (byte)255;
            }
        }

        return image;
    }

    /// <summary>Random pixels from a fixed seed: nothing to predict, every value possible.</summary>
    public static RasterImage Noise(int width, int height, int seed)
    {
        var image = new RasterImage(width, height);
        new Random(seed).NextBytes(image.Pixels);

        return image;
    }

    /// <summary>A picture whose every pixel is different, to follow where pixels go.</summary>
    public static RasterImage Labelled(int width, int height)
    {
        var image = new RasterImage(width, height);

        for (var i = 0; i < width * height; i++)
        {
            image.Pixels[(i * 4) + 0] = (byte)i;
            image.Pixels[(i * 4) + 1] = (byte)(i >> 8);
            image.Pixels[(i * 4) + 2] = 7;
            image.Pixels[(i * 4) + 3] = 255;
        }

        return image;
    }

    /// <summary>The colour of one pixel as R, G, B, A.</summary>
    public static (byte R, byte G, byte B, byte A) At(RasterImage image, int x, int y)
    {
        var index = ((y * image.Width) + x) * 4;

        return (image.Pixels[index], image.Pixels[index + 1], image.Pixels[index + 2], image.Pixels[index + 3]);
    }

    /// <summary>Peak signal-to-noise ratio of the colour channels, in dB (99 for identical pictures).</summary>
    public static double Psnr(RasterImage expected, RasterImage actual)
    {
        double sum = 0;
        long count = 0;

        for (var i = 0; i < expected.Pixels.Length; i += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                double difference = expected.Pixels[i + channel] - actual.Pixels[i + channel];
                sum += difference * difference;
                count++;
            }
        }

        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (sum / count));
    }

    /// <summary>The alpha channel alone.</summary>
    public static byte[] Alpha(RasterImage image)
    {
        var alpha = new byte[image.Width * image.Height];

        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = image.Pixels[(i * 4) + 3];

        return alpha;
    }
}
