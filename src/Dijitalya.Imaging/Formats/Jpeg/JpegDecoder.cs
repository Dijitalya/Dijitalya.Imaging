using Dijitalya.Imaging.Metadata;

namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>
/// Decodes JPEG (ITU-T T.81) with Huffman coding: baseline, extended sequential (8-bit) and
/// progressive, any sampling factors, restart intervals, and grayscale, YCbCr, RGB, CMYK and YCCK
/// pictures. Arithmetic coding, lossless and hierarchical JPEG and 12-bit samples are refused, as
/// they are practically never met. The EXIF orientation is read from APP1 and an RGB ICC profile
/// from APP2.
/// </summary>
internal static class JpegDecoder
{
    private const int Sof0 = 0xC0;
    private const int Sof1 = 0xC1;
    private const int Sof2 = 0xC2;
    private const int Dht = 0xC4;
    private const int Rst0 = 0xD0;
    private const int Rst7 = 0xD7;
    private const int Soi = 0xD8;
    private const int Eoi = 0xD9;
    private const int Sos = 0xDA;
    private const int Dqt = 0xDB;
    private const int Dri = 0xDD;
    private const int App1 = 0xE1;
    private const int App2 = 0xE2;
    private const int App14 = 0xEE;

    public static DecodedImage Decode(byte[] data)
    {
        var quantization = new int[4][];
        var dcTables = new JpegHuffmanTable?[4];
        var acTables = new JpegHuffmanTable?[4];
        JpegFrame? frame = null;
        var restartInterval = 0;
        var adobeTransform = -1;
        var orientation = 1;
        var iccChunks = new SortedDictionary<int, byte[]>();
        var iccChunkCount = 0;
        var scans = 0;
        var position = 2;

        while (true)
        {
            position = FindMarker(data, position);

            if (position + 1 >= data.Length)
                break;

            var marker = data[position + 1];
            position += 2;

            if (marker == Soi || marker is >= Rst0 and <= Rst7 || marker == 0x01)
                continue;

            if (marker == Eoi)
                break;

            if (position + 2 > data.Length)
                break;

            var length = (data[position] << 8) | data[position + 1];
            var end = position + length;

            if (length < 2 || end > data.Length)
            {
                // A segment cut off by the end of the file: whatever was decoded before it stands.
                break;
            }

            var body = data.AsSpan(position + 2, length - 2);

            switch (marker)
            {
                case Sof0:
                case Sof1:
                case Sof2:
                    frame ??= ReadFrame(body, marker == Sof2);
                    break;

                case Dht:
                    ReadHuffmanTables(body, dcTables, acTables);
                    break;

                case Dqt:
                    ReadQuantizationTables(body, quantization);
                    break;

                case Dri:
                    restartInterval = body.Length >= 2 ? (body[0] << 8) | body[1] : 0;
                    break;

                case App1:
                    if (body.Length > 6 && body[..6].SequenceEqual("Exif\0\0"u8))
                        orientation = ExifOrientationReader.Read(body);

                    break;

                case App2:
                    // A profile too large for one segment is split over several, numbered from 1.
                    if (body.Length > 14 && body[..12].SequenceEqual("ICC_PROFILE\0"u8) && body[12] > 0)
                    {
                        iccChunks.TryAdd(body[12], body[14..].ToArray());
                        iccChunkCount = body[13];
                    }

                    break;

                case App14:
                    if (body.Length >= 12 && body[..5].SequenceEqual("Adobe"u8))
                        adobeTransform = body[11];

                    break;

                case Sos:
                {
                    if (frame is null)
                        throw new InvalidImageContentException("The JPEG has a scan before its frame.");

                    var scan = ReadScan(body, frame, dcTables, acTables, out var spectralStart, out var spectralEnd, out var successiveHigh, out var successiveLow);
                    var decoder = new JpegScanDecoder(data, end, frame, scan, restartInterval, spectralStart, spectralEnd, successiveHigh, successiveLow);

                    position = decoder.Decode();
                    scans++;
                    continue;
                }

                case 0xC3:
                case >= 0xC5 and <= 0xC7:
                case >= 0xC9 and <= 0xCB:
                case >= 0xCD and <= 0xCF:
                    throw new InvalidImageContentException("Lossless, hierarchical and arithmetic-coded JPEG are not supported.");
            }

            position = end;
        }

        if (frame is null || scans == 0)
            throw new InvalidImageContentException("The JPEG has no image data.");

        var planes = new byte[frame.Components.Count][];
        var workspace = new float[64];

        for (var index = 0; index < planes.Length; index++)
        {
            var component = frame.Components[index];
            var table = quantization[component.QuantizationTable]
                ?? throw new InvalidImageContentException("The JPEG uses a quantisation table it does not define.");
            var stride = component.BlocksPerLineForMcu * 8;
            var plane = new byte[stride * component.BlocksPerColumn * 8];

            for (var blockRow = 0; blockRow < component.BlocksPerColumn; blockRow++)
            {
                for (var blockColumn = 0; blockColumn < component.BlocksPerLine; blockColumn++)
                {
                    JpegIdct.Transform(
                        component.Coefficients, component.BlockOffset(blockRow, blockColumn), table,
                        plane, (blockRow * 8 * stride) + (blockColumn * 8), stride, workspace);
                }
            }

            planes[index] = plane;
        }

        var image = JpegColorConverter.Convert(frame, planes, ColorSpaceOf(frame, adobeTransform));

        return new DecodedImage(image, ImageFormat.Jpeg, orientation, AssembleIccProfile(iccChunks, iccChunkCount));
    }

