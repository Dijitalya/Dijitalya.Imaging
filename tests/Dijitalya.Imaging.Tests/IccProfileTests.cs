using Dijitalya.Imaging;

namespace Dijitalya.Imaging.Tests;

/// <summary>
/// A phone photo in Display P3 looks dull when its profile is lost, so an RGB profile travels with
/// the pixels from any format to any other. A grey or CMYK profile is dropped: the decoded pixels are
/// RGB, and such a profile would describe colours they no longer have.
/// </summary>
public sealed class IccProfileTests
{
    public static TheoryData<ImageFormat, ImageFormat> FormatPairs
    {
        get
        {
            var pairs = new TheoryData<ImageFormat, ImageFormat>();
            ImageFormat[] formats = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Webp];

            foreach (var from in formats)
            {
                foreach (var to in formats)
                    pairs.Add(from, to);
            }

            return pairs;
        }
    }

    [Theory]
    [MemberData(nameof(FormatPairs))]
    public void AnRgbProfile_SurvivesAnyConversion(ImageFormat from, ImageFormat to)
    {
        var profile = Profile("RGB ", 3000);
        var first = ImageCodec.Decode(ImageCodec.Encode(SamplePictures.Photo(20, 10), from, 80, profile));

        var second = ImageCodec.Decode(ImageCodec.Encode(first.Image, to, 80, first.IccProfile));

        Assert.Equal(profile, second.IccProfile);
    }

    [Fact]
    public void AProfileLargerThanOneJpegSegment_IsSplitAndReassembled()
    {
        var profile = Profile("RGB ", 200_000);

        var decoded = ImageCodec.Decode(ImageCodec.Encode(SamplePictures.Photo(8, 8), ImageFormat.Jpeg, 80, profile));

        Assert.Equal(profile, decoded.IccProfile);
    }

    [Fact]
    public void AGreyProfile_IsDropped()
    {
        var decoded = ImageCodec.Decode(ImageCodec.Encode(SamplePictures.Photo(8, 8), ImageFormat.Png, iccProfile: Profile("GRAY", 500)));

        Assert.Null(decoded.IccProfile);
    }

    [Fact]
    public void WithoutAProfile_NothingIsWritten()
    {
        var decoded = ImageCodec.Decode(ImageCodec.Encode(SamplePictures.Photo(8, 8), ImageFormat.Webp));

        Assert.Null(decoded.IccProfile);
    }

    // Only the header matters to the codecs: its size and its data colour space.
    private static byte[] Profile(string colourSpace, int length)
    {
        var profile = new byte[length];
        new Random(length).NextBytes(profile);
        profile[0] = (byte)(length >> 24);
        profile[1] = (byte)(length >> 16);
        profile[2] = (byte)(length >> 8);
        profile[3] = (byte)length;

        for (var i = 0; i < 4; i++)
            profile[16 + i] = (byte)colourSpace[i];

        return profile;
    }
}
