namespace Dijitalya.Imaging.Formats.Bmp;

/// <summary>
/// What the pixel decoders of a bitmap share: the file, where its pixels start, the picture they
/// write into and the palette. Rows are addressed as the file stores them; bottom-up files are
/// turned the right way round here.
/// </summary>
internal sealed class BmpContext(byte[] data, int pixelOffset, RasterImage image, bool topDown, byte[] palette)
{
    public byte[] Data { get; } = data;

    public long PixelOffset { get; } = pixelOffset;

    public RasterImage Image { get; } = image;

    /// <summary>Writes palette entry <paramref name="index"/>; an index past the palette is black.</summary>
    public void SetIndexed(int x, int storedRow, int index)
    {
        var entry = index * 3;

        if (entry + 2 < palette.Length)
            SetColor(x, storedRow, palette[entry], palette[entry + 1], palette[entry + 2], 255);
        else
            SetColor(x, storedRow, 0, 0, 0, 255);
    }

    public void SetColor(int x, int storedRow, byte red, byte green, byte blue, byte alpha)
    {
        if (x < 0 || x >= Image.Width || storedRow < 0 || storedRow >= Image.Height)
            return;

        var y = topDown ? storedRow : Image.Height - 1 - storedRow;
        var target = ((y * Image.Width) + x) * 4;
        var pixels = Image.Pixels;

        pixels[target] = red;
        pixels[target + 1] = green;
        pixels[target + 2] = blue;
        pixels[target + 3] = alpha;
    }
}
