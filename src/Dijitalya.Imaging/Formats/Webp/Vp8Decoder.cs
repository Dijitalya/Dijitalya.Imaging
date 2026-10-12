namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Decodes a VP8 key frame, the lossy WebP bitstream (RFC 6386), following libwebp step by step so
/// that the pixels match it: frame header, segments, loop filter settings, token partitions,
/// prediction modes and coefficients, reconstruction in a 32-byte-stride work buffer, the loop filter
/// over the whole frame and finally the conversion to RGBA. The result is opaque; the alpha of a
/// WebP comes from its ALPH chunk.
/// </summary>
internal sealed class Vp8Decoder
{
    private const int Stride = Vp8Predictor.Stride;
    private const int LumaOffset = Stride + 8;
    private const int UOffset = LumaOffset + (16 * Stride) + Stride;
    private const int VOffset = UOffset + 16;
    private const int WorkBufferSize = (Stride * 17) + (Stride * 9);
    private const int Segments = 4;
    private const int ProbabilityCount = 11;

    // The offset of each 4x4 luma block inside the macroblock, in raster order.
    private static readonly int[] BlockScan =
    [
        0, 4, 8, 12,
        4 * Stride, (4 * Stride) + 4, (4 * Stride) + 8, (4 * Stride) + 12,
        8 * Stride, (8 * Stride) + 4, (8 * Stride) + 8, (8 * Stride) + 12,
        12 * Stride, (12 * Stride) + 4, (12 * Stride) + 8, (12 * Stride) + 12,
    ];

    private readonly byte[] _coefficientProbabilities = DecodeContext.Allocate<byte>(checked(Vp8Tables.CoefficientProbabilities.Length));
    private readonly int[] _segmentProbabilities = [255, 255, 255];
    private readonly Vp8QuantMatrix[] _quantizers = new Vp8QuantMatrix[Segments];
    private readonly Vp8FilterStrength[,] _filterStrengths = new Vp8FilterStrength[Segments, 2];
    private readonly short[] _secondOrder = DecodeContext.Allocate<short>(checked(16));
    private readonly Vp8MacroblockData _block = new();
    private readonly byte[] _work = DecodeContext.Allocate<byte>(checked(WorkBufferSize));

    private Vp8BoolDecoder _header = null!;
    private Vp8BoolDecoder[] _partitions = [];
    private bool _useSegments;
    private bool _updateSegmentMap;
    private bool _absoluteSegmentValues = true;
    private readonly int[] _segmentQuantizer = DecodeContext.Allocate<int>(checked(Segments));
    private readonly int[] _segmentFilterLevel = DecodeContext.Allocate<int>(checked(Segments));
    private bool _useSkipProbability;
    private int _skipProbability;
    private int _filterType;

    private int _width;
    private int _height;
    private int _macroblocksWide;
    private int _macroblocksHigh;

    // Prediction contexts: the 4x4 modes above each macroblock column and left of the current one,
    // and the "has coefficients" flags the token contexts use.
    private byte[] _modesTop = [];
    private readonly byte[] _modesLeft = DecodeContext.Allocate<byte>(checked(4));
    private uint[] _nonZeroTop = [];
    private uint[] _nonZeroDcTop = [];
    private uint _nonZeroLeft;
    private uint _nonZeroDcLeft;

    // The unfiltered bottom rows of the previous macroblock row, which the next row predicts from.
    private byte[] _topY = [];
    private byte[] _topU = [];
    private byte[] _topV = [];

    private byte[] _planeY = [];
    private byte[] _planeU = [];
    private byte[] _planeV = [];
    private int _strideY;
    private int _strideUv;

    // Per macroblock: segment, whether it uses 4x4 prediction and whether its inner edges are filtered.
    private byte[] _filterInfo = [];

    private Vp8Decoder()
    {
    }

