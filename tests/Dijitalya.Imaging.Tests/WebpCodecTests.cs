using System.Security.Cryptography;
using Dijitalya.Imaging;

namespace Dijitalya.Imaging.Tests;

/// <summary>
/// WebP decoding is pinned to libwebp itself: the files below were written by libwebp's cwebp (and
/// Pillow's animation encoder, which is libwebp too), and the hashes are of the pixels libwebp's own
/// decoder produces from them. The decoder must match them bit for bit. Encoding is pinned by round
/// trips: the output must decode to the same size, close to the source, with alpha exactly as given.
/// </summary>
public sealed class WebpCodecTests
{
    // 30 x 21 pixels each: lossy with the normal loop filter and sharpness, lossy with the simple
    // filter, lossy with filtered and compressed alpha, and lossless with transparency.
    private const string Lossy =
        "UklGRigBAABXRUJQVlA4IBwBAACQBgCdASoeABUAPrVKoUsoJKOiMBgIAQQWiWwAnTKEdJC+ctgiTZsR2spAPcAiPL88jS20"
        + "0Z8BRcswCtpoAPyMOECxn8qYpWqa8NsYD55Xwen2ClaZOJqyCOh2xI5ltoeH3O/czGrpZdSRlh5Sv3L/r/tpesfSzk2wnvT3"
        + "i6We92aeU/WEHzlYpIOGRXlLX114e32MdSwyhWtChbYrcWieaNsrb+Y97pJMLrpT7n97xKDrboqyLpqlX9SlMkl2h/M6T4QJ"
        + "l1Lkgmmgma34lCW6DmhUdWikIJV5jvuQhQ3H24Pn043/+Tf8J3/Qf0OQTrGgxf/5N/+UGUlU5BI0rC61PPsN/PHmF/zUUsXa"
        + "UHcuiO0yVWXfbP5Iq4AAAA==";

    private const string LossySimpleFilter =
        "UklGRvQAAABXRUJQVlA4IOgAAACyBQCdASoeABUAPu1krE+oJCOhsBgIBQAdiWwAnTKEgQj99BAZ1LbtMkpngMEq7RfzPqSv"
        + "7AAA94zM7vNE9wZrWgEqnfTUbX6lv/x9guUM4JwsOkmlGpInl55MXKj/WyKkswG8SwLSfRlJjojSW+wbQa5WsPN+HvE8KJuZ"
        + "u/NxqZdWT3u7jacHGXHNQHVDV8XRvSRbcZGtZsQRAgs2lh1CwVpnE4rDIyy1LhaiJxWdtdknO2weXyqrP5//Jv7p/5J/g27Z"
        + "X3z/+Tf+fNqjpv1GWbxiOq2cY4/B0lq75NH+mS62xOd5OSAA";

    private const string LossyWithAlpha =
        "UklGRqoBAABXRUJQVlA4WAoAAAAQAAAAHQAAFAAAQUxQSLgAAAAFuS5E9D80kmrbdq1oQBub6Ah9LKAz5xyq+svAGqd/xpKB"
        + "gIgJmIDHB/hW+V+fOffIeEBNTc1wj4aB4MQRC4d42zEcyYCCCEgKAQ0TAiIIwgEEDYnMIMh4bSYSh6CiV2S9ApMEgiKCeG0I"
        + "tuMrx4KAHEMS0cihKAIwMPGVVEAR5CBmm8i+skzKsQRN2nhVDIUBhijIduqPdy8cK8fMlDb94/0rZQKmtm3LBy9BgLHltm0f"
        + "CoMElpYMVlA4IMwAAABQBQCdASoeABUAPplCmUglpCKhMBgMALATCWwAnTKEf1ATC99uHBV/3eKu1FduuZmCv6gA/pZrOmsS"
        + "q/DExliZAtOJX0f7YeUzaIBBanYIvDaeIM/9TyqL+iD7qfei58e/M/4pL6pY9n1r6+siZ/tcCP8BoV+N0G9/0kYkFVv2zg98"
        + "M4zvoqvAsIvHcFdb/2eepf1PiFxE+/3K0OVFv5aniONqUPXORPANgVfn10SR8E+v2AgVIgNKECmxcudW6YYJUUPHDmmBt3sQ"
        + "IAA=";

