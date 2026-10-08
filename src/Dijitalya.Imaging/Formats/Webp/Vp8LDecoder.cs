namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Decodes VP8L, the lossless WebP bitstream (RFC 9649), the way libwebp does: transforms
/// (predictor, cross colour, subtract green, colour indexing), the colour cache, meta prefix codes
/// and LZ77 backward references. Every count and distance read from the file is checked against the
/// picture before it is used, and reading past the end of the data is reported as a broken file.
/// </summary>
internal sealed class Vp8LDecoder
{
    private const byte Signature = 0x2F;
    private const int LiteralCodes = 256;
    private const int LengthCodes = 24;
    private const int DistanceCodes = 40;
    private const int MaxCacheBits = 11;
    private const int CodeLengthCodes = 19;
    private const int DefaultCodeLength = 8;
    private const uint OpaqueBlack = 0xFF000000;

    private static readonly int[] CodeLengthCodeOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
    private static readonly int[] CodeLengthExtraBits = [2, 3, 7];
    private static readonly int[] CodeLengthRepeatOffsets = [3, 3, 11];

    private readonly Vp8LBitReader _reader;
    private readonly List<Vp8LTransform> _transforms = [];
    private int _transformsSeen;

    private Vp8LDecoder(Vp8LBitReader reader)
    {
        _reader = reader;
    }

    /// <summary>Reads the size from the header of a VP8L chunk without decoding it.</summary>
    public static (int Width, int Height) ReadSize(byte[] data, int start, int length)
    {
        if (length < 5 || data[start] != Signature)
            throw new InvalidImageContentException("The WebP lossless header is invalid.");

        var reader = new Vp8LBitReader(data, start + 1, length - 1);
        var width = reader.ReadBits(14) + 1;
        var height = reader.ReadBits(14) + 1;
        reader.ReadBits(1);

        if (reader.ReadBits(3) != 0)
            throw new InvalidImageContentException("The WebP lossless version is not supported.");

        return (width, height);
    }

    /// <summary>Decodes a whole VP8L chunk, header included.</summary>
    public static RasterImage Decode(byte[] data, int start, int length)
    {
        var (width, height) = ReadSize(data, start, length);
        ImageLimits.EnsureSize(width, height);

        var reader = new Vp8LBitReader(data, start + 5, length - 5);
        var argb = new Vp8LDecoder(reader).DecodeImage(width, height);
        var image = new RasterImage(width, height);
        var pixels = image.Pixels;

        for (var index = 0; index < argb.Length; index++)
        {
            var value = argb[index];
            var target = index * 4;

            pixels[target] = (byte)(value >> 16);
            pixels[target + 1] = (byte)(value >> 8);
            pixels[target + 2] = (byte)value;
            pixels[target + 3] = (byte)(value >> 24);
        }

        return image;
    }

    /// <summary>
    /// Decodes the losslessly compressed alpha of an ALPH chunk: a VP8L stream without header whose
    /// size is the picture's, with the alpha in the green channel.
    /// </summary>
    public static byte[] DecodeAlpha(byte[] data, int start, int length, int width, int height)
    {
        var reader = new Vp8LBitReader(data, start, length);
        var argb = new Vp8LDecoder(reader).DecodeImage(width, height);
        var alpha = new byte[argb.Length];

        for (var index = 0; index < argb.Length; index++)
            alpha[index] = (byte)(argb[index] >> 8);

        return alpha;
    }

    private uint[] DecodeImage(int width, int height)
    {
        var codedWidth = width;

        while (_reader.ReadBits(1) == 1)
            codedWidth = ReadTransform(codedWidth, height);

        var pixels = DecodeImageStream(codedWidth, height, isMainImage: true);

        for (var index = _transforms.Count - 1; index >= 0; index--)
            pixels = InverseTransform(_transforms[index], pixels);

        return pixels;
    }