    /// <summary>
    /// The position of the next marker's 0xFF at or after <paramref name="position"/>: a 0xFF followed
    /// by neither 0x00 (a stuffed byte) nor 0xFF (a fill byte). The end of the data when there is none.
    /// </summary>
    internal static int FindMarker(byte[] data, int position)
    {
        for (var index = Math.Max(0, position); index + 1 < data.Length; index++)
        {
            if (data[index] == 0xFF && data[index + 1] != 0x00 && data[index + 1] != 0xFF)
                return index;
        }

        return data.Length;
    }

    // The profile when every numbered part is present, and it describes RGB; null otherwise.
    private static byte[]? AssembleIccProfile(SortedDictionary<int, byte[]> chunks, int count)
    {
        if (count == 0 || chunks.Count != count || chunks.Keys.First() != 1 || chunks.Keys.Last() != count)
            return null;

        return IccProfiles.RgbOrNull(chunks.Values.SelectMany(chunk => chunk).ToArray());
    }

    private static JpegFrame ReadFrame(ReadOnlySpan<byte> body, bool progressive)
    {
        if (body.Length < 6)
            throw new InvalidImageContentException("The JPEG frame header is too short.");

        var precision = body[0];
        var height = (body[1] << 8) | body[2];
        var width = (body[3] << 8) | body[4];
        var count = body[5];

        if (precision != 8)
            throw new InvalidImageContentException("Only 8-bit JPEG is supported.");

        if (height == 0)
            throw new InvalidImageContentException("A JPEG whose height follows the data (DNL) is not supported.");

        ImageLimits.EnsureSize(width, height);

        if (count is not (1 or 3 or 4) || body.Length < 6 + (count * 3))
            throw new InvalidImageContentException("The JPEG frame has an unsupported number of components.");

        var components = new List<JpegComponent>(count);

        for (var index = 0; index < count; index++)
        {
            var offset = 6 + (index * 3);
            var horizontal = body[offset + 1] >> 4;
            var vertical = body[offset + 1] & 15;
            var table = body[offset + 2];

            if (horizontal is < 1 or > 4 || vertical is < 1 or > 4 || table > 3)
                throw new InvalidImageContentException("A JPEG component is malformed.");

            components.Add(new JpegComponent(body[offset], horizontal, vertical, table));
        }

        return new JpegFrame(width, height, progressive, components);
    }

