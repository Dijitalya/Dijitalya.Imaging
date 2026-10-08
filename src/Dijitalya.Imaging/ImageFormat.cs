namespace Dijitalya.Imaging;

/// <summary>The file formats the codecs know.</summary>
public enum ImageFormat
{
    /// <summary>JPEG: decoded (baseline and progressive) and encoded (baseline).</summary>
    Jpeg = 0,

    /// <summary>PNG: decoded and encoded.</summary>
    Png = 1,

    /// <summary>GIF: decoded (the first frame) and encoded (a still picture of up to 256 colours).</summary>
    Gif = 2,

    /// <summary>BMP: decoded and encoded (uncompressed).</summary>
    Bmp = 3,

    /// <summary>WebP: decoded (lossy, lossless, alpha, the first frame of an animation) and encoded (lossy, alpha lossless).</summary>
    Webp = 4,
}
