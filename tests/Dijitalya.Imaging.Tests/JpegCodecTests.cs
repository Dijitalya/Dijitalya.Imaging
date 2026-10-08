using Dijitalya.Imaging;

namespace Dijitalya.Imaging.Tests;

/// <summary>JPEG loses detail by design; what is pinned is that it loses little, keeps the size and has no alpha.</summary>
public sealed class JpegCodecTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(17, 9)]
    [InlineData(160, 120)]
    public void APhotograph_ComesBackClose_AtTheSameSize(int width, int height)
    {
        var source = SamplePictures.Photo(width, height);

        var decoded = ImageCodec.Decode(ImageCodec.Encode(source, ImageFormat.Jpeg, quality: 90));

        Assert.Equal(ImageFormat.Jpeg, decoded.Format);
        Assert.Equal((width, height), (decoded.Image.Width, decoded.Image.Height));
        Assert.True(SamplePictures.Psnr(source, decoded.Image) > 34);
    }

    [Fact]
    public void HigherQuality_MeansALargerAndCloserFile()
    {
        var source = SamplePictures.Photo(128, 96);

        var low = ImageCodec.Encode(source, ImageFormat.Jpeg, quality: 30);
        var high = ImageCodec.Encode(source, ImageFormat.Jpeg, quality: 95);

        Assert.True(high.Length > low.Length);
        Assert.True(SamplePictures.Psnr(source, ImageCodec.Decode(high).Image) > SamplePictures.Psnr(source, ImageCodec.Decode(low).Image));
    }

    [Fact]
    public void TransparentPixels_AreLaidOnWhite()
    {
        var decoded = ImageCodec.Decode(ImageCodec.Encode(new RasterImage(16, 16), ImageFormat.Jpeg, quality: 95)).Image;

        var (r, g, b, a) = SamplePictures.At(decoded, 8, 8);
        Assert.Equal(255, a);
        Assert.True(r > 250 && g > 250 && b > 250, $"({r}, {g}, {b})");
    }
}
