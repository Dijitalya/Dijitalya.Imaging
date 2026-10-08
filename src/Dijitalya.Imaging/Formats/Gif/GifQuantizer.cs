namespace Dijitalya.Imaging.Formats.Gif;

/// <summary>
/// Turns a picture into at most 256 palette indexes for GIF. A pixel at least half transparent
/// becomes the one transparent entry; the others keep their colour. A picture of few enough colours
/// keeps them exactly; one of more gets a palette chosen by median cut over its colour histogram and
/// is mapped onto it with Floyd-Steinberg error diffusion, so gradients do not turn into bands.
/// </summary>
internal sealed class GifQuantizer
{
    private const int MaxColors = 256;
    private const int AlphaThreshold = 128;

    // Histogram cells: 5 bits per channel.
    private const int CellBits = 5;
    private const int CellCount = 1 << (3 * CellBits);

    // The nearest-entry cache of the mapping is finer: 6 bits per channel.
    private const int LookupBits = 6;

    private GifQuantizer(byte[] palette, int colorCount, int transparentIndex, byte[] indexes)
    {
        Palette = palette;
        ColorCount = colorCount;
        TransparentIndex = transparentIndex;
        Indexes = indexes;
    }

    /// <summary>The palette, three bytes (R, G, B) per entry.</summary>
    public byte[] Palette { get; }

    public int ColorCount { get; }

    /// <summary>The entry of transparent pixels, or -1 when every pixel is opaque enough.</summary>
    public int TransparentIndex { get; }

    /// <summary>One palette index per pixel, rows top to bottom.</summary>
    public byte[] Indexes { get; }

    public static GifQuantizer Quantize(RasterImage image)
    {
        var pixels = image.Pixels;
        var count = image.Width * image.Height;
        var hasTransparency = false;
        var colors = new Dictionary<int, int>();

        for (var i = 0; i < count; i++)
        {
            if (pixels[(i * 4) + 3] < AlphaThreshold)
            {
                hasTransparency = true;
                continue;
            }

            var rgb = (pixels[i * 4] << 16) | (pixels[(i * 4) + 1] << 8) | pixels[(i * 4) + 2];

            if (colors.Count <= MaxColors)
                colors.TryAdd(rgb, colors.Count);
        }

        var available = MaxColors - (hasTransparency ? 1 : 0);

        return colors.Count <= available
            ? Exact(pixels, count, colors, hasTransparency)
            : Reduced(image, available, hasTransparency);
    }

    // Few enough colours: each keeps its own entry, nothing is lost.
    private static GifQuantizer Exact(byte[] pixels, int count, Dictionary<int, int> colors, bool hasTransparency)
    {
        var colorCount = colors.Count + (hasTransparency ? 1 : 0);
        var transparentIndex = hasTransparency ? colors.Count : -1;
        var palette = new byte[Math.Max(colorCount, 1) * 3];

        foreach (var (rgb, index) in colors)
        {
            palette[index * 3] = (byte)(rgb >> 16);
            palette[(index * 3) + 1] = (byte)(rgb >> 8);
            palette[(index * 3) + 2] = (byte)rgb;
        }

        var indexes = new byte[count];

        for (var i = 0; i < count; i++)
        {
            if (pixels[(i * 4) + 3] < AlphaThreshold)
            {
                indexes[i] = (byte)transparentIndex;
                continue;
            }

            var rgb = (pixels[i * 4] << 16) | (pixels[(i * 4) + 1] << 8) | pixels[(i * 4) + 2];
            indexes[i] = (byte)colors[rgb];
        }

        return new GifQuantizer(palette, Math.Max(colorCount, 1), transparentIndex, indexes);
    }

    private static GifQuantizer Reduced(RasterImage image, int available, bool hasTransparency)
    {
        var palette = MedianCut(image, available);
        var colorCount = (palette.Length / 3) + (hasTransparency ? 1 : 0);
        var transparentIndex = hasTransparency ? palette.Length / 3 : -1;
        var fullPalette = new byte[colorCount * 3];
        palette.CopyTo(fullPalette, 0);

        return new GifQuantizer(fullPalette, colorCount, transparentIndex, Dither(image, palette, transparentIndex));
    }

    // Splits the colour histogram into boxes, always the one with the widest spread, at its median.
    private static byte[] MedianCut(RasterImage image, int maxColors)
    {
        var pixels = image.Pixels;
        var counts = new long[CellCount];
        var sums = new long[CellCount * 3];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < AlphaThreshold)
                continue;