    /// <summary>Reads the size from the header of a VP8 chunk; throws when the header is not a usable key frame.</summary>
    public static (int Width, int Height) ReadSize(byte[] data, int start, int length)
    {
        if (length < 10)
            throw new InvalidImageContentException("The WebP lossy header is cut off.");

        var bits = data[start] | (data[start + 1] << 8) | (data[start + 2] << 16);

        if ((bits & 1) != 0)
            throw new InvalidImageContentException("The WebP lossy data is not a key frame.");

        if (((bits >> 1) & 7) > 3)
            throw new InvalidImageContentException("The WebP lossy profile is not supported.");

        if (((bits >> 4) & 1) == 0)
            throw new InvalidImageContentException("The WebP lossy frame is not displayable.");

        if (data[start + 3] != 0x9D || data[start + 4] != 0x01 || data[start + 5] != 0x2A)
            throw new InvalidImageContentException("The WebP lossy start code is missing.");

        var width = (data[start + 6] | (data[start + 7] << 8)) & 0x3FFF;
        var height = (data[start + 8] | (data[start + 9] << 8)) & 0x3FFF;

        if (width == 0 || height == 0)
            throw new InvalidImageContentException("The WebP lossy picture has no pixels.");

        return (width, height);
    }

    /// <summary>Decodes a VP8 chunk into an opaque picture.</summary>
    public static RasterImage Decode(byte[] data, int start, int length)
    {
        var decoder = new Vp8Decoder();
        decoder.ParseHeaders(data, start, length);
        decoder.DecodeFrame();

        if (decoder._filterType > 0)
            decoder.FilterFrame();

        var image = new RasterImage(decoder._width, decoder._height);
        Vp8YuvConverter.ToRgba(decoder._planeY, decoder._strideY, decoder._planeU, decoder._planeV, decoder._strideUv, decoder._width, decoder._height, image.Pixels);

        return image;
    }

    private void ParseHeaders(byte[] data, int start, int length)
    {
        (_width, _height) = ReadSize(data, start, length);
        ImageLimits.EnsureSize(_width, _height);

        var tag = data[start] | (data[start + 1] << 8) | (data[start + 2] << 16);
        var partitionLength = tag >> 5;
        var position = start + 10;
        var remaining = length - 10;

        if (partitionLength > remaining)
            throw new InvalidImageContentException("The WebP lossy header partition is cut off.");

        _header = new Vp8BoolDecoder(data, position, partitionLength);
        position += partitionLength;
        remaining -= partitionLength;

        // Colour space and clamping type: both have a single meaning for WebP.
        _header.GetBit(0x80);
        _header.GetBit(0x80);

        ParseSegmentHeader();
        ParseFilterHeader();
        ParsePartitions(data, position, remaining);
        ParseQuantizers();

        // Whether to keep the probabilities for later frames: meaningless for a single frame.
        _header.GetBit(0x80);
        ParseProbabilities();

        if (_header.IsEndOfStream)
            throw new InvalidImageContentException("The WebP lossy header is cut off.");
    }

    private void ParseSegmentHeader()
    {
        _useSegments = _header.GetBit(0x80) == 1;

        if (!_useSegments)
            return;

        _updateSegmentMap = _header.GetBit(0x80) == 1;

        if (_header.GetBit(0x80) == 1)
        {
            _absoluteSegmentValues = _header.GetBit(0x80) == 1;

            for (var s = 0; s < Segments; s++)
                _segmentQuantizer[s] = _header.GetBit(0x80) == 1 ? _header.GetSignedValue(7) : 0;

            for (var s = 0; s < Segments; s++)
                _segmentFilterLevel[s] = _header.GetBit(0x80) == 1 ? _header.GetSignedValue(6) : 0;
        }

        if (_updateSegmentMap)
        {
            for (var s = 0; s < _segmentProbabilities.Length; s++)
                _segmentProbabilities[s] = _header.GetBit(0x80) == 1 ? _header.GetValue(8) : 255;
        }
    }