    private const string Lossless =
        "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";

    // 12 x 10 pixels, two lossless frames; the first one is translucent in parts.
    private const string Animated =
        "UklGRkAEAABXRUJQVlA4WAoAAAASAAAACwAACQAAQU5JTQYAAAAAAAAAAABBTk1GRgIAAAAAAAAAAAsAAAkAAFAAAAJWUDhM"
        + "LQIAAC8LQAIQfwPXAgACUTamWm3/QE1ce402Z9u2t8ZeX9DY2bZvvNHmb8ANAMCQdKOzbYfKHV56Bdi23x/ZTlXCf/QtfA22"
        + "bbmxbVtNkuDu7gwDZuJOx6fPotPKnc5pGQI1iwlolQ53l+ewbRtJojy79xd9pYfYK/nDWVIIyp/REYcCL39MpvQ2+cMwGGBv"
        + "nxy+LCSdAQRoKjuB7+sdPSjGFTLNni6hf/JTXo+90Pz3nv+5lQOVMPDT3Z8w4fwoKPhPoxru93m/n0/epneuTnvs/UEsGANH"
        + "G5ikVmUOhCLf5NqRVtxat9KMmJ92OWPr7etUfNmRXSeveL4Z6pcxn0Lv3v8YaktUa3/ZsGfgn3zzDvsXtpjq7lKz2tVqe9vp"
        + "iwQVp72TtVDfSKhysmrU7blNUKdgJj0QppCS127c22qnNslKxGa4uR8em3QCWWkm9vnB3tHmDTtCJJtHWyo1ki5HzYrl9laP"
        + "41bEk4wgAAgb42QknvV8/53yzxY+cMgCwQdQEdMP+oFhHjTi+IZ832AFQuPAQwBCfxDMB2NgPhBA0BjeyAsBsH0STAuMwZhX"
        + "CDrYoXfc4xc4DgCwcM6qbdu2bdu2sv+3S0T0P+aL9WZ3Ot9vj+d3udpvD8fL9fN+/VBaINM5Qtl6bzzDWHyp3h5MVjqjKc7w"
        + "JFqrL1FqDSYIxRGrLZ5oodEdwiRXpDK5w5lysw8RbKHS6AzE87U2AAAoDDZvJFWs/uUas8sfS+cAAEFOTUbGAQAAAAAAAAAA"
        + "CwAACQAAUAAAAFZQOEyuAQAALwtAAgC/4zS2bVe59+VHznRBD8/hcQx1UAyaweEYHPPbIMvvcs6/DaeRJDlyOGt29xV89Ew6"
        + "EQBATbZztK3zETLJ3d0zDyFac9cH8CXXk23+4/Q654RioC6gWpT2pwOy9mRx4XobOYonibrQuDyfpjOen7IFDOiuyEYSkoGE"
        + "HwIAQJUT+qEIneKncH+IQhqkAAxJHJfHEAJHSF9OJoYhhAFTnAuF5F0RGIcIQNwHYhRNmiJQElcGFBeH4zsiGabsQTyU+9OL"
        + "bOQ5Y+s4PTX/PcboNjJeTekIRRiIiWZhUBELsVuQ3Lb1XFfRmAvx1fRXB+zsXcZv3wYk3WoyRvoM0Mu5nIMpqyRjzKrD1JcN"
        + "wejCfmY36FEDiZvkkFwp++8Bg1Q3anK6mkV0mUhUM5bwfKkF8QA4DACAaFXlZtu2bdu2bSOi/1FIz9fL4/ny+/wfuWJjfLrd"
        + "r6/n951qdfqHaXO4Y5Bp9VJ7M9mOYlGQiFa6vcV+NYvEYQKQHWTK+USyVkUwyMBWO3k+vcfGEnJEfgtJrrI7qBqBQkmhu8Rh"
        + "t5Er4+NSc0jLlFh1pmDACw==";

