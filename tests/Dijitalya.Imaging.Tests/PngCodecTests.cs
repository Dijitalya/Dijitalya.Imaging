using System.Text;
using Dijitalya.Imaging;

namespace Dijitalya.Imaging.Tests;

/// <summary>PNG is lossless: whatever goes in must come back exactly, in whichever shape the encoder picks.</summary>
public sealed class PngCodecTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 3)]
    [InlineData(64, 48)]
    public void TrueColourWithAlpha_RoundTripsExactly(int width, int height)
    {
        var source = SamplePictures.Noise(width, height, seed: width);

        var decoded = ImageCodec.Decode(ImageCodec.Encode(source, ImageFormat.Png));

        Assert.Equal(ImageFormat.Png, decoded.Format);
        Assert.Equal(source.Pixels, decoded.Image.Pixels);
    }

    [Fact]
    public void AFewColours_GoThroughAPalette_AndStillRoundTripExactly()
    {
        var source = SamplePictures.Photo(40, 30, withAlpha: true);

        // Reduce to a handful of colours, some of them translucent.
        for (var i = 0; i < source.Pixels.Length; i++)
        {
            source.Pixels[i] &= 0xC0;
        }

        var encoded = ImageCodec.Encode(source, ImageFormat.Png);

        Assert.Equal(source.Pixels, ImageCodec.Decode(encoded).Image.Pixels);
        Assert.Contains("PLTE", Encoding.ASCII.GetString(encoded));
    }

    [Fact]
    public void AnOpaquePicture_IsWrittenWithoutAnAlphaChannel()
    {
        var source = SamplePictures.Photo(300, 2);

        var encoded = ImageCodec.Encode(source, ImageFormat.Png);

        // Colour type 2 (truecolour) in the header, not 6 (truecolour with alpha).
        Assert.Equal(2, encoded[25]);
        Assert.Equal(source.Pixels, ImageCodec.Decode(encoded).Image.Pixels);
    }
}