            var cell = Cell(pixels[i], pixels[i + 1], pixels[i + 2]);
            counts[cell]++;
            sums[cell * 3] += pixels[i];
            sums[(cell * 3) + 1] += pixels[i + 1];
            sums[(cell * 3) + 2] += pixels[i + 2];
        }

        var cells = new List<int>();

        for (var cell = 0; cell < CellCount; cell++)
        {
            if (counts[cell] > 0)
                cells.Add(cell);
        }

        // A box is a range of the cell list; its cells are re-sorted along its widest channel when it is split.
        var boxes = new List<(int Start, int Length)> { (0, cells.Count) };
        var cellArray = cells.ToArray();

        while (boxes.Count < maxColors)
        {
            var best = -1;
            var bestSpread = 0L;

            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Length < 2)
                    continue;

                var spread = Spread(cellArray, boxes[b], counts, out _);

                if (spread > bestSpread)
                {
                    bestSpread = spread;
                    best = b;
                }
            }

            if (best < 0)
                break;

            var box = boxes[best];
            Spread(cellArray, box, counts, out var channel);
            Array.Sort(cellArray, box.Start, box.Length, Comparer<int>.Create((a, c) => Channel(a, channel).CompareTo(Channel(c, channel))));

            // Split where half the box's pixels lie on each side.
            long total = 0;

            for (var i = box.Start; i < box.Start + box.Length; i++)
                total += counts[cellArray[i]];

            long running = 0;
            var split = box.Start + 1;

            for (var i = box.Start; i < box.Start + box.Length - 1; i++)
            {
                running += counts[cellArray[i]];
                split = i + 1;

                if (running * 2 >= total)
                    break;
            }

            boxes[best] = (box.Start, split - box.Start);
            boxes.Add((split, box.Start + box.Length - split));
        }

        var palette = new byte[boxes.Count * 3];

        for (var b = 0; b < boxes.Count; b++)
        {
            long weight = 0, red = 0, green = 0, blue = 0;

            for (var i = boxes[b].Start; i < boxes[b].Start + boxes[b].Length; i++)
            {
                var cell = cellArray[i];
                weight += counts[cell];
                red += sums[cell * 3];
                green += sums[(cell * 3) + 1];
                blue += sums[(cell * 3) + 2];
            }

            palette[b * 3] = (byte)((red + (weight / 2)) / weight);
            palette[(b * 3) + 1] = (byte)((green + (weight / 2)) / weight);
            palette[(b * 3) + 2] = (byte)((blue + (weight / 2)) / weight);
        }

        return palette;
    }

    // The pixel-weighted spread of the box's widest channel (range times pixels), and that channel.
    private static long Spread(int[] cells, (int Start, int Length) box, long[] counts, out int channel)
    {
        Span<int> minimum = [int.MaxValue, int.MaxValue, int.MaxValue];
        Span<int> maximum = [int.MinValue, int.MinValue, int.MinValue];
        long weight = 0;

        for (var i = box.Start; i < box.Start + box.Length; i++)
        {
            weight += counts[cells[i]];

            for (var c = 0; c < 3; c++)
            {
                var value = Channel(cells[i], c);
                minimum[c] = Math.Min(minimum[c], value);
                maximum[c] = Math.Max(maximum[c], value);
            }
        }

        // Green weighs most in perceived brightness, blue least.
        var red = (maximum[0] - minimum[0]) * 3;
        var green = (maximum[1] - minimum[1]) * 4;
        var blue = (maximum[2] - minimum[2]) * 2;
        channel = green >= red && green >= blue ? 1 : red >= blue ? 0 : 2;

        return Math.Max(red, Math.Max(green, blue)) * weight;
    }

    // Maps every pixel to its nearest palette entry, passing each pixel's error on to its neighbours.
    private static byte[] Dither(RasterImage image, byte[] palette, int transparentIndex)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = image.Pixels;
        var indexes = new byte[width * height];
        var nearest = new short[1 << (3 * LookupBits)];
        Array.Fill(nearest, (short)-1);

        // Errors of the current and the next row, three channels each, with a pixel of margin on both sides.
        var current = new int[(width + 2) * 3];
        var next = new int[(width + 2) * 3];

        for (var y = 0; y < height; y++)
        {
            Array.Clear(next);

            for (var x = 0; x < width; x++)
            {
                var source = ((y * width) + x) * 4;

                if (pixels[source + 3] < AlphaThreshold)
                {
                    indexes[(y * width) + x] = (byte)transparentIndex;
                    continue;
                }

                var e = (x + 1) * 3;
                var r = Math.Clamp(pixels[source] + ((current[e] + 8) >> 4), 0, 255);
                var g = Math.Clamp(pixels[source + 1] + ((current[e + 1] + 8) >> 4), 0, 255);
                var b = Math.Clamp(pixels[source + 2] + ((current[e + 2] + 8) >> 4), 0, 255);

                var key = ((r >> (8 - LookupBits)) << (2 * LookupBits)) | ((g >> (8 - LookupBits)) << LookupBits) | (b >> (8 - LookupBits));
                var index = nearest[key];

                if (index < 0)
                {
                    index = Nearest(palette, r, g, b);
                    nearest[key] = index;
                }

                indexes[(y * width) + x] = (byte)index;

                // Floyd-Steinberg: 7/16 right, 3/16 below left, 5/16 below, 1/16 below right (kept in sixteenths).
                Span<int> error = [r - palette[index * 3], g - palette[(index * 3) + 1], b - palette[(index * 3) + 2]];

                for (var c = 0; c < 3; c++)
                {
                    current[e + 3 + c] += error[c] * 7;
                    next[e - 3 + c] += error[c] * 3;
                    next[e + c] += error[c] * 5;
                    next[e + 3 + c] += error[c];
                }
            }

            (current, next) = (next, current);
        }

        return indexes;
    }

    private static short Nearest(byte[] palette, int r, int g, int b)
    {
        var best = 0;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < palette.Length / 3; i++)
        {
            var dr = r - palette[i * 3];
            var dg = g - palette[(i * 3) + 1];
            var db = b - palette[(i * 3) + 2];
            var distance = (dr * dr * 3) + (dg * dg * 4) + (db * db * 2);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return (short)best;
    }

    private static int Cell(int r, int g, int b) =>
        ((r >> (8 - CellBits)) << (2 * CellBits)) | ((g >> (8 - CellBits)) << CellBits) | (b >> (8 - CellBits));

    private static int Channel(int cell, int channel) => (cell >> ((2 - channel) * CellBits)) & ((1 << CellBits) - 1);
}
