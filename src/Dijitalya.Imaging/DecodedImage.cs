namespace Dijitalya.Imaging;

/// <summary>
/// A decoded picture with what the file said about it: its format, its EXIF orientation and its
/// colour profile.
/// </summary>
public sealed class DecodedImage
{
    /// <summary>A decoded picture and what its file said about it.</summary>
    /// <param name="image">The pixels as they are stored in the file.</param>
    /// <param name="format">The format of the file.</param>
    /// <param name="orientation">The EXIF orientation; a value outside 1 to 8 is taken as 1.</param>
    /// <param name="iccProfile">The RGB colour profile of the file, or null for sRGB.</param>
    public DecodedImage(RasterImage image, ImageFormat format, int orientation, byte[]? iccProfile = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        Image = image;
        Format = format;
        Orientation = orientation is >= 1 and <= 8 ? orientation : 1;
        IccProfile = iccProfile;
    }

    /// <summary>The pixels as they are stored in the file, before the orientation is applied.</summary>
    public RasterImage Image { get; }

    /// <summary>The format of the file the picture was decoded from.</summary>
    public ImageFormat Format { get; }

    /// <summary>
    /// The EXIF orientation (1 to 8): how the stored pixels must be turned to stand upright. 1 means
    /// they already do, and is also the value for a file that carries no orientation.
    /// </summary>
    public int Orientation { get; }

    /// <summary>
    /// The ICC profile describing the colours of the pixels, when the file carried an RGB one;
    /// null means sRGB. Passing it on to <see cref="ImageCodec.Encode"/> keeps the colours as they
    /// were (a phone's Display P3 photo, for instance).
    /// </summary>
    public byte[]? IccProfile { get; }
}
