using Dijitalya.Imaging;
using Dijitalya.Imaging.Processing;

namespace Dijitalya.Imaging.Tests;

/// <summary>The three ways a picture is fitted into a box, and what resampling must not do to it.</summary>
public sealed class ImageResizerTests
{
    [Theory]
    [InlineData(400, 200, 100, 100, 100, 50)]
    [InlineData(200, 400, 100, 100, 50, 100)]
    [InlineData(400, 200, 800, 0, 800, 400)]
    [InlineData(400, 200, 0, 50, 100, 50)]
    public void Max_KeepsProportions_AndWorksOutAMissingSide(int width, int height, int boxWidth, int boxHeight, int expectedWidth, int expectedHeight)
    {
        var resized = ImageResizer.ResizeToBox(SamplePictures.Photo(width, height), boxWidth, boxHeight, ResizeMode.Max);

        Assert.Equal((expectedWidth, expectedHeight), (resized.Width, resized.Height));
    }

    [Fact]
    public void Crop_FillsTheBoxExactly_KeepingTheCentre()
    {
        // Left third red, middle third green, right third blue: a square crop keeps the green.
        var source = new RasterImage(300, 100);

        for (var x = 0; x < 300; x++)
        {
            for (var y = 0; y < 100; y++)
            {
                var index = ((y * 300) + x) * 4;
                source.Pixels[index + (x / 100)] = 255;
                source.Pixels[index + 3] = 255;
            }
        }

        var cropped = ImageResizer.ResizeToBox(source, 50, 50, ResizeMode.Crop);

        Assert.Equal((50, 50), (cropped.Width, cropped.Height));
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), SamplePictures.At(cropped, 25, 25));
    }

    [Fact]
    public void Pad_FillsTheBoxExactly_WithTransparentBars()
    {
        var padded = ImageResizer.ResizeToBox(SamplePictures.Photo(200, 100), 100, 100, ResizeMode.Pad);

        Assert.Equal((100, 100), (padded.Width, padded.Height));

        // The bar above the picture is transparent.
        Assert.Equal(0, SamplePictures.At(padded, 50, 5).A);
        Assert.Equal(255, SamplePictures.At(padded, 50, 50).A);
    }

    [Fact]
    public void AFlatColour_StaysExactlyThatColour_AtAnySize()
    {
        var source = new RasterImage(37, 23);

        for (var i = 0; i < source.Pixels.Length; i += 4)
        {
            source.Pixels[i] = 200;
            source.Pixels[i + 1] = 100;
            source.Pixels[i + 2] = 50;
            source.Pixels[i + 3] = 255;
        }

        foreach (var (width, height) in new[] { (10, 7), (100, 61), (3, 3) })
        {
            var resized = ImageResizer.Resize(source, width, height);

            for (var i = 0; i < resized.Pixels.Length; i += 4)
            {
                Assert.Equal(new byte[] { 200, 100, 50, 255 }, resized.Pixels.AsSpan(i, 4).ToArray());
            }
        }
    }

    [Fact]
    public void TransparentPixels_DoNotBleedTheirColourIntoTheirNeighbours()
    {
        // Opaque white beside fully transparent black: no grey fringe may appear.
        var source = new RasterImage(20, 20);

        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 10; x++)
            {
                var index = ((y * 20) + x) * 4;
                source.Pixels[index] = source.Pixels[index + 1] = source.Pixels[index + 2] = source.Pixels[index + 3] = 255;
            }
        }

        var resized = ImageResizer.Resize(source, 7, 7);

        for (var i = 0; i < resized.Pixels.Length; i += 4)
        {
            // A visible pixel keeps the colour of the opaque ones.
            if (resized.Pixels[i + 3] > 0)
            {
                Assert.True(resized.Pixels[i] > 250, $"red {resized.Pixels[i]} at {i / 4}");
            }
        }
    }
}
