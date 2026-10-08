using System.Runtime.InteropServices;

namespace Dijitalya.Imaging.Processing;

/// <summary>
/// Lossless geometric operations: turning by right angles, mirroring, cropping and the EXIF
/// orientation that combines them. Each one returns a new picture and leaves its source as it was.
/// </summary>
public static class ImageTransforms
{
    /// <summary>
    /// Turns the picture upright as its EXIF orientation (1 to 8) says. Orientation 1, and any value
    /// outside 1 to 8, return the source itself.
    /// </summary>
    public static RasterImage AutoOrient(RasterImage source, int orientation)
    {
        ArgumentNullException.ThrowIfNull(source);

        return orientation switch
        {
            2 => FlipHorizontal(source),
            3 => Rotate(source, 180),
            4 => FlipVertical(source),
            5 => Transpose(source),
            6 => Rotate(source, 90),
            7 => Transverse(source),
            8 => Rotate(source, 270),
            _ => source,
        };
    }

    /// <summary>Turns the picture clockwise by 0, 90, 180 or 270 degrees (any multiple of 90).</summary>
    public static RasterImage Rotate(RasterImage source, int degrees)
    {
        ArgumentNullException.ThrowIfNull(source);

        var normalized = ((degrees % 360) + 360) % 360;

        if (normalized % 90 != 0)
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Only multiples of 90 degrees are supported.");

        var width = source.Width;
        var height = source.Height;
        var from = MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan());

        switch (normalized)
        {
            case 0:
                return source.Clone();

            case 180:
            {
                var result = new RasterImage(width, height);
                var to = MemoryMarshal.Cast<byte, uint>(result.Pixels.AsSpan());
                var last = from.Length - 1;

                for (var index = 0; index <= last; index++)
                    to[index] = from[last - index];

                return result;
            }

            case 90:
            {
                // The result is height wide: its column x is the source row height - 1 - x.
                var result = new RasterImage(height, width);
                var to = MemoryMarshal.Cast<byte, uint>(result.Pixels.AsSpan());

                for (var y = 0; y < width; y++)
                {
                    for (var x = 0; x < height; x++)
                        to[(y * height) + x] = from[((height - 1 - x) * width) + y];
                }

                return result;
            }

            default:
            {
                var result = new RasterImage(height, width);
                var to = MemoryMarshal.Cast<byte, uint>(result.Pixels.AsSpan());

                for (var y = 0; y < width; y++)
                {
                    for (var x = 0; x < height; x++)
                        to[(y * height) + x] = from[(x * width) + (width - 1 - y)];
                }

                return result;
            }
        }
    }

    /// <summary>Mirrors the picture left to right.</summary>
    public static RasterImage FlipHorizontal(RasterImage source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new RasterImage(source.Width, source.Height);
        var from = MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan());
        var to = MemoryMarshal.Cast<byte, uint>(result.Pixels.AsSpan());
        var width = source.Width;

        for (var y = 0; y < source.Height; y++)
        {
            var row = y * width;

            for (var x = 0; x < width; x++)
                to[row + x] = from[row + (width - 1 - x)];
        }

        return result;
    }

    /// <summary>Mirrors the picture top to bottom.</summary>
    public static RasterImage FlipVertical(RasterImage source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new RasterImage(source.Width, source.Height);
        var stride = source.Width * 4;

        for (var y = 0; y < source.Height; y++)
            Buffer.BlockCopy(source.Pixels, (source.Height - 1 - y) * stride, result.Pixels, y * stride, stride);

        return result;
    }

    /// <summary>The rectangle of the picture at (<paramref name="x"/>, <paramref name="y"/>); it must lie inside the picture.</summary>
    public static RasterImage Crop(RasterImage source, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > source.Width || y + height > source.Height)
            throw new ArgumentOutOfRangeException(nameof(width), "The rectangle must lie inside the picture.");

        var result = new RasterImage(width, height);
        var sourceStride = source.Width * 4;
        var stride = width * 4;

        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(source.Pixels, ((y + row) * sourceStride) + (x * 4), result.Pixels, row * stride, stride);

        return result;
    }

    /// <summary>Mirrors the picture across its top-left to bottom-right diagonal (EXIF orientation 5).</summary>
    internal static RasterImage Transpose(RasterImage source)
    {
        var width = source.Width;
        var height = source.Height;
        var result = new RasterImage(height, width);
        var from = MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan());
        var to = MemoryMarshal.Cast<byte, uint>(result.Pixels.AsSpan());

        for (var y = 0; y < width; y++)
        {
            for (var x = 0; x < height; x++)
                to[(y * height) + x] = from[(x * width) + y];
        }

        return result;
    }

    /// <summary>Mirrors the picture across its top-right to bottom-left diagonal (EXIF orientation 7).</summary>
    internal static RasterImage Transverse(RasterImage source)
    {
        var width = source.Width;
        var height = source.Height;
        var result = new RasterImage(height, width);
        var from = MemoryMarshal.Cast<byte, uint>(source.Pixels.AsSpan());
        var to = MemoryMarshal.Cast<byte, uint>(result.Pixels.AsSpan());

        for (var y = 0; y < width; y++)
        {
            for (var x = 0; x < height; x++)
                to[(y * height) + x] = from[((height - 1 - x) * width) + (width - 1 - y)];
        }

        return result;
    }

    /// <summary>Copies <paramref name="picture"/> onto <paramref name="canvas"/> with its top-left corner at (x, y); it must fit.</summary>
    internal static void Paste(RasterImage picture, RasterImage canvas, int x, int y)
    {
        var stride = picture.Width * 4;
        var canvasStride = canvas.Width * 4;

        for (var row = 0; row < picture.Height; row++)
            Buffer.BlockCopy(picture.Pixels, row * stride, canvas.Pixels, ((y + row) * canvasStride) + (x * 4), stride);
    }
}