    [Theory]
    [InlineData(Lossy, "7b2969e870bad1e2c9e725b01129db14fcfbb08d1570954057304ca3437c8a17")]
    [InlineData(LossySimpleFilter, "38051b47daf3449233f468b1c46f3db263a57f1d9d5f7ddd60c572c5cf29e4fc")]
    [InlineData(LossyWithAlpha, "988cd0649c3e5d7c2971cc9cf7dc7d9e4c008bdad431f3bc3e227f61f410d0ff")]
    [InlineData(Lossless, "8c4ca67c029a4a1427ee27c31b6e76f3f0f7dea42b199044ff883ffaa292184a")]
    public void Decode_MatchesLibwebp_BitForBit(string file, string pixelHash)
    {
        var decoded = ImageCodec.Decode(Convert.FromBase64String(file));

        Assert.Equal(ImageFormat.Webp, decoded.Format);
        Assert.Equal((30, 21), (decoded.Image.Width, decoded.Image.Height));
        Assert.Equal(pixelHash, Convert.ToHexStringLower(SHA256.HashData(decoded.Image.Pixels)));
    }

    [Fact]
    public void AnAnimation_DecodesToItsFirstFrame_AsLibwebpShowsIt()
    {
        var decoded = ImageCodec.Decode(Convert.FromBase64String(Animated));

        Assert.Equal((12, 10), (decoded.Image.Width, decoded.Image.Height));
        Assert.Equal("cb3d9d02b5f70bce8e37fa9432858f9eb49166810b6fb2fabd31ea8ec5af6eb4", Convert.ToHexStringLower(SHA256.HashData(decoded.Image.Pixels)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 5)]
    [InlineData(17, 31)]
    [InlineData(200, 150)]
    public void Encode_WritesALossyPicture_ThatDecodesCloseToTheSource(int width, int height)
    {
        var source = SamplePictures.Photo(width, height);

        var encoded = ImageCodec.Encode(source, ImageFormat.Webp, quality: 82);
        var decoded = ImageCodec.Decode(encoded);

        // An opaque picture needs only the simple container.
        Assert.Equal("VP8 ", System.Text.Encoding.ASCII.GetString(encoded, 12, 4));
        Assert.Equal((width, height), (decoded.Image.Width, decoded.Image.Height));
        Assert.True(SamplePictures.Psnr(source, decoded.Image) > 30);
    }

    [Fact]
    public void Encode_KeepsTheAlphaExactly_ThroughALosslessAlphaChunk()
    {
        var source = SamplePictures.Photo(90, 60, withAlpha: true);

        var decoded = ImageCodec.Decode(ImageCodec.Encode(source, ImageFormat.Webp, quality: 50)).Image;

        Assert.Equal(SamplePictures.Alpha(source), SamplePictures.Alpha(decoded));
    }

    [Fact]
    public void Encode_KeepsNoisyAlphaExactly_Too()
    {
        var source = SamplePictures.Photo(64, 40);
        var random = new Random(3);

        for (var i = 3; i < source.Pixels.Length; i += 4)
            source.Pixels[i] = (byte)random.Next(256);

        var decoded = ImageCodec.Decode(ImageCodec.Encode(source, ImageFormat.Webp, quality: 75)).Image;

        Assert.Equal(SamplePictures.Alpha(source), SamplePictures.Alpha(decoded));
    }

    [Fact]
    public void Encode_HigherQuality_MeansALargerAndCloserFile()
    {
        var source = SamplePictures.Photo(160, 120);

        var low = ImageCodec.Encode(source, ImageFormat.Webp, quality: 20);
        var high = ImageCodec.Encode(source, ImageFormat.Webp, quality: 95);

        Assert.True(high.Length > low.Length);
        Assert.True(SamplePictures.Psnr(source, ImageCodec.Decode(high).Image) > SamplePictures.Psnr(source, ImageCodec.Decode(low).Image));
    }

    [Fact]
    public void Encode_RefusesAPictureWiderThanTheFormatAllows()
    {
        Assert.Throws<NotSupportedException>(() => ImageCodec.Encode(new RasterImage(16384, 1), ImageFormat.Webp));
    }
}