    private int ReadTransform(int width, int height)
    {
        var type = _reader.ReadBits(2);

        // Each transform may appear once.
        if ((_transformsSeen & (1 << type)) != 0)
            throw new InvalidImageContentException("A WebP lossless transform appears twice.");

        _transformsSeen |= 1 << type;

        switch (type)
        {
            case Vp8LTransform.Predictor:
            case Vp8LTransform.CrossColor:
            {
                var bits = _reader.ReadBits(3) + 2;
                var data = DecodeImageStream(SubSampleSize(width, bits), SubSampleSize(height, bits), isMainImage: false);
                _transforms.Add(new Vp8LTransform(type, width, height, bits, data));

                return width;
            }

            case Vp8LTransform.ColorIndexing:
            {
                var colors = _reader.ReadBits(8) + 1;
                var bits = colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;
                var table = DecodeImageStream(colors, 1, isMainImage: false);
                _transforms.Add(new Vp8LTransform(type, width, height, bits, ExpandColorTable(table, bits)));

                return SubSampleSize(width, bits);
            }

            default:
                _transforms.Add(new Vp8LTransform(type, width, height, 0, []));

                return width;
        }
    }

    // The table is stored as differences from the previous entry; indexes past it are transparent black.
    private static uint[] ExpandColorTable(uint[] table, int bits)
    {
        var expanded = new uint[1 << (8 >> bits)];
        expanded[0] = table[0];

        for (var index = 1; index < table.Length; index++)
            expanded[index] = AddPixels(table[index], expanded[index - 1]);

        return expanded;
    }

    private uint[] DecodeImageStream(int width, int height, bool isMainImage)
    {
        var cacheBits = 0;

        if (_reader.ReadBits(1) == 1)
        {
            cacheBits = _reader.ReadBits(4);

            if (cacheBits is < 1 or > MaxCacheBits)
                throw new InvalidImageContentException("The WebP colour cache size is invalid.");
        }

        var groupBits = 0;
        var groupWidth = 0;
        int[]? groupImage = null;
        var groupCount = 1;
        var groupCountInFile = 1;
        int[]? groupMapping = null;

        // Only the main image may switch prefix codes by region.
        if (isMainImage && _reader.ReadBits(1) == 1)
        {
            groupBits = _reader.ReadBits(3) + 2;
            groupWidth = SubSampleSize(width, groupBits);

            var groupPixels = DecodeImageStream(groupWidth, SubSampleSize(height, groupBits), isMainImage: false);
            groupImage = new int[groupPixels.Length];

            for (var index = 0; index < groupPixels.Length; index++)
            {
                var group = (int)((groupPixels[index] >> 8) & 0xFFFF);
                groupImage[index] = group;
                groupCountInFile = Math.Max(groupCountInFile, group + 1);
            }

            // Groups the image never names are read and dropped; the used ones are numbered densely.
            groupMapping = new int[groupCountInFile];
            Array.Fill(groupMapping, -1);
            groupCount = 0;

            for (var index = 0; index < groupImage.Length; index++)
            {
                ref var mapped = ref groupMapping[groupImage[index]];

                if (mapped < 0)
                    mapped = groupCount++;

                groupImage[index] = mapped;
            }
        }

        if (_reader.IsEndOfStream)
            throw new InvalidImageContentException("The WebP data is cut off.");

        // Each group takes at least 20 bits, so a file cannot claim more groups than it has room for.
        if (_reader.RemainingBits + 64 < (long)groupCountInFile * 20)
            throw new InvalidImageContentException("The WebP data is cut off.");

        var groups = new Vp8LHuffmanGroup[groupCount];
        var greenSize = LiteralCodes + LengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0);
        var lengths = new int[Math.Max(greenSize, 256)];

        for (var index = 0; index < groupCountInFile; index++)
        {
            var group = new Vp8LHuffmanGroup(
                ReadCode(greenSize, lengths),
                ReadCode(256, lengths),
                ReadCode(256, lengths),
                ReadCode(256, lengths),
                ReadCode(DistanceCodes, lengths));

            var target = groupMapping is null ? index : groupMapping[index];

            if (target >= 0)
                groups[target] = group;
        }

        var pixels = new uint[width * height];
        DecodePixels(pixels, width, height, groups, groupImage, groupBits, groupWidth, cacheBits);

        if (_reader.IsEndOfStream)
            throw new InvalidImageContentException("The WebP data is cut off.");

