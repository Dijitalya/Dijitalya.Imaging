using Dijitalya.Imaging;

namespace Dijitalya.Imaging.Tests;

/// <summary>
/// GIF and BMP: small files built by hand pin how they are read, and round trips pin how they are written. A
/// bitmap keeps every pixel; a gif keeps up to 256 colours exactly and reduces a picture of more.
/// </summary>
public sealed class GifAndBmpCodecTests
{
    [Fact]
    public void AGif_DecodesItsFirstFrame_WithItsTransparency()
    {
        // 2 x 1 pixels: palette red, green; the graphic control extension makes index 1 transparent.
        byte[] gif =
        [
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 2, 0, 1, 0, 0x80, 0, 0,
            255, 0, 0, 0, 255, 0,
            0x21, 0xF9, 4, 1, 0, 0, 1, 0,
            0x2C, 0, 0, 0, 0, 2, 0, 1, 0, 0,
            2, 2, 0x44, 0x0A, 0,
            0x3B,
        ];

        var decoded = ImageCodec.Decode(gif);

        Assert.Equal(ImageFormat.Gif, decoded.Format);
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), SamplePictures.At(decoded.Image, 0, 0));
        Assert.Equal(0, SamplePictures.At(decoded.Image, 1, 0).A);
    }

    [Fact]
    public void A24BitBmp_DecodesBottomUpRows_InTheRightOrder()
    {
        // 1 x 2 pixels, stored bottom row first: blue below, white above.
        byte[] bmp =
        [
            (byte)'B', (byte)'M', 62, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0,
            40, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            255, 0, 0, 0,
            255, 255, 255, 0,
        ];

        var decoded = ImageCodec.Decode(bmp);

        Assert.Equal(ImageFormat.Bmp, decoded.Format);
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), SamplePictures.At(decoded.Image, 0, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), SamplePictures.At(decoded.Image, 0, 1));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(13, 7)]
    [InlineData(64, 40)]
    public void ABitmap_RoundTripsExactly_WithItsTransparency(int width, int height)
    {
        var source = SamplePictures.Noise(width, height, seed: height);

        var decoded = ImageCodec.Decode(ImageCodec.Encode(source, ImageFormat.Bmp));

        Assert.Equal(ImageFormat.Bmp, decoded.Format);
        Assert.Equal(source.Pixels, decoded.Image.Pixels);
    }

    [Fact]
    public void AGifOfFewColours_RoundTripsExactly_WithItsTransparency()
    {
        var source = SamplePictures.Photo(50, 30, withAlpha: true);

        // At most 64 colours, and alpha either fully on or fully off.
        for (var i = 0; i < source.Pixels.Length; i += 4)
        {
            source.Pixels[i] &= 0xC0;
            source.Pixels[i + 1] &= 0xC0;
            source.Pixels[i + 2] &= 0xC0;
            source.Pixels[i + 3] = source.Pixels[i + 3] < 128 ? (byte)0 : (byte)255;

            if (source.Pixels[i + 3] == 0)
            {
                source.Pixels[i] = source.Pixels[i + 1] = source.Pixels[i + 2] = 0;
            }
        }

        var decoded = ImageCodec.Decode(ImageCodec.Encode(source, ImageFormat.Gif)).Image;

        for (var i = 0; i < source.Pixels.Length; i += 4)
        {
            Assert.Equal(source.Pixels[i + 3], decoded.Pixels[i + 3]);

            if (source.Pixels[i + 3] == 255)
            {
                Assert.Equal(source.Pixels.AsSpan(i, 3).ToArray(), decoded.Pixels.AsSpan(i, 3).ToArray());
            }
        }
    }

    [Fact]
    public void AGifOfManyColours_IsReducedTo256_AndStaysClose()
    {
        var source = SamplePictures.Photo(160, 120);

        var encoded = ImageCodec.Encode(source, ImageFormat.Gif);
        var decoded = ImageCodec.Decode(encoded).Image;

        Assert.Equal(7, encoded[10] & 7);
        Assert.True(SamplePictures.Psnr(source, decoded) > 25);
    }

    [Fact]
    public void ALargeGif_KeepsItsPixels_AcrossDictionaryResets()
    {
        // Noise of 16 colours fills the LZW dictionary many times over.
        var source = SamplePictures.Noise(300, 200, seed: 9);

        for (var i = 0; i < source.Pixels.Length; i += 4)
        {
            source.Pixels[i] &= 0xC0;
            source.Pixels[i + 1] &= 0x80;
            source.Pixels[i + 2] = 0;
            source.Pixels[i + 3] = 255;
        }

        var decoded = ImageCodec.Decode(ImageCodec.Encode(source, ImageFormat.Gif)).Image;

        Assert.Equal(source.Pixels, decoded.Pixels);
    }
}
