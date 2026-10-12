using System.Buffers.Binary;
using System.IO.Compression;
using Dijitalya.Imaging.Common;
using Dijitalya.Imaging.Metadata;

namespace Dijitalya.Imaging.Formats.Png;

/// <summary>
/// Decodes PNG (ISO/IEC 15948): every colour type and bit depth, Adam7 interlacing and tRNS
/// transparency. Sixteen-bit samples keep their high byte. Gamma and colour profiles are not applied,
/// as browsers that ignore them show the picture; an RGB ICC profile is handed on with the pixels.
/// An animated PNG yields its default image.
/// </summary>
internal static class PngDecoder
{
    internal const int Grayscale = 0;
    internal const int Truecolor = 2;
    internal const int Indexed = 3;
    internal const int GrayscaleAlpha = 4;
    internal const int TruecolorAlpha = 6;

    // Real profiles are a few hundred kilobytes at most; this only stops a decompression bomb.
    private static int MaxIccProfileSize => DecodeContext.MaxMetadataBytes;

    // Adam7: the first column and row, and the step, of each of the seven passes.
    private static readonly int[] PassStartX = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] PassStartY = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] PassStepX = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] PassStepY = [8, 8, 8, 4, 4, 2, 2];

    public static DecodedImage Decode(byte[] data)
    {
        var header = default(PngHeader);
        var hasHeader = false;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        int[]? transparentSample = null;
        var orientation = 1;
        byte[]? iccProfile = null;
        var segments = new List<(int Offset, int Length)>();
        var hasEnd = false;
        var hasData = false;
        var dataEnded = false;
        var position = 8;

        while (position + 12 <= data.Length)
        {
            DecodeContext.Checkpoint();
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));

            if (length > (uint)(data.Length - position - 12))
            {
                if (DecodeContext.Strict)
                    throw new InvalidImageContentException("A PNG chunk is truncated.");
                // Best-effort recovery is available only when explicitly requested.
                break;
            }

            var type = data.AsSpan(position + 4, 4);
            var body = data.AsSpan(position + 8, (int)length);
            var isCritical = (type[0] & 0x20) == 0;

            if (isCritical || DecodeContext.Strict)
            {
                var expected = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 8 + (int)length, 4));

                if (Crc32.Compute(data.AsSpan(position + 4, (int)length + 4)) != expected)
                    throw new InvalidImageContentException("A PNG chunk fails its checksum.");
            }

            var payloadOffset = position + 8;
            position += (int)length + 12;
            if (hasData && !type.SequenceEqual("IDAT"u8))
                dataEnded = true;

            if (type.SequenceEqual("IHDR"u8))
            {
                if (DecodeContext.Strict && (hasHeader || body.Length != 13))
                    throw new InvalidImageContentException("The PNG must contain one 13-byte IHDR chunk.");
                header = ReadHeader(body);
                hasHeader = true;
            }
            else if (!hasHeader)
            {
                throw new InvalidImageContentException("The PNG does not start with its header.");
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (DecodeContext.Strict && (hasData || palette is not null || header.ColorType is Grayscale or GrayscaleAlpha))
                    throw new InvalidImageContentException("The PNG palette is duplicated, misplaced or unsupported.");
                if (body.Length % 3 != 0 || body.Length == 0 || body.Length > 768)
                    throw new InvalidImageContentException("The PNG palette is malformed.");

                palette = DecodeContext.Copy(body);
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                if (DecodeContext.Strict && (hasData || paletteAlpha is not null || transparentSample is not null))
                    throw new InvalidImageContentException("The PNG transparency chunk is misplaced or duplicated.");
                if (DecodeContext.Strict && !(header.ColorType == Indexed && palette is not null && body.Length > 0 && body.Length <= palette.Length / 3
                    || header.ColorType == Grayscale && body.Length == 2 || header.ColorType == Truecolor && body.Length == 6))
                    throw new InvalidImageContentException("The PNG transparency chunk is invalid.");
                if (header.ColorType == Indexed)
                {
                    paletteAlpha = DecodeContext.Copy(body);
                }
                else if (header.ColorType == Grayscale && body.Length >= 2)
                {
                    transparentSample = [BinaryPrimitives.ReadUInt16BigEndian(body)];
                }
                else if (header.ColorType == Truecolor && body.Length >= 6)
                {
                    transparentSample =
                    [
                        BinaryPrimitives.ReadUInt16BigEndian(body),
                        BinaryPrimitives.ReadUInt16BigEndian(body[2..]),
                        BinaryPrimitives.ReadUInt16BigEndian(body[4..]),
                    ];
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (DecodeContext.Strict && (dataEnded || header.ColorType == Indexed && palette is null))
                    throw new InvalidImageContentException("The PNG image data is out of order.");
                hasData = true;
                DecodeContext.Charge(32); // Segment storage, including list growth.
                segments.Add((payloadOffset, body.Length));
            }
            else if (type.SequenceEqual("eXIf"u8))
            {
                orientation = ExifOrientationReader.Read(body);
            }
            else if (type.SequenceEqual("iCCP"u8))
            {
                iccProfile ??= ReadIccProfile(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (DecodeContext.Strict && body.Length != 0)
                    throw new InvalidImageContentException("The PNG end chunk is invalid.");
                hasEnd = true;
                break;
            }
            else if (isCritical && DecodeContext.Strict)
            {
                throw new InvalidImageContentException("The PNG contains an unknown critical chunk.");
            }
        }

        if (DecodeContext.Strict && !hasEnd)
            throw new InvalidImageContentException("The PNG is missing its end chunk.");
        if (!hasHeader || !hasData)
            throw new InvalidImageContentException("The PNG has no image data.");

        if (header.ColorType == Indexed && palette is null)
            throw new InvalidImageContentException("The indexed PNG has no palette.");

        using var compressed = new PngDataStream(data, segments);
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        var reader = new PixelDataReader(inflater, data, segments);
        var image = new RasterImage(header.Width, header.Height);
        var writer = new PngPixelWriter(header, palette, paletteAlpha, transparentSample);

        if (header.Interlaced)
        {
            for (var pass = 0; pass < 7; pass++)
            {
                var passWidth = PassLength(header.Width, PassStartX[pass], PassStepX[pass]);
                var passHeight = PassLength(header.Height, PassStartY[pass], PassStepY[pass]);

                if (passWidth == 0 || passHeight == 0)
                    continue;

                DecodePass(reader, passWidth, passHeight, writer, image, PassStartX[pass], PassStartY[pass], PassStepX[pass], PassStepY[pass]);
            }
        }
        else
        {
            DecodePass(reader, header.Width, header.Height, writer, image, 0, 0, 1, 1);
        }

        reader.Complete();
        return new DecodedImage(image, ImageFormat.Png, orientation, iccProfile);
    }

    // iCCP: a name of 1 to 79 bytes, a zero byte, compression method 0, then the zlib-compressed
    // profile. A damaged one is ignored: the picture does not depend on it.
    private static byte[]? ReadIccProfile(ReadOnlySpan<byte> body)
    {
        var nameEnd = body[..Math.Min(body.Length, 80)].IndexOf((byte)0);

        if (nameEnd < 1 || nameEnd + 2 > body.Length || body[nameEnd + 1] != 0)
            return null;

        try
        {
            using var input = new MemoryStream(DecodeContext.Copy(body[(nameEnd + 2)..]));
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = DecodeContext.Allocate<byte>(16384);
            int read;

            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                DecodeContext.Checkpoint();
                if (output.Length + read > MaxIccProfileSize)
                    throw new InvalidImageContentException("The PNG colour profile exceeds the metadata limit.");
                DecodeContext.Charge(read * 3L);
                output.Write(buffer, 0, read);
            }

            return IccProfiles.RgbOrNull(output.ToArray());
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static PngHeader ReadHeader(ReadOnlySpan<byte> body)
    {
        if (body.Length < 13)
            throw new InvalidImageContentException("The PNG header is too short.");

        var width = BinaryPrimitives.ReadUInt32BigEndian(body);
        var height = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        int bitDepth = body[8];
        int colorType = body[9];

        ImageLimits.EnsureSize(width, height);

        var validDepth = colorType switch
        {
            Grayscale => bitDepth is 1 or 2 or 4 or 8 or 16,
            Indexed => bitDepth is 1 or 2 or 4 or 8,
            Truecolor or GrayscaleAlpha or TruecolorAlpha => bitDepth is 8 or 16,
            _ => false,
        };

        if (!validDepth || body[10] != 0 || body[11] != 0 || body[12] > 1)
            throw new InvalidImageContentException("The PNG header describes an unsupported image.");

        var channels = colorType switch
        {
            Truecolor => 3,
            GrayscaleAlpha => 2,
            TruecolorAlpha => 4,
            _ => 1,
        };

        return new PngHeader((int)width, (int)height, bitDepth, colorType, channels, body[12] == 1);
    }

    private static int RowLength(PngHeader header, int width) => (int)((((long)width * header.Channels * header.BitDepth) + 7) / 8);

    private static int PassLength(int length, int start, int step) => length <= start ? 0 : ((length - start + step - 1) / step);

    // Read one row at a time. A truncated stream is recoverable only in tolerant mode.
    private sealed class PixelDataReader(ZLibStream inflater, byte[] data, IReadOnlyList<(int Offset, int Length)> segments)
    {
        private bool _ended;
        private long _read;
        private uint _adlerA = 1;
        private uint _adlerB;

        public void Read(Span<byte> destination)
        {
            destination.Clear();
            var filled = 0;
            while (!_ended && filled < destination.Length)
            {
                DecodeContext.Checkpoint();
                int count;
                try { count = inflater.Read(destination[filled..]); }
                catch (InvalidDataException) when (!DecodeContext.Strict && _read > 0) { count = 0; }
                if (count == 0)
                {
                    _ended = true;
                    if (DecodeContext.Strict)
                        throw new InvalidImageContentException("The PNG pixel data is truncated.");
                    break;
                }
                if (DecodeContext.Strict)
                {
                    var bytes = destination.Slice(filled, count);
                    while (!bytes.IsEmpty)
                    {
                        var block = bytes[..Math.Min(bytes.Length, 5552)];
                        foreach (var value in block)
                        {
                            _adlerA += value;
                            _adlerB += _adlerA;
                        }
                        _adlerA %= 65521;
                        _adlerB %= 65521;
                        bytes = bytes[block.Length..];
                    }
                }
                filled += count;
                _read += count;
            }
        }

        public void Complete()
        {
            if (_read == 0)
                throw new InvalidImageContentException("The PNG has no readable pixel data.");
            if (DecodeContext.Strict)
            {
                if (inflater.ReadByte() != -1)
                    throw new InvalidImageContentException("The PNG contains excess decompressed pixel data.");
                Span<byte> trailer = stackalloc byte[4];
                var remaining = 4;
                for (var index = segments.Count - 1; index >= 0 && remaining > 0; index--)
                {
                    var segment = segments[index];
                    var count = Math.Min(remaining, segment.Length);
                    data.AsSpan(segment.Offset + segment.Length - count, count).CopyTo(trailer.Slice(remaining - count, count));
                    remaining -= count;
                }
                if (remaining != 0 || BinaryPrimitives.ReadUInt32BigEndian(trailer) != (_adlerB << 16 | _adlerA))
                    throw new InvalidImageContentException("The PNG zlib checksum is missing or invalid.");
            }
        }
    }

    private static void DecodePass(
        PixelDataReader reader, int width, int height, PngPixelWriter writer, RasterImage image,
        int startX, int startY, int stepX, int stepY)
    {
        var header = writer.Header;
        var rowLength = RowLength(header, width);
        var bytesPerPixel = Math.Max(1, (header.Channels * header.BitDepth) / 8);
        var previous = DecodeContext.Allocate<byte>(rowLength);
        var current = DecodeContext.Allocate<byte>(rowLength);
        Span<byte> filterByte = stackalloc byte[1];

        for (var row = 0; row < height; row++)
        {
            DecodeContext.Checkpoint();
            reader.Read(filterByte);
            var filter = filterByte[0];
            reader.Read(current);

            Unfilter(filter, current, previous, bytesPerPixel);
            writer.WriteRow(current, width, image, startX, startY + (row * stepY), stepX);

            (previous, current) = (current, previous);
        }

    }

    private static void Unfilter(byte filter, byte[] row, byte[] previous, int bytesPerPixel)
    {
        switch (filter)
        {
            case 0:
                return;

            case 1:
                for (var index = bytesPerPixel; index < row.Length; index++)
                    row[index] = (byte)(row[index] + row[index - bytesPerPixel]);
                return;

            case 2:
                for (var index = 0; index < row.Length; index++)
                    row[index] = (byte)(row[index] + previous[index]);
                return;

            case 3:
                for (var index = 0; index < row.Length; index++)
                {
                    var left = index >= bytesPerPixel ? row[index - bytesPerPixel] : 0;
                    row[index] = (byte)(row[index] + ((left + previous[index]) >> 1));
                }

                return;

            case 4:
                for (var index = 0; index < row.Length; index++)
                {
                    var left = index >= bytesPerPixel ? row[index - bytesPerPixel] : 0;
                    var upperLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : 0;
                    row[index] = (byte)(row[index] + Paeth(left, previous[index], upperLeft));
                }

                return;

            default:
                throw new InvalidImageContentException("The PNG uses an unknown row filter.");
        }
    }

    internal static int Paeth(int left, int above, int upperLeft)
    {
        var estimate = left + above - upperLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceAbove = Math.Abs(estimate - above);
        var distanceUpperLeft = Math.Abs(estimate - upperLeft);

        if (distanceLeft <= distanceAbove && distanceLeft <= distanceUpperLeft)
            return left;

        return distanceAbove <= distanceUpperLeft ? above : upperLeft;
    }
}