    private void ParseFilterHeader()
    {
        var simple = _header.GetBit(0x80) == 1;
        var level = _header.GetValue(6);
        var sharpness = _header.GetValue(3);
        var useDeltas = _header.GetBit(0x80) == 1;
        var referenceDelta = 0;
        var intra4x4Delta = 0;

        if (useDeltas && _header.GetBit(0x80) == 1)
        {
            // Four reference-frame deltas and four mode deltas; a key frame uses the first of each.
            for (var i = 0; i < 4; i++)
            {
                if (_header.GetBit(0x80) == 1)
                {
                    var delta = _header.GetSignedValue(6);

                    if (i == 0)
                        referenceDelta = delta;
                }
            }

            for (var i = 0; i < 4; i++)
            {
                if (_header.GetBit(0x80) == 1)
                {
                    var delta = _header.GetSignedValue(6);

                    if (i == 0)
                        intra4x4Delta = delta;
                }
            }
        }

        _filterType = level == 0 ? 0 : simple ? 1 : 2;

        for (var s = 0; s < Segments; s++)
        {
            var baseLevel = level;

            if (_useSegments)
            {
                baseLevel = _segmentFilterLevel[s];

                if (!_absoluteSegmentValues)
                    baseLevel += level;
            }

            for (var intra4x4 = 0; intra4x4 <= 1; intra4x4++)
            {
                var strength = baseLevel;

                if (useDeltas)
                {
                    strength += referenceDelta;

                    if (intra4x4 == 1)
                        strength += intra4x4Delta;
                }

                strength = Math.Clamp(strength, 0, 63);

                if (strength == 0)
                {
                    _filterStrengths[s, intra4x4] = new Vp8FilterStrength();
                    continue;
                }

                var innerLevel = strength;

                if (sharpness > 0)
                {
                    innerLevel >>= sharpness > 4 ? 2 : 1;

                    if (innerLevel > 9 - sharpness)
                        innerLevel = 9 - sharpness;
                }

                if (innerLevel < 1)
                    innerLevel = 1;

                _filterStrengths[s, intra4x4] = new Vp8FilterStrength
                {
                    Limit = (2 * strength) + innerLevel,
                    InnerLevel = innerLevel,
                    HighEdgeVarianceThreshold = strength >= 40 ? 2 : strength >= 15 ? 1 : 0,
                };
            }
        }
    }

    private void ParsePartitions(byte[] data, int position, int remaining)
    {
        var count = 1 << _header.GetValue(2);
        var last = count - 1;

        if (remaining < 3 * last)
            throw new InvalidImageContentException("The WebP lossy partition table is cut off.");

        var sizes = position;
        var partitionStart = position + (3 * last);
        var left = remaining - (3 * last);
        _partitions = new Vp8BoolDecoder[count];

        for (var p = 0; p < last; p++)
        {
            var size = data[sizes] | (data[sizes + 1] << 8) | (data[sizes + 2] << 16);
            size = Math.Min(size, left);
            _partitions[p] = new Vp8BoolDecoder(data, partitionStart, size);
            partitionStart += size;
            left -= size;
            sizes += 3;
        }

        if (left <= 0)
            throw new InvalidImageContentException("The WebP lossy data is cut off.");

        _partitions[last] = new Vp8BoolDecoder(data, partitionStart, left);
    }