    private static void ReadHuffmanTables(ReadOnlySpan<byte> body, JpegHuffmanTable?[] dcTables, JpegHuffmanTable?[] acTables)
    {
        var offset = 0;

        while (offset + 17 <= body.Length)
        {
            var tableClass = body[offset] >> 4;
            var index = body[offset] & 15;
            var counts = body.Slice(offset + 1, 16);
            var total = 0;

            foreach (var count in counts)
                total += count;

            if (tableClass > 1 || index > 3 || offset + 17 + total > body.Length)
                throw new InvalidImageContentException("A JPEG Huffman table is malformed.");

            var table = new JpegHuffmanTable(counts, body.Slice(offset + 17, total));

            if (tableClass == 0)
                dcTables[index] = table;
            else
                acTables[index] = table;

            offset += 17 + total;
        }
    }

    private static void ReadQuantizationTables(ReadOnlySpan<byte> body, int[][] quantization)
    {
        var offset = 0;

        while (offset < body.Length)
        {
            var wide = body[offset] >> 4;
            var index = body[offset] & 15;
            var size = wide == 0 ? 64 : 128;

            if (wide > 1 || index > 3 || offset + 1 + size > body.Length)
                throw new InvalidImageContentException("A JPEG quantisation table is malformed.");

            var table = new int[64];

            for (var position = 0; position < 64; position++)
            {
                var value = wide == 0
                    ? body[offset + 1 + position]
                    : (body[offset + 1 + (position * 2)] << 8) | body[offset + 2 + (position * 2)];

                table[JpegTables.ZigZag[position]] = value;
            }

            quantization[index] = table;
            offset += 1 + size;
        }
    }

    private static List<JpegComponent> ReadScan(
        ReadOnlySpan<byte> body, JpegFrame frame, JpegHuffmanTable?[] dcTables, JpegHuffmanTable?[] acTables,
        out int spectralStart, out int spectralEnd, out int successiveHigh, out int successiveLow)
    {
        var count = body.Length > 0 ? body[0] : 0;

        if (count is < 1 or > 4 || body.Length < 4 + (count * 2))
            throw new InvalidImageContentException("A JPEG scan header is malformed.");

        var parameters = 1 + (count * 2);
        spectralStart = body[parameters];
        spectralEnd = body[parameters + 1];
        successiveHigh = body[parameters + 2] >> 4;
        successiveLow = body[parameters + 2] & 15;

        if (frame.Progressive)
        {
            if (spectralStart > 63 || spectralEnd > 63 || spectralStart > spectralEnd || (spectralStart == 0 && spectralEnd != 0)
                || successiveLow > 13 || successiveHigh > 13)
            {
                throw new InvalidImageContentException("A progressive JPEG scan is malformed.");
            }
        }
        else
        {
            spectralStart = 0;
            spectralEnd = 63;
            successiveHigh = 0;
            successiveLow = 0;
        }

        var components = new List<JpegComponent>(count);

        for (var index = 0; index < count; index++)
        {
            var id = body[1 + (index * 2)];
            var tables = body[2 + (index * 2)];
            var component = frame.Components.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new InvalidImageContentException("A JPEG scan names a component the frame does not have.");

            component.DcTable = dcTables[(tables >> 4) & 3];
            component.AcTable = acTables[tables & 3];

            var needsDc = spectralStart == 0 && successiveHigh == 0;
            var needsAc = spectralEnd > 0;

            if ((needsDc && component.DcTable is null) || (needsAc && component.AcTable is null))
                throw new InvalidImageContentException("A JPEG scan uses a Huffman table that is not defined.");

            components.Add(component);
        }

        return components;
    }

    private static JpegColorSpace ColorSpaceOf(JpegFrame frame, int adobeTransform)
    {
        var components = frame.Components;

        switch (components.Count)
        {
            case 1:
                return JpegColorSpace.Grayscale;

            case 3:
                if (adobeTransform == 0)
                    return JpegColorSpace.Rgb;

                if (adobeTransform < 0 && components[0].Id == 'R' && components[1].Id == 'G' && components[2].Id == 'B')
                    return JpegColorSpace.Rgb;

                return JpegColorSpace.YCbCr;

            default:
                if (adobeTransform < 0)
                    return JpegColorSpace.Cmyk;

                return adobeTransform == 2 ? JpegColorSpace.Ycck : JpegColorSpace.InvertedCmyk;
        }
    }
}
