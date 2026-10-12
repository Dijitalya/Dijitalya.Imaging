using Dijitalya.Imaging;

namespace Dijitalya.Imaging.Tests;

/// <summary>
/// Uploads are untrusted. Whatever a damaged or hostile file does to a decoder, the caller may only
/// ever see a picture or one of the two "not a usable picture" exceptions — never an index error,
/// never a hang, never a huge allocation.
/// </summary>
public sealed class DamagedFileTests
{
    public static TheoryData<ImageFormat> Formats => new() { ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Gif, ImageFormat.Bmp, ImageFormat.Webp };

    [Theory]
    [MemberData(nameof(Formats))]
    public void FlippedBytes_TruncationAndInsertions_OnlyEverFailCleanly(ImageFormat format)
    {
        var original = ImageCodec.Encode(SamplePictures.Photo(48, 32, withAlpha: format is not (ImageFormat.Jpeg or ImageFormat.Gif)), format, quality: 80);
        var random = new Random(1234 + (int)format);

        var attempts = int.TryParse(Environment.GetEnvironmentVariable("IMAGING_FUZZ_ITERATIONS"), out var requested)
            ? Math.Clamp(requested, 300, 100_000) : 300;
        var options = new ImageDecodeOptions { MaxPixelCount = 1_048_576, MaxAllocationBytes = 32 * 1024 * 1024 };
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var damaged = Damage(original, random);

            try
            {
                ImageCodec.Decode(damaged, options);
            }
            catch (Exception exception)
            {
                Assert.True(
                    exception is InvalidImageContentException or UnknownImageFormatException,
                    $"attempt {attempt} must fail cleanly, not with {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    [Fact]
    public void AGifBlockWithoutItsTerminator_EndsTheReading_InsteadOfLooping()
    {
        // An extension whose sub-blocks run off the end of the file, a known way for a crafted GIF to hang a decoder.
        byte[] gif =
        [
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0, 1, 0, 0, 0, 0,
            0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E',
        ];

        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(gif));
    }

    private static byte[] Damage(byte[] original, Random random)
    {
        var data = (byte[])original.Clone();

        switch (random.Next(4))
        {
            case 0:
                for (var i = random.Next(1, 8); i > 0; i--)
                    data[random.Next(data.Length)] = (byte)random.Next(256);

                return data;

            case 1:
                return data[..random.Next(1, data.Length)];

            case 2:
                for (var i = random.Next(1, 30); i > 0; i--)
                    data[random.Next(Math.Min(16, data.Length - 1), data.Length)] ^= (byte)(1 << random.Next(8));

                return data;

            default:
            {
                var position = random.Next(data.Length);
                var inserted = new byte[random.Next(1, 40)];
                random.NextBytes(inserted);

                return [.. data[..position], .. inserted, .. data[position..]];
            }
        }
    }
}