    private void ParseQuantizers()
    {
        var baseIndex = _header.GetValue(7);
        var y1Dc = ReadDelta();
        var y2Dc = ReadDelta();
        var y2Ac = ReadDelta();
        var uvDc = ReadDelta();
        var uvAc = ReadDelta();

        for (var s = 0; s < Segments; s++)
        {
            int index;

            if (_useSegments)
            {
                index = _segmentQuantizer[s];

                if (!_absoluteSegmentValues)
                    index += baseIndex;
            }
            else if (s > 0)
            {
                _quantizers[s] = _quantizers[0];
                continue;
            }
            else
            {
                index = baseIndex;
            }

            _quantizers[s] = new Vp8QuantMatrix
            {
                Y1Dc = Vp8Tables.DcQuantization[Math.Clamp(index + y1Dc, 0, 127)],
                Y1Ac = Vp8Tables.AcQuantization[Math.Clamp(index, 0, 127)],
                Y2Dc = Vp8Tables.DcQuantization[Math.Clamp(index + y2Dc, 0, 127)] * 2,

                // x * 155 / 100, as libwebp computes it, but never below 8.
                Y2Ac = Math.Max((Vp8Tables.AcQuantization[Math.Clamp(index + y2Ac, 0, 127)] * 101581) >> 16, 8),
                UvDc = Vp8Tables.DcQuantization[Math.Clamp(index + uvDc, 0, 117)],
                UvAc = Vp8Tables.AcQuantization[Math.Clamp(index + uvAc, 0, 127)],
            };
        }

        int ReadDelta() => _header.GetBit(0x80) == 1 ? _header.GetSignedValue(4) : 0;
    }

    private void ParseProbabilities()
    {
        var defaults = Vp8Tables.CoefficientProbabilities;
        var updates = Vp8Tables.CoefficientUpdateProbabilities;

        for (var index = 0; index < defaults.Length; index++)
            _coefficientProbabilities[index] = _header.GetBit(updates[index]) == 1 ? (byte)_header.GetValue(8) : defaults[index];

        _useSkipProbability = _header.GetBit(0x80) == 1;

        if (_useSkipProbability)
            _skipProbability = _header.GetValue(8);
    }

    private void DecodeFrame()
    {
        _macroblocksWide = (_width + 15) >> 4;
        _macroblocksHigh = (_height + 15) >> 4;
        _strideY = _macroblocksWide * 16;
        _strideUv = _macroblocksWide * 8;
        _planeY = DecodeContext.Allocate<byte>(checked(_strideY * _macroblocksHigh * 16));
        _planeU = DecodeContext.Allocate<byte>(checked(_strideUv * _macroblocksHigh * 8));
        _planeV = DecodeContext.Allocate<byte>(checked(_strideUv * _macroblocksHigh * 8));
        _modesTop = DecodeContext.Allocate<byte>(checked(_macroblocksWide * 4));
        _nonZeroTop = DecodeContext.Allocate<uint>(checked(_macroblocksWide));
        _nonZeroDcTop = DecodeContext.Allocate<uint>(checked(_macroblocksWide));
        _topY = DecodeContext.Allocate<byte>(checked(_macroblocksWide * 16));
        _topU = DecodeContext.Allocate<byte>(checked(_macroblocksWide * 8));
        _topV = DecodeContext.Allocate<byte>(checked(_macroblocksWide * 8));
        _filterInfo = DecodeContext.Allocate<byte>(checked(_macroblocksWide * _macroblocksHigh));

        for (var mbY = 0; mbY < _macroblocksHigh; mbY++)
        {
            DecodeContext.Checkpoint();
            var tokens = _partitions[mbY & (_partitions.Length - 1)];

            Array.Clear(_modesLeft);
            _nonZeroLeft = 0;
            _nonZeroDcLeft = 0;
            PrepareRow(mbY);

            for (var mbX = 0; mbX < _macroblocksWide; mbX++)
            {
                ParseModes(mbX);
                var hasNoCoefficients = ParseCoefficients(mbX, tokens);

                if (tokens.IsEndOfStream)
                    throw new InvalidImageContentException("The WebP lossy data is cut off.");

                var inner = _block.IsIntra4x4 || !hasNoCoefficients;
                _filterInfo[(mbY * _macroblocksWide) + mbX] = (byte)((_block.Segment << 2) | (_block.IsIntra4x4 ? 2 : 0) | (inner ? 1 : 0));

                Reconstruct(mbX, mbY);
            }

            if (_header.IsEndOfStream)
                throw new InvalidImageContentException("The WebP lossy header partition is cut off.");
        }
    }

