using Dijitalya.Imaging;
using Dijitalya.Imaging.Processing;

namespace Dijitalya.Imaging.Tests;

/// <summary>Where every pixel goes under the EXIF orientations, rotations, flips and crops.</summary>
public sealed class ImageTransformsTests
{
    // A 3 x 2 picture labelled
    //   0 1 2
    //   3 4 5
    // and, per EXIF orientation, the labels of the upright result row by row.
    [Theory]
    [InlineData(1, 3, 2, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(2, 3, 2, new[] { 2, 1, 0, 5, 4, 3 })]
    [InlineData(3, 3, 2, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(4, 3, 2, new[] { 3, 4, 5, 0, 1, 2 })]
    [InlineData(5, 2, 3, new[] { 0, 3, 1, 4, 2, 5 })]
    [InlineData(6, 2, 3, new[] { 3, 0, 4, 1, 5, 2 })]
    [InlineData(7, 2, 3, new[] { 5, 2, 4, 1, 3, 0 })]
    [InlineData(8, 2, 3, new[] { 2, 5, 1, 4, 0, 3 })]
    public void AutoOrient_TurnsThePictureUpright(int orientation, int width, int height, int[] labels)
    {
        var upright = ImageTransforms.AutoOrient(SamplePictures.Labelled(3, 2), orientation);

        Assert.Equal((width, height), (upright.Width, upright.Height));
        Assert.Equal(labels, Labels(upright));
    }

    [Fact]
    public void Rotate_TurnsClockwise()
    {
        var rotated = ImageTransforms.Rotate(SamplePictures.Labelled(3, 2), 90);

        Assert.Equal(new[] { 3, 0, 4, 1, 5, 2 }, Labels(rotated));
    }

    [Fact]
    public void Rotate_RefusesAnythingButQuarterTurns()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageTransforms.Rotate(SamplePictures.Labelled(3, 2), 45));
    }

    [Fact]
    public void Crop_TakesExactlyTheRectangle()
    {
        var cropped = ImageTransforms.Crop(SamplePictures.Labelled(3, 2), 1, 0, 2, 2);

        Assert.Equal(new[] { 1, 2, 4, 5 }, Labels(cropped));
    }

    private static int[] Labels(RasterImage image)
    {
        var labels = new int[image.Width * image.Height];

        for (var i = 0; i < labels.Length; i++)
            labels[i] = image.Pixels[i * 4] | (image.Pixels[(i * 4) + 1] << 8);

        return labels;
    }
}
