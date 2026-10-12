namespace Dijitalya.Imaging.Formats.Png;

/// <summary>Turns the unfiltered rows of a PNG into RGBA pixels, for every colour type and bit depth.</summary>
internal sealed class PngPixelWriter(PngHeader header, byte[]? palette, byte[]? paletteAlpha, int[]? transparentSample)
{
    public PngHeader Header { get; } = header;

    /// <summary>Writes one row of <paramref name="width"/> samples to row <paramref name="y"/>, every <paramref name="stepX"/> pixels from <paramref name="startX"/>.</summary>
    public void WriteRow(byte[] row, int width, RasterImage image, int startX, int y, int stepX)
    {
        var pixels = image.Pixels;
        var rowStart = y * image.Width * 4;
        var depth = Header.BitDepth;

        for (var column = 0; column < width; column++)
        {
            var target = rowStart + ((startX + (column * stepX)) * 4);

            switch (Header.ColorType)
            {
                case PngDecoder.Grayscale:
                {
                    var sample = Sample(row, column, depth);
                    var gray = ScaleTo8(sample, depth);
                    pixels[target] = pixels[target + 1] = pixels[target + 2] = gray;
                    pixels[target + 3] = transparentSample is not null && sample == transparentSample[0] ? (byte)0 : (byte)255;
                    break;
                }

                case PngDecoder.Indexed:
                {
                    var index = Sample(row, column, depth);
                    var entry = index * 3;

                    if (palette is not null && entry + 2 < palette.Length)
                    {
                        pixels[target] = palette[entry];
                        pixels[target + 1] = palette[entry + 1];
                        pixels[target + 2] = palette[entry + 2];
                    }
                    else
                    {
                        if (DecodeContext.Strict)
                            throw new InvalidImageContentException("A PNG pixel references an undefined palette entry.");
                        pixels[target] = pixels[target + 1] = pixels[target + 2] = 0;
                    }

                    pixels[target + 3] = paletteAlpha is not null && index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
                    break;
                }

                case PngDecoder.Truecolor:
                {
                    if (depth == 8)
                    {
                        var source = column * 3;
                        pixels[target] = row[source];
                        pixels[target + 1] = row[source + 1];
                        pixels[target + 2] = row[source + 2];
                        pixels[target + 3] = transparentSample is not null
                            && row[source] == transparentSample[0] && row[source + 1] == transparentSample[1] && row[source + 2] == transparentSample[2]
                            ? (byte)0
                            : (byte)255;
                    }
                    else
                    {
                        var source = column * 6;
                        var red = (row[source] << 8) | row[source + 1];
                        var green = (row[source + 2] << 8) | row[source + 3];
                        var blue = (row[source + 4] << 8) | row[source + 5];
                        pixels[target] = row[source];
                        pixels[target + 1] = row[source + 2];
                        pixels[target + 2] = row[source + 4];
                        pixels[target + 3] = transparentSample is not null
                            && red == transparentSample[0] && green == transparentSample[1] && blue == transparentSample[2]
                            ? (byte)0
                            : (byte)255;
                    }

                    break;
                }

                case PngDecoder.GrayscaleAlpha:
                {
                    var size = depth / 8;
                    var source = column * 2 * size;
                    pixels[target] = pixels[target + 1] = pixels[target + 2] = row[source];
                    pixels[target + 3] = row[source + size];
                    break;
                }

                default:
                {
                    var size = depth / 8;
                    var source = column * 4 * size;
                    pixels[target] = row[source];
                    pixels[target + 1] = row[source + size];
                    pixels[target + 2] = row[source + (2 * size)];
                    pixels[target + 3] = row[source + (3 * size)];
                    break;
                }
            }
        }
    }

    private static int Sample(byte[] row, int column, int depth)
    {
        switch (depth)
        {
            case 8:
                return row[column];

            case 16:
                return (row[column * 2] << 8) | row[(column * 2) + 1];

            default:
            {
                var bit = column * depth;
                var shift = 8 - depth - (bit & 7);
                return (row[bit >> 3] >> shift) & ((1 << depth) - 1);
            }
        }
    }

    private static byte ScaleTo8(int sample, int depth) => depth switch
    {
        16 => (byte)(sample >> 8),
        8 => (byte)sample,
        _ => (byte)(sample * 255 / ((1 << depth) - 1)),
    };
}