        return pixels;
    }

    private Vp8LHuffmanCode ReadCode(int alphabetSize, int[] lengths)
    {
        Array.Clear(lengths);

        if (_reader.ReadBits(1) == 1)
        {
            // A simple code: one or two symbols, each one bit long.
            var symbols = _reader.ReadBits(1) + 1;
            var first = _reader.ReadBits(_reader.ReadBits(1) == 0 ? 1 : 8);
            lengths[first] = 1;

            if (symbols == 2)
                lengths[_reader.ReadBits(8)] = 1;
        }
        else
        {
            Span<int> codeLengthLengths = stackalloc int[CodeLengthCodes];
            var count = _reader.ReadBits(4) + 4;

            for (var index = 0; index < count; index++)
                codeLengthLengths[CodeLengthCodeOrder[index]] = _reader.ReadBits(3);

            ReadCodeLengths(Vp8LHuffmanCode.Create(codeLengthLengths), alphabetSize, lengths);
        }

        if (_reader.IsEndOfStream)
            throw new InvalidImageContentException("The WebP data is cut off.");

        return Vp8LHuffmanCode.Create(lengths.AsSpan(0, alphabetSize));
    }

    private void ReadCodeLengths(Vp8LHuffmanCode lengthCode, int symbolCount, int[] lengths)
    {
        var maxSymbol = symbolCount;

        if (_reader.ReadBits(1) == 1)
        {
            var lengthBits = 2 + (2 * _reader.ReadBits(3));
            maxSymbol = 2 + _reader.ReadBits(lengthBits);

            if (maxSymbol > symbolCount)
                throw new InvalidImageContentException("A WebP prefix code is too long.");
        }

        var symbol = 0;
        var previous = DefaultCodeLength;

        while (symbol < symbolCount && maxSymbol-- > 0)
        {
            var codeLength = lengthCode.Decode(_reader);

            if (codeLength < 16)
            {
                lengths[symbol++] = codeLength;

                if (codeLength != 0)
                    previous = codeLength;

                continue;
            }

            var slot = codeLength - 16;
            var repeat = _reader.ReadBits(CodeLengthExtraBits[slot]) + CodeLengthRepeatOffsets[slot];

            if (symbol + repeat > symbolCount)
                throw new InvalidImageContentException("A WebP prefix code is too long.");

            var value = codeLength == 16 ? previous : 0;

            while (repeat-- > 0)
                lengths[symbol++] = value;
        }
    }

    private void DecodePixels(uint[] pixels, int width, int height, Vp8LHuffmanGroup[] groups, int[]? groupImage, int groupBits, int groupWidth, int cacheBits)
    {
        var total = width * height;
        var cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        var cacheShift = 32 - cacheBits;
        var lengthLimit = LiteralCodes + LengthCodes;
        var cacheLimit = lengthLimit + (cache?.Length ?? 0);
        var mask = groupImage is null ? -1 : (1 << groupBits) - 1;
        var position = 0;
        var x = 0;
        var y = 0;
        var group = groups[groupImage is null ? 0 : groupImage[0]];

        while (position < total)
        {
            if ((x & mask) == 0 && groupImage is not null)
                group = groups[groupImage[((y >> groupBits) * groupWidth) + (x >> groupBits)]];

            var code = group.Green.Decode(_reader);

            if (code < LiteralCodes)
            {
                var red = group.Red.Decode(_reader);
                var blue = group.Blue.Decode(_reader);
                var alpha = group.Alpha.Decode(_reader);

                if (_reader.IsEndOfStream)
                    break;

                var value = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)code << 8) | (uint)blue;
                pixels[position++] = value;

                if (cache is not null)
                    cache[(value * 0x1E35A7BDu) >> cacheShift] = value;

                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
            else if (code < lengthLimit)
            {
                var length = ReadCopyValue(code - LiteralCodes);
                var distanceCode = ReadCopyValue(group.Distance.Decode(_reader));
                var distance = Vp8LDistanceCoder.ToDistance(width, distanceCode);

                if (_reader.IsEndOfStream)
                    break;

                if (position < distance || total - position < length)
                    throw new InvalidImageContentException("A WebP backward reference points outside the picture.");

                for (var index = 0; index < length; index++)
                {
                    var value = pixels[position - distance];
                    pixels[position++] = value;

                    if (cache is not null)
                        cache[(value * 0x1E35A7BDu) >> cacheShift] = value;
                }

                x += length;

                while (x >= width)
                {
                    x -= width;
                    y++;
                }

                if ((x & mask) != 0 && position < total && groupImage is not null)
                    group = groups[groupImage[((y >> groupBits) * groupWidth) + (x >> groupBits)]];
            }
            else if (code < cacheLimit)
            {
                if (_reader.IsEndOfStream)
                    break;

                // A cached colour is already in the cache at its own key, so it needs no insertion.
                pixels[position++] = cache![code - lengthLimit];

                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
            else
            {
                throw new InvalidImageContentException("The WebP data holds an invalid symbol.");
            }
        }
    }

    private int ReadCopyValue(int symbol)
    {
        if (symbol < 4)
            return symbol + 1;

        var extraBits = (symbol - 2) >> 1;
        var offset = (2 + (symbol & 1)) << extraBits;

        return offset + _reader.ReadBits(extraBits) + 1;
    }

    private static uint[] InverseTransform(Vp8LTransform transform, uint[] pixels)
    {
        switch (transform.Type)
        {
            case Vp8LTransform.Predictor:
                InversePredictor(transform, pixels);
                return pixels;

            case Vp8LTransform.CrossColor:
                InverseCrossColor(transform, pixels);
                return pixels;

            case Vp8LTransform.SubtractGreen:
                for (var index = 0; index < pixels.Length; index++)
                {
                    var argb = pixels[index];
                    var green = (argb >> 8) & 0xFF;
                    var redBlue = ((argb & 0x00FF00FFu) + ((green << 16) | green)) & 0x00FF00FFu;
                    pixels[index] = (argb & 0xFF00FF00u) | redBlue;
                }

                return pixels;

            default:
                return InverseColorIndexing(transform, pixels);
        }
    }

    // In place: every prediction reads only pixels that are already final.
    private static void InversePredictor(Vp8LTransform transform, uint[] pixels)
    {
        var width = transform.Width;
        var height = transform.Height;
        var bits = transform.Bits;
        var tilesPerRow = SubSampleSize(width, bits);
        var modes = transform.Data;

        // The first row predicts from the left; its first pixel from opaque black.
        pixels[0] = AddPixels(pixels[0], OpaqueBlack);

        for (var x = 1; x < width; x++)
            pixels[x] = AddPixels(pixels[x], pixels[x - 1]);

        for (var y = 1; y < height; y++)
        {
            var row = y * width;
            var tileRow = (y >> bits) * tilesPerRow;

            // The first pixel of a row predicts from the one above it.
            pixels[row] = AddPixels(pixels[row], pixels[row - width]);

            for (var x = 1; x < width; x++)
            {
                var index = row + x;
                var mode = (int)((modes[tileRow + (x >> bits)] >> 8) & 0xF);
                pixels[index] = AddPixels(pixels[index], Predict(mode, pixels, index, width));
            }
        }
    }

    private static uint Predict(int mode, uint[] pixels, int index, int width)
    {
        var left = pixels[index - 1];
        var top = pixels[index - width];

        // The top-right of the last pixel of a row is the first pixel of the row itself, as in libwebp.
        return mode switch
        {
            0 => OpaqueBlack,
            1 => left,
            2 => top,
            3 => pixels[index - width + 1],
            4 => pixels[index - width - 1],
            5 => Average2(Average2(left, pixels[index - width + 1]), top),
            6 => Average2(left, pixels[index - width - 1]),
            7 => Average2(left, top),
            8 => Average2(pixels[index - width - 1], top),
            9 => Average2(top, pixels[index - width + 1]),
            10 => Average2(Average2(left, pixels[index - width - 1]), Average2(top, pixels[index - width + 1])),
            11 => Select(top, left, pixels[index - width - 1]),
            12 => ClampedAddSubtractFull(left, top, pixels[index - width - 1]),
            13 => ClampedAddSubtractHalf(left, top, pixels[index - width - 1]),
            _ => OpaqueBlack,
        };
    }

    private static void InverseCrossColor(Vp8LTransform transform, uint[] pixels)
    {
        var width = transform.Width;
        var height = transform.Height;
        var bits = transform.Bits;
        var tilesPerRow = SubSampleSize(width, bits);
        var codes = transform.Data;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var tileRow = (y >> bits) * tilesPerRow;

            for (var x = 0; x < width; x++)
            {
                var code = codes[tileRow + (x >> bits)];
                var greenToRed = (sbyte)code;
                var greenToBlue = (sbyte)(code >> 8);
                var redToBlue = (sbyte)(code >> 16);

                var argb = pixels[row + x];
                var green = (sbyte)(argb >> 8);
                var red = (int)((argb >> 16) & 0xFF);
                var blue = (int)(argb & 0xFF);

                red = (red + ((greenToRed * green) >> 5)) & 0xFF;
                blue += (greenToBlue * green) >> 5;
                blue += (redToBlue * (sbyte)red) >> 5;
                blue &= 0xFF;

                pixels[row + x] = (argb & 0xFF00FF00u) | ((uint)red << 16) | (uint)blue;
            }
        }
    }

    private static uint[] InverseColorIndexing(Vp8LTransform transform, uint[] pixels)
    {
        var width = transform.Width;
        var height = transform.Height;
        var bits = transform.Bits;
        var table = transform.Data;
        var output = new uint[width * height];

        if (bits == 0)
        {
            for (var index = 0; index < output.Length; index++)
                output[index] = table[(pixels[index] >> 8) & 0xFF];

            return output;
        }

        var packedWidth = SubSampleSize(width, bits);
        var bitsPerPixel = 8 >> bits;
        var pixelsPerByte = 1 << bits;
        var bitMask = (1u << bitsPerPixel) - 1;

        for (var y = 0; y < height; y++)
        {
            var source = y * packedWidth;
            var target = y * width;

            for (var x = 0; x < width; x += pixelsPerByte)
            {
                var packed = (pixels[source++] >> 8) & 0xFF;
                var end = Math.Min(x + pixelsPerByte, width);

                for (var column = x; column < end; column++)
                {
                    output[target + column] = table[packed & bitMask];
                    packed >>= bitsPerPixel;
                }
            }
        }

        return output;
    }

    private static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    private static uint AddPixels(uint a, uint b)
    {
        var alphaGreen = (a & 0xFF00FF00u) + (b & 0xFF00FF00u);
        var redBlue = (a & 0x00FF00FFu) + (b & 0x00FF00FFu);

        return (alphaGreen & 0xFF00FF00u) | (redBlue & 0x00FF00FFu);
    }

    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);

    private static int Clip255(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

    private static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
    {
        var a = Clip255((int)(c0 >> 24) + (int)(c1 >> 24) - (int)(c2 >> 24));
        var r = Clip255((int)((c0 >> 16) & 0xFF) + (int)((c1 >> 16) & 0xFF) - (int)((c2 >> 16) & 0xFF));
        var g = Clip255((int)((c0 >> 8) & 0xFF) + (int)((c1 >> 8) & 0xFF) - (int)((c2 >> 8) & 0xFF));
        var b = Clip255((int)(c0 & 0xFF) + (int)(c1 & 0xFF) - (int)(c2 & 0xFF));

        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int AddSubtractHalf(int a, int b) => Clip255(a + ((a - b) / 2));

    private static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
    {
        var average = Average2(c0, c1);
        var a = AddSubtractHalf((int)(average >> 24), (int)(c2 >> 24));
        var r = AddSubtractHalf((int)((average >> 16) & 0xFF), (int)((c2 >> 16) & 0xFF));
        var g = AddSubtractHalf((int)((average >> 8) & 0xFF), (int)((c2 >> 8) & 0xFF));
        var b = AddSubtractHalf((int)(average & 0xFF), (int)(c2 & 0xFF));

        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static uint Select(uint a, uint b, uint c)
    {
        var difference =
            Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24)) +
            Sub3((int)((a >> 16) & 0xFF), (int)((b >> 16) & 0xFF), (int)((c >> 16) & 0xFF)) +
            Sub3((int)((a >> 8) & 0xFF), (int)((b >> 8) & 0xFF), (int)((c >> 8) & 0xFF)) +
            Sub3((int)(a & 0xFF), (int)(b & 0xFF), (int)(c & 0xFF));

        return difference <= 0 ? a : b;
    }

    private static int Sub3(int a, int b, int c) => Math.Abs(b - c) - Math.Abs(a - c);
}
