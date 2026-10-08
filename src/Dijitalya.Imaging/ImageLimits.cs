namespace Dijitalya.Imaging;

/// <summary>
/// The largest pictures the codecs accept. A file of a few kilobytes can claim to be a picture of a
/// billion pixels; the decoders compare the size a file announces with these limits before they
/// allocate anything for it, so such a file is refused instead of exhausting memory.
/// </summary>
public static class ImageLimits
{
    /// <summary>The longest side a picture may have, in pixels.</summary>
    public const int MaxDimension = 32_768;

    /// <summary>
    /// The most pixels a picture may have: 64 megapixels, which is 256 MB as RGBA. Photographs of
    /// today's phones and cameras stay well below it.
    /// </summary>
    public const long MaxPixelCount = 64L * 1024 * 1024;

    /// <summary>
    /// The largest file <see cref="ImageCodec.DecodeAsync"/> reads: room for the largest picture
    /// stored uncompressed, plus metadata.
    /// </summary>
    public const long MaxFileSize = (MaxPixelCount * 4) + (16 * 1024 * 1024);

    /// <summary>True when a picture of this size may be decoded or created.</summary>
    public static bool IsAllowed(long width, long height) =>
        width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension && width * height <= MaxPixelCount;

    /// <exception cref="InvalidImageContentException">The size is not positive or exceeds the limits.</exception>
    public static void EnsureSize(long width, long height)
    {
        if (!IsAllowed(width, height))
            throw new InvalidImageContentException($"A picture of {width} x {height} pixels is not supported.");
    }
}