    private void ParseModes(int mbX)
    {
        var block = _block;
        var header = _header;

        block.Segment = _updateSegmentMap
            ? header.GetBit(_segmentProbabilities[0]) == 0
                ? header.GetBit(_segmentProbabilities[1])
                : header.GetBit(_segmentProbabilities[2]) + 2
            : 0;

        block.Skip = _useSkipProbability && header.GetBit(_skipProbability) == 1;
        block.IsIntra4x4 = header.GetBit(145) == 0;

        var top = mbX * 4;

        if (!block.IsIntra4x4)
        {
            var mode = header.GetBit(156) == 1
                ? header.GetBit(128) == 1 ? Vp8Predictor.TrueMotionMode : Vp8Predictor.HorizontalMode
                : header.GetBit(163) == 1 ? Vp8Predictor.VerticalMode : Vp8Predictor.DcMode;

            block.Modes[0] = (byte)mode;
            Array.Fill(_modesTop, (byte)mode, top, 4);
            Array.Fill(_modesLeft, (byte)mode);
        }
        else
        {
            var probabilities = Vp8Tables.SubblockModeProbabilities;

            for (var y = 0; y < 4; y++)
            {
                int mode = _modesLeft[y];

                for (var x = 0; x < 4; x++)
                {
                    var p = ((_modesTop[top + x] * 10) + mode) * 9;

                    mode = header.GetBit(probabilities[p]) == 0 ? 0
                        : header.GetBit(probabilities[p + 1]) == 0 ? 1
                        : header.GetBit(probabilities[p + 2]) == 0 ? 2
                        : header.GetBit(probabilities[p + 3]) == 0
                            ? header.GetBit(probabilities[p + 4]) == 0 ? 3
                                : header.GetBit(probabilities[p + 5]) == 0 ? 4 : 5
                            : header.GetBit(probabilities[p + 6]) == 0 ? 6
                                : header.GetBit(probabilities[p + 7]) == 0 ? 7
                                : header.GetBit(probabilities[p + 8]) == 0 ? 8 : 9;

                    _modesTop[top + x] = (byte)mode;
                    block.Modes[(y * 4) + x] = (byte)mode;
                }

                _modesLeft[y] = (byte)mode;
            }
        }

        block.ChromaMode = header.GetBit(142) == 0 ? Vp8Predictor.DcMode
            : header.GetBit(114) == 0 ? Vp8Predictor.VerticalMode
            : header.GetBit(183) == 1 ? Vp8Predictor.TrueMotionMode
            : Vp8Predictor.HorizontalMode;
    }

