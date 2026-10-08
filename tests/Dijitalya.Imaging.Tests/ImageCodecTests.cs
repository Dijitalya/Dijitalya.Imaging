using System.Buffers.Binary;
using Dijitalya.Imaging;
using Dijitalya.Imaging.Common;

namespace Dijitalya.Imaging.Tests;

/// <summary>
/// The entry point every caller uses: the format is judged by content, never by name, and anything that is not
/// a picture the codecs know is reported as such rather than failing some other way.
/// </summary>
public sealed class ImageCodecTests
{
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ImageFormat.Jpeg)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ImageFormat.Png)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, ImageFormat.Gif)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, ImageFormat.Webp)]
    public void DetectFormat_RecognisesTheSignatures(byte[] header, ImageFormat expected)
    {
        Assert.Equal(expected, ImageCodec.DetectFormat(header));
    }

    [Fact]
    public void DetectFormat_WantsMoreThanTwoLettersForABitmap()
    {
        // "BM" starts plenty of text files; the header size after it must be a real one too.
        Assert.Null(ImageCodec.DetectFormat("BM is not a bitmap header at all"u8));
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Gif)]
    [InlineData(ImageFormat.Bmp)]
    [InlineData(ImageFormat.Webp)]
    public void Identify_ReadsTheSizeFromTheHeader(ImageFormat format)
    {
        var encoded = ImageCodec.Encode(SamplePictures.Photo(37, 21), format, quality: 80);

        Assert.Equal((format, 37, 21), ImageCodec.Identify(encoded));
    }

    [Fact]
    public void Identify_AnswersNull_ForContentThatIsNoPicture_OrACutOffHeader()
    {
        var png = ImageCodec.Encode(SamplePictures.Photo(10, 10), ImageFormat.Png);

        Assert.Null(ImageCodec.Identify("%PDF-1.7 a document"u8));
        Assert.Null(ImageCodec.Identify(png.AsSpan(0, 12)));
    }

    [Fact]
    public void Decode_ReportsContentThatIsNoPicture_AsAnUnknownFormat()
    {
        var pdf = "%PDF-1.7 a document, not a picture"u8.ToArray();

        Assert.Throws<UnknownImageFormatException>(() => ImageCodec.Decode(pdf));
    }

    [Fact]
    public async Task DecodeAsync_TurnsAwayANonPicture_WithoutReadingAllOfIt()
    {
        var stream = new CountingStream(new byte[8 * 1024 * 1024]);

        await Assert.ThrowsAsync<UnknownImageFormatException>(() => ImageCodec.DecodeAsync(stream));

        // The signature decides; the rest of an archive is never loaded.
        Assert.True(stream.BytesRead < 1024, $"{stream.BytesRead} bytes were read");
    }

    [Fact]
    public void Decode_ReportsADamagedPicture_AsInvalidContent()
    {
        var png = ImageCodec.Encode(SamplePictures.Photo(32, 32), ImageFormat.Png);
        png[40] ^= 0xFF;

        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(png));
    }

    [Fact]
    public void Decode_RefusesAPictureLargerThanTheLimits_BeforeAllocatingForIt()
    {
        // A valid PNG header (checksum included) announcing 100000 x 100000 pixels.
        var png = ImageCodec.Encode(SamplePictures.Photo(1, 1), ImageFormat.Png);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32.Compute(png.AsSpan(12, 17)));

        var exception = Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(png));

        Assert.Contains("100000 x 100000", exception.Message);
    }

    [Theory]
    [InlineData(ImageFormat.Jpeg, ".jpg", "image/jpeg")]
    [InlineData(ImageFormat.Png, ".png", "image/png")]
    [InlineData(ImageFormat.Gif, ".gif", "image/gif")]
    [InlineData(ImageFormat.Bmp, ".bmp", "image/bmp")]
    [InlineData(ImageFormat.Webp, ".webp", "image/webp")]
    public void ExtensionAndContentType_MatchTheFormat(ImageFormat format, string extension, string contentType)
    {
        Assert.Equal(extension, ImageCodec.ExtensionOf(format));
        Assert.Equal(contentType, ImageCodec.ContentTypeOf(format));
    }
}
