namespace Dijitalya.Imaging;

/// <summary>
/// A picture held in memory: <see cref="Width"/> by <see cref="Height"/> pixels, four bytes each in
/// R, G, B, A order, rows from top to bottom. Alpha is straight (not premultiplied); 255 is opaque.
/// </summary>
public sealed class RasterImage
{
    /// <summary>A new, fully transparent picture of the given size.</summary>
    /// <exception cref="InvalidImageContentException">The size is not positive or exceeds <see cref="ImageLimits"/>.</exception>
    public RasterImage(int width, int height)
        : this(width, height, null)
    {
    }

    internal RasterImage(int width, int height, byte[]? pixels)
    {
        ImageLimits.EnsureSize(width, height);

        var length = width * height * 4;

        if (pixels is not null && pixels.Length != length)
            throw new ArgumentException("The pixel buffer does not match the size of the picture.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels ?? DecodeContext.Allocate<byte>(length);
    }

    /// <summary>The width in pixels.</summary>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixels, <c>Width * Height * 4</c> bytes in R, G, B, A order.</summary>
    public byte[] Pixels { get; }

    /// <summary>A copy of the picture with its own pixel buffer.</summary>
    public RasterImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>True when at least one pixel is not fully opaque.</summary>
    public bool HasTransparency()
    {
        var pixels = Pixels;

        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 255)
                return true;
        }

        return false;
    }
}