    // Returns true when the macroblock has no non-zero coefficient at all.
    private bool ParseCoefficients(int mbX, Vp8BoolDecoder tokens)
    {
        var block = _block;

        if (block.Skip)
        {
            _nonZeroLeft = 0;
            _nonZeroTop[mbX] = 0;

            if (!block.IsIntra4x4)
            {
                _nonZeroDcLeft = 0;
                _nonZeroDcTop[mbX] = 0;
            }

            block.NonZeroY = 0;
            block.NonZeroUv = 0;

            return true;
        }

        var coefficients = block.Coefficients;
        var quantizer = _quantizers[block.Segment];
        Array.Clear(coefficients);

        int first;
        int acType;

        if (!block.IsIntra4x4)
        {
            Array.Clear(_secondOrder);
            var context = (int)(_nonZeroDcTop[mbX] + _nonZeroDcLeft);
            var count = ReadCoefficients(tokens, 1, context, quantizer.Y2Dc, quantizer.Y2Ac, 0, _secondOrder, 0);
            _nonZeroDcTop[mbX] = _nonZeroDcLeft = count > 0 ? 1u : 0u;

            if (count > 1)
            {
                Vp8Transform.InverseWalshHadamard(_secondOrder, coefficients);
            }
            else
            {
                var dc = (short)((_secondOrder[0] + 3) >> 3);

                for (var i = 0; i < 256; i += 16)
                    coefficients[i] = dc;
            }

            first = 1;
            acType = 0;
        }
        else
        {
            first = 0;
            acType = 3;
        }

        var topFlags = _nonZeroTop[mbX] & 0x0F;
        var leftFlags = _nonZeroLeft & 0x0F;
        uint nonZeroY = 0;
        var blockIndex = 0;

        for (var y = 0; y < 4; y++)
        {
            var left = leftFlags & 1;
            uint codes = 0;

            for (var x = 0; x < 4; x++)
            {
                var context = (int)(left + (topFlags & 1));
                var offset = blockIndex * 16;
                var count = ReadCoefficients(tokens, acType, context, quantizer.Y1Dc, quantizer.Y1Ac, first, coefficients, offset);
                left = count > first ? 1u : 0u;
                topFlags = (topFlags >> 1) | (left << 7);
                codes = CoefficientCode(codes, count, coefficients[offset] != 0);
                blockIndex++;
            }

            topFlags >>= 4;
            leftFlags = (leftFlags >> 1) | (left << 7);
            nonZeroY = (nonZeroY << 8) | codes;
        }

        var outTop = topFlags;
        var outLeft = leftFlags >> 4;
        uint nonZeroUv = 0;

        for (var channel = 0; channel < 4; channel += 2)
        {
            uint codes = 0;
            topFlags = _nonZeroTop[mbX] >> (4 + channel);
            leftFlags = _nonZeroLeft >> (4 + channel);

            for (var y = 0; y < 2; y++)
            {
                var left = leftFlags & 1;

                for (var x = 0; x < 2; x++)
                {
                    var context = (int)(left + (topFlags & 1));
                    var offset = blockIndex * 16;
                    var count = ReadCoefficients(tokens, 2, context, quantizer.UvDc, quantizer.UvAc, 0, coefficients, offset);
                    left = count > 0 ? 1u : 0u;
                    topFlags = (topFlags >> 1) | (left << 3);
                    codes = CoefficientCode(codes, count, coefficients[offset] != 0);
                    blockIndex++;
                }

                topFlags >>= 2;
                leftFlags = (leftFlags >> 1) | (left << 5);
            }

            nonZeroUv |= codes << (4 * channel);
            outTop |= (topFlags << 4) << channel;
            outLeft |= (leftFlags & 0xF0) << channel;
        }

        _nonZeroTop[mbX] = outTop;
        _nonZeroLeft = outLeft;
        block.NonZeroY = nonZeroY;
        block.NonZeroUv = nonZeroUv;

        return (nonZeroY | nonZeroUv) == 0;
    }

    private static uint CoefficientCode(uint codes, int count, bool hasDc) =>
        (codes << 2) | (count > 3 ? 3u : count > 1 ? 2u : hasDc ? 1u : 0u);

    // Reads the tokens of one block from position 'first' on; returns the index after the last
    // non-zero coefficient (0 when there is none).
    private int ReadCoefficients(Vp8BoolDecoder tokens, int type, int context, int dcFactor, int acFactor, int first, short[] output, int offset)
    {
        var probabilities = _coefficientProbabilities;
        var p = ProbabilityOffset(type, Vp8Tables.Bands[first], context);

        for (var n = first; n < 16; n++)
        {
            if (tokens.GetBit(probabilities[p]) == 0)
                return n;

            while (tokens.GetBit(probabilities[p + 1]) == 0)
            {
                if (++n == 16)
                    return 16;

                p = ProbabilityOffset(type, Vp8Tables.Bands[n], 0);
            }

            int value;

            if (tokens.GetBit(probabilities[p + 2]) == 0)
            {
                value = 1;
                p = ProbabilityOffset(type, Vp8Tables.Bands[n + 1], 1);
            }
            else
            {
                value = ReadLargeValue(tokens, probabilities, p);
                p = ProbabilityOffset(type, Vp8Tables.Bands[n + 1], 2);
            }

            output[offset + Vp8Tables.ZigZag[n]] = (short)(tokens.GetSigned(value) * (n > 0 ? acFactor : dcFactor));
        }

        return 16;
    }

