namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>How the components of a JPEG are turned into RGB.</summary>
internal enum JpegColorSpace
{
    Grayscale = 0,

    YCbCr = 1,

    /// <summary>Three components that are R, G and B themselves (Adobe transform 0, or components named 'R', 'G', 'B').</summary>
    Rgb = 2,

    /// <summary>CMYK as ink amounts (255 is full ink).</summary>
    Cmyk = 3,

    /// <summary>CMYK as Adobe applications write it, inverted (255 is no ink).</summary>
    InvertedCmyk = 4,

    /// <summary>Adobe YCCK: inverted C, M, Y carried as YCbCr, and K.</summary>
    Ycck = 5,
}
