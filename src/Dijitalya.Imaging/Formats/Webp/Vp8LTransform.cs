namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>One transform of a VP8L image, as read before the image data and undone after it.</summary>
internal sealed class Vp8LTransform(int type, int width, int height, int bits, uint[] data)
{
    public const int Predictor = 0;
    public const int CrossColor = 1;
    public const int SubtractGreen = 2;
    public const int ColorIndexing = 3;

    public int Type { get; } = type;

    /// <summary>The width of the image the transform produces when it is undone.</summary>
    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>The tile size (log2) of predictor and cross-colour data, or the pixels-per-byte packing (log2) of colour indexing.</summary>
    public int Bits { get; } = bits;

    /// <summary>The sub-image of predictor and cross-colour data, or the colour table of colour indexing.</summary>
    public uint[] Data { get; } = data;
}