    private static int ReadLargeValue(Vp8BoolDecoder tokens, byte[] probabilities, int p)
    {
        if (tokens.GetBit(probabilities[p + 3]) == 0)
        {
            return tokens.GetBit(probabilities[p + 4]) == 0 ? 2 : 3 + tokens.GetBit(probabilities[p + 5]);
        }

        if (tokens.GetBit(probabilities[p + 6]) == 0)
        {
            if (tokens.GetBit(probabilities[p + 7]) == 0)
                return 5 + tokens.GetBit(159);

            var value = 7 + (2 * tokens.GetBit(165));

            return value + tokens.GetBit(145);
        }

        var bit1 = tokens.GetBit(probabilities[p + 8]);
        var bit0 = tokens.GetBit(probabilities[p + 9 + bit1]);
        var category = (2 * bit1) + bit0;
        var extra = 0;

        foreach (var probability in Vp8Tables.CategoryProbabilities[category])
            extra += extra + tokens.GetBit(probability);

        return extra + 3 + (8 << category);
    }

    private static int ProbabilityOffset(int type, int band, int context) => (((type * 8) + band) * 3 + context) * ProbabilityCount;

    // The borders of the work buffer at the start of a macroblock row (RFC 6386 12.2).
    private void PrepareRow(int mbY)
    {
        var work = _work;

        for (var j = 0; j < 16; j++)
            work[LumaOffset + (j * Stride) - 1] = 129;

        for (var j = 0; j < 8; j++)
        {
            work[UOffset + (j * Stride) - 1] = 129;
            work[VOffset + (j * Stride) - 1] = 129;
        }

        if (mbY > 0)
        {
            work[LumaOffset - Stride - 1] = 129;
            work[UOffset - Stride - 1] = 129;
            work[VOffset - Stride - 1] = 129;
        }
        else
        {
            Array.Fill(work, (byte)127, LumaOffset - Stride - 1, 16 + 4 + 1);
            Array.Fill(work, (byte)127, UOffset - Stride - 1, 8 + 1);
            Array.Fill(work, (byte)127, VOffset - Stride - 1, 8 + 1);
        }
    }

    private void Reconstruct(int mbX, int mbY)
    {
        var work = _work;
        var block = _block;

        // The new left column is the right edge of the previous macroblock, with the corner above it.
        if (mbX > 0)
        {
            for (var j = -1; j < 16; j++)
                Array.Copy(work, LumaOffset + (j * Stride) + 12, work, LumaOffset + (j * Stride) - 4, 4);

            for (var j = -1; j < 8; j++)
            {
                Array.Copy(work, UOffset + (j * Stride) + 4, work, UOffset + (j * Stride) - 4, 4);
                Array.Copy(work, VOffset + (j * Stride) + 4, work, VOffset + (j * Stride) - 4, 4);
            }
        }

        if (mbY > 0)
        {
            Array.Copy(_topY, mbX * 16, work, LumaOffset - Stride, 16);
            Array.Copy(_topU, mbX * 8, work, UOffset - Stride, 8);
            Array.Copy(_topV, mbX * 8, work, VOffset - Stride, 8);
        }

        var codes = block.NonZeroY;

        if (block.IsIntra4x4)
        {
            var topRight = LumaOffset - Stride + 16;

            if (mbY > 0)
            {
                if (mbX >= _macroblocksWide - 1)
                    Array.Fill(work, _topY[(mbX * 16) + 15], topRight, 4);
                else
                    Array.Copy(_topY, (mbX + 1) * 16, work, topRight, 4);
            }

            // The blocks on the right edge take their top-right pixels from above the macroblock.
            for (var row = 4; row <= 12; row += 4)
                Array.Copy(work, topRight, work, topRight + (row * Stride), 4);

            for (var n = 0; n < 16; n++, codes <<= 2)
            {
                var target = LumaOffset + BlockScan[n];
                Vp8Predictor.PredictLuma4(block.Modes[n], work, target);
                Vp8Transform.AddLuma(codes, block.Coefficients, n * 16, work, target);
            }
        }
        else
        {
            Vp8Predictor.PredictLuma16(CheckMode(mbX, mbY, block.Modes[0]), work, LumaOffset);

            if (codes != 0)
            {
                for (var n = 0; n < 16; n++, codes <<= 2)
                    Vp8Transform.AddLuma(codes, block.Coefficients, n * 16, work, LumaOffset + BlockScan[n]);
            }
        }

        var chromaMode = CheckMode(mbX, mbY, block.ChromaMode);
        Vp8Predictor.PredictChroma8(chromaMode, work, UOffset);
        Vp8Predictor.PredictChroma8(chromaMode, work, VOffset);
        Vp8Transform.AddChroma(block.NonZeroUv, block.Coefficients, 256, work, UOffset);
        Vp8Transform.AddChroma(block.NonZeroUv >> 8, block.Coefficients, 320, work, VOffset);

        if (mbY < _macroblocksHigh - 1)
        {
            Array.Copy(work, LumaOffset + (15 * Stride), _topY, mbX * 16, 16);
            Array.Copy(work, UOffset + (7 * Stride), _topU, mbX * 8, 8);
            Array.Copy(work, VOffset + (7 * Stride), _topV, mbX * 8, 8);
        }

        var lumaTarget = (mbY * 16 * _strideY) + (mbX * 16);

        for (var j = 0; j < 16; j++)
            Array.Copy(work, LumaOffset + (j * Stride), _planeY, lumaTarget + (j * _strideY), 16);

        var chromaTarget = (mbY * 8 * _strideUv) + (mbX * 8);

        for (var j = 0; j < 8; j++)
        {
            Array.Copy(work, UOffset + (j * Stride), _planeU, chromaTarget + (j * _strideUv), 8);
            Array.Copy(work, VOffset + (j * Stride), _planeV, chromaTarget + (j * _strideUv), 8);
        }
    }

    // DC prediction needs the edges it averages; at the picture's edges it uses what exists.
    private static int CheckMode(int mbX, int mbY, int mode)
    {
        if (mode != Vp8Predictor.DcMode)
            return mode;

        if (mbX == 0)
            return mbY == 0 ? Vp8Predictor.DcNoTopLeftMode : Vp8Predictor.DcNoLeftMode;

        return mbY == 0 ? Vp8Predictor.DcNoTopMode : Vp8Predictor.DcMode;
    }

    // The loop filter runs over the reconstructed frame macroblock by macroblock, in raster order.
    private void FilterFrame()
    {
        var simple = _filterType == 1;

        for (var mbY = 0; mbY < _macroblocksHigh; mbY++)
        {
            DecodeContext.Checkpoint();
            for (var mbX = 0; mbX < _macroblocksWide; mbX++)
            {
                var info = _filterInfo[(mbY * _macroblocksWide) + mbX];
                var strength = _filterStrengths[info >> 2, (info >> 1) & 1];

                Vp8LoopFilter.FilterMacroblock(
                    simple, strength, (info & 1) != 0, mbX > 0, mbY > 0,
                    _planeY, (mbY * 16 * _strideY) + (mbX * 16), _strideY,
                    _planeU, _planeV, (mbY * 8 * _strideUv) + (mbX * 8), _strideUv);
            }
        }
    }
}
