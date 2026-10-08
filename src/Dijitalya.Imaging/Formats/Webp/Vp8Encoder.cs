namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Encodes a picture as a VP8 key frame, the lossy WebP bitstream (RFC 6386), along the lines of
/// libwebp at its faster settings: the quality maps to a quantiser as in libwebp, each macroblock
/// takes the 16x16 or 4x4 luma prediction and the chroma prediction that cost least in distortion
/// plus estimated mode bits, the token probabilities are adapted to the picture and the loop
/// filter strength follows the quantiser. Reconstruction uses the decoder's own predictors and
/// transforms, so the encoder predicts from exactly the pixels a decoder will have.
/// </summary>
internal sealed class Vp8Encoder
{
    /// <summary>The largest width or height VP8 can describe.</summary>
    public const int MaxDimension = 16383;

    private const int Stride = Vp8Predictor.Stride;
    private const int LumaOffset = Stride + 8;
    private const int UOffset = LumaOffset + (16 * Stride) + Stride;
    private const int VOffset = UOffset + 16;
    private const int WorkBufferSize = (Stride * 17) + (Stride * 9);
    private const int MaxFirstPartitionSize = (1 << 19) - 1;
    private const int FilterStrength = 60;
    private const int ChromaDcDelta = -2;
    private const int SkipProbabilityThreshold = 250;
    private const int DistortionScale = 256;
    private const int LambdaI16 = 106;
    private const int LambdaI4 = 11;
    private const int LambdaUv = 120;

    private static readonly int[] BlockScan =
    [
        0, 4, 8, 12,
        4 * Stride, (4 * Stride) + 4, (4 * Stride) + 8, (4 * Stride) + 12,
        8 * Stride, (8 * Stride) + 4, (8 * Stride) + 8, (8 * Stride) + 12,
        12 * Stride, (12 * Stride) + 4, (12 * Stride) + 8, (12 * Stride) + 12,
    ];

    // The four 4x4 blocks of an 8x8 chroma block.
    private static readonly int[] ChromaScan = [0, 4, 4 * Stride, (4 * Stride) + 4];

    private readonly int _width;
    private readonly int _height;
    private readonly int _macroblocksWide;
    private readonly int _macroblocksHigh;
    private readonly bool _allowIntra4;

    // The picture in YUV 4:2:0, padded to whole macroblocks by repeating the last column and row.
    private readonly byte[] _planeY;
    private readonly byte[] _planeU;
    private readonly byte[] _planeV;
    private readonly int _strideY;
    private readonly int _strideUv;

    private readonly int _quantizer;
    private readonly int _filterLevel;
    private readonly Vp8EncodingMatrix _lumaMatrix;
    private readonly Vp8EncodingMatrix _secondOrderMatrix;
    private readonly Vp8EncodingMatrix _chromaMatrix;
    private readonly long _intra4Penalty;
    private readonly long _modeBitLimit;

    // The source macroblock and the reconstruction, laid out alike so they can be compared directly.
    private readonly byte[] _source = new byte[WorkBufferSize];
    private readonly byte[] _work = new byte[WorkBufferSize];
    private readonly short[] _coefficients = new short[16 * 16];
    private readonly short[] _chromaCoefficients = new short[8 * 16];
    private readonly short[] _secondOrderLevels = new short[16];
    private readonly short[] _secondOrder = new short[16];
    private readonly short[] _lumaLevels = new short[16 * 16];
    private readonly short[] _chromaLevels = new short[8 * 16];
    private readonly int[] _lumaLast = new int[16];
    private readonly int[] _chromaLast = new int[8];
    private int _secondOrderLast;

    // Unfiltered bottom rows of the previous macroblock row.
    private readonly byte[] _topY;
    private readonly byte[] _topU;
    private readonly byte[] _topV;

    // Decisions per macroblock: 16 subblock modes (or the 16x16 mode in entry 0), the chroma mode and flags.
    private readonly byte[] _modes;
    private readonly byte[] _chromaModes;
    private readonly bool[] _isIntra16;
    private readonly bool[] _isEmpty;

    // Mode contexts while deciding, as the decoder keeps them.
    private readonly byte[] _modesTop;
    private readonly byte[] _modesLeft = new byte[4];

    // "Has non-zero coefficients" contexts: 4 luma, 2 U and 2 V flags per macroblock column, and the DC flag.
    private readonly bool[] _nonZeroTop;
    private readonly bool[] _nonZeroDcTop;
    private readonly bool[] _nonZeroLeft = new bool[8];
    private bool _nonZeroDcLeft;

    // The levels of all blocks, in coding order: per block its last index and the levels up to it.
    private readonly List<short> _levels = [];

    private readonly Vp8TokenCoder _tokens = new();
    private readonly Vp8TokenCoder _emptyTokens = new();
    private int _emptyCount;

    private Vp8Encoder(RasterImage image, int quality, bool allowIntra4)
    {
        _width = image.Width;
        _height = image.Height;
        _macroblocksWide = (_width + 15) >> 4;
        _macroblocksHigh = (_height + 15) >> 4;
        _allowIntra4 = allowIntra4;
        _strideY = _macroblocksWide * 16;
        _strideUv = _macroblocksWide * 8;
        _planeY = new byte[_strideY * _macroblocksHigh * 16];
        _planeU = new byte[_strideUv * _macroblocksHigh * 8];
        _planeV = new byte[_strideUv * _macroblocksHigh * 8];
        _topY = new byte[_macroblocksWide * 16];
        _topU = new byte[_macroblocksWide * 8];
        _topV = new byte[_macroblocksWide * 8];

        var count = _macroblocksWide * _macroblocksHigh;
        _modes = new byte[count * 16];
        _chromaModes = new byte[count];
        _isIntra16 = new bool[count];
        _isEmpty = new bool[count];
        _modesTop = new byte[_macroblocksWide * 4];
        _nonZeroTop = new bool[_macroblocksWide * 8];
        _nonZeroDcTop = new bool[_macroblocksWide];

        // libwebp's mapping of quality to compression: file size goes roughly with the cube of the quantiser.
        var q = quality / 100.0;
        var linear = q < 0.75 ? q * (2.0 / 3.0) : (2.0 * q) - 1.0;
        var compression = Math.Pow(linear, 1.0 / 3.0);
        _quantizer = Math.Clamp((int)(127.0 * (1.0 - compression)), 0, 127);

        var dc = Vp8Tables.DcQuantization;
        var ac = Vp8Tables.AcQuantization;
        _lumaMatrix = new Vp8EncodingMatrix(dc[_quantizer], ac[_quantizer], 96, 110, sharpen: true);
        _secondOrderMatrix = new Vp8EncodingMatrix(dc[_quantizer] * 2, Math.Max((ac[_quantizer] * 101581) >> 16, 8), 96, 108, sharpen: false);
        _chromaMatrix = new Vp8EncodingMatrix(dc[Math.Clamp(_quantizer + ChromaDcDelta, 0, 117)], ac[_quantizer], 110, 115, sharpen: false);

        var averageStep = (long)_lumaMatrix.AverageStep;
        _intra4Penalty = 1000 * averageStep * averageStep;

        // Keeps the first partition, which holds all modes, within the 19-bit size the format allows.
        _modeBitLimit = 256L * 510 * 8 * 1024 / count;

        // Filter strength from the AC quantiser step, as libwebp sets it for its default strength.
        var step = Math.Min(ac[_quantizer] >> 2, 63);
        var level = step * 5 * FilterStrength / 256;
        _filterLevel = level < 2 ? 0 : Math.Min(level, 63);

        ConvertToYuv(image);
    }

    /// <summary>Encodes <paramref name="image"/> (its colours; alpha is the container's business) as a VP8 frame.</summary>
    /// <param name="image">The picture.</param>
    /// <param name="quality">0 (smallest) to 100 (best).</param>
    public static byte[] Encode(RasterImage image, int quality)
    {
        if (image.Width > MaxDimension || image.Height > MaxDimension)
            throw new NotSupportedException($"WebP pictures can be at most {MaxDimension} pixels wide and high.");

        quality = Math.Clamp(quality, 0, 100);

        // A picture of very many detailed macroblocks can overflow the first partition with 4x4 modes.
        return new Vp8Encoder(image, quality, allowIntra4: true).EncodeFrame()
            ?? new Vp8Encoder(image, quality, allowIntra4: false).EncodeFrame()
            ?? throw new NotSupportedException("The picture is too large for a lossy WebP.");
    }

    private byte[]? EncodeFrame()
    {
        for (var mbY = 0; mbY < _macroblocksHigh; mbY++)
        {
            Array.Clear(_modesLeft);
            Array.Clear(_nonZeroLeft);
            _nonZeroDcLeft = false;
            PrepareRow(mbY);

            for (var mbX = 0; mbX < _macroblocksWide; mbX++)
                EncodeMacroblock(mbX, mbY);
        }

        var count = _macroblocksWide * _macroblocksHigh;
        var skipProbability = (int)((long)(count - _emptyCount) * 255 / count);
        var useSkip = skipProbability < SkipProbabilityThreshold;

        if (!useSkip)
            _tokens.Add(_emptyTokens);

        _tokens.FinalizeProbabilities();

        var header = WriteFirstPartition(useSkip, skipProbability);

        if (header.Length > MaxFirstPartitionSize)
            return null;

        var tokens = WriteTokens(useSkip);

        return AssembleFrame(header, tokens);
    }

    private void EncodeMacroblock(int mbX, int mbY)
    {
        var index = (mbY * _macroblocksWide) + mbX;

        LoadSource(mbX, mbY);
        PrepareMacroblock(mbX, mbY);

        var intra16 = ChooseLuma(mbX, mbY, index, out var lumaEmpty);
        var chromaEmpty = EncodeChroma(mbX, mbY, index);
        var empty = lumaEmpty && chromaEmpty;

        _isIntra16[index] = intra16;
        _isEmpty[index] = empty;

        RecordTokens(mbX, intra16, empty);
        SaveTopSamples(mbX, mbY);
    }

    // Decides between 16x16 and 4x4 luma prediction and leaves the chosen reconstruction in the work
    // buffer. Returns whether 16x16 was chosen; 'empty' tells whether all luma levels are zero.
    private bool ChooseLuma(int mbX, int mbY, int index, out bool empty)
    {
        var work = _work;
        var bestMode = 0;
        var bestScore = long.MaxValue;

        for (var mode = 0; mode < 4; mode++)
        {
            if (mode > 0 && Vp8EncoderTables.FixedCostsI16[mode] > _modeBitLimit)
                continue;

            Vp8Predictor.PredictLuma16(CheckMode(mbX, mbY, mode), work, LumaOffset);
            var score = (Distortion(LumaOffset, 16, 16) * DistortionScale) + (Vp8EncoderTables.FixedCostsI16[mode] * (long)LambdaI16);

            if (score < bestScore)
            {
                bestScore = score;
                bestMode = mode;
            }
        }

        var tryIntra4 = _allowIntra4;

        // A flat block on the picture's edge stays 16x16, which avoids starting a checkerboard pattern there.
        if ((mbX == 0 || mbY == 0) && IsFlatSource())
        {
            bestMode = mbX == 0 ? Vp8Predictor.DcMode : Vp8Predictor.VerticalMode;
            tryIntra4 = false;
        }

        if (tryIntra4 && TryIntra4(mbX, index, bestScore, out empty))
            return false;

        Array.Clear(_modes, index * 16, 16);
        _modes[index * 16] = (byte)bestMode;
        Array.Fill(_modesTop, (byte)bestMode, mbX * 4, 4);
        Array.Fill(_modesLeft, (byte)bestMode);

        empty = EncodeIntra16(mbX, mbY, bestMode);

        return true;
    }

    private bool TryIntra4(int mbX, int index, long intra16Score, out bool empty)
    {
        var work = _work;
        var score = _intra4Penalty;
        var modeBits = 0L;
        Span<byte> modes = stackalloc byte[16];
        empty = true;

        for (var n = 0; n < 16; n++)
        {
            var x = n & 3;
            var y = n >> 2;
            int top = y == 0 ? _modesTop[(mbX * 4) + x] : modes[n - 4];
            int left = x == 0 ? _modesLeft[y] : modes[n - 1];
            var costs = ((top * 10) + left) * 10;
            var target = LumaOffset + BlockScan[n];
            var bestMode = 0;
            var bestScore = long.MaxValue;

            for (var mode = 0; mode < 10; mode++)
            {
                Vp8Predictor.PredictLuma4(mode, work, target);
                var candidate = (Distortion(target, 4, 4) * DistortionScale) + (Vp8EncoderTables.FixedCostsI4[costs + mode] * (long)LambdaI4);

                if (candidate < bestScore)
                {
                    bestScore = candidate;
                    bestMode = mode;
                }
            }

            modeBits += Vp8EncoderTables.FixedCostsI4[costs + bestMode];
            score += bestScore;

            // 4x4 prediction will not beat 16x16 any more.
            if (score >= intra16Score || modeBits > _modeBitLimit)
                return false;

            modes[n] = (byte)bestMode;
            Vp8Predictor.PredictLuma4(bestMode, work, target);
            Vp8Transform.Forward(_source, target, work, target, _coefficients, n * 16);
            _lumaLast[n] = _lumaMatrix.Quantize(_coefficients.AsSpan(n * 16, 16), _lumaLevels.AsSpan(n * 16, 16));
            Vp8Transform.AddFull(_coefficients, n * 16, work, target);

            if (_lumaLast[n] >= 0)
                empty = false;
        }

        modes.CopyTo(_modes.AsSpan(index * 16, 16));

        for (var x = 0; x < 4; x++)
            _modesTop[(mbX * 4) + x] = modes[12 + x];

        for (var y = 0; y < 4; y++)
            _modesLeft[y] = modes[(y * 4) + 3];

        return true;
    }

    private bool EncodeIntra16(int mbX, int mbY, int mode)
    {
        var work = _work;
        Vp8Predictor.PredictLuma16(CheckMode(mbX, mbY, mode), work, LumaOffset);

        for (var n = 0; n < 16; n++)
            Vp8Transform.Forward(_source, LumaOffset + BlockScan[n], work, LumaOffset + BlockScan[n], _coefficients, n * 16);

        Vp8Transform.ForwardWalshHadamard(_coefficients, _secondOrder);
        _secondOrderLast = _secondOrderMatrix.Quantize(_secondOrder, _secondOrderLevels);
        var empty = _secondOrderLast < 0;

        for (var n = 0; n < 16; n++)
        {
            // The DCs travel in the second-order block.
            _coefficients[n * 16] = 0;
            _lumaLast[n] = _lumaMatrix.Quantize(_coefficients.AsSpan(n * 16, 16), _lumaLevels.AsSpan(n * 16, 16));

            if (_lumaLast[n] >= 0)
                empty = false;
        }

        Vp8Transform.InverseWalshHadamard(_secondOrder, _coefficients);

        for (var n = 0; n < 16; n++)
            Vp8Transform.AddFull(_coefficients, n * 16, work, LumaOffset + BlockScan[n]);

        return empty;
    }

    private bool EncodeChroma(int mbX, int mbY, int index)
    {
        var work = _work;
        var bestMode = 0;
        var bestScore = long.MaxValue;

        for (var mode = 0; mode < 4; mode++)
        {
            var checkedMode = CheckMode(mbX, mbY, mode);
            Vp8Predictor.PredictChroma8(checkedMode, work, UOffset);
            Vp8Predictor.PredictChroma8(checkedMode, work, VOffset);
            var distortion = Distortion(UOffset, 8, 8) + Distortion(VOffset, 8, 8);
            var score = (distortion * DistortionScale) + (Vp8EncoderTables.FixedCostsUv[mode] * (long)LambdaUv);

            if (score < bestScore)
            {
                bestScore = score;
                bestMode = mode;
            }
        }

        _chromaModes[index] = (byte)bestMode;
        var predictionMode = CheckMode(mbX, mbY, bestMode);
        Vp8Predictor.PredictChroma8(predictionMode, work, UOffset);
        Vp8Predictor.PredictChroma8(predictionMode, work, VOffset);

        var empty = true;

        for (var n = 0; n < 8; n++)
        {
            var target = (n < 4 ? UOffset : VOffset) + ChromaScan[n & 3];
            Vp8Transform.Forward(_source, target, work, target, _chromaCoefficients, n * 16);
            _chromaLast[n] = _chromaMatrix.Quantize(_chromaCoefficients.AsSpan(n * 16, 16), _chromaLevels.AsSpan(n * 16, 16));
            Vp8Transform.AddFull(_chromaCoefficients, n * 16, work, target);

            if (_chromaLast[n] >= 0)
                empty = false;
        }

        return empty;
    }

    // Stores the macroblock's levels and counts its tokens with the contexts the decoder will have.
    private void RecordTokens(int mbX, bool intra16, bool empty)
    {
        var coder = empty ? _emptyTokens : _tokens;

        if (empty)
            _emptyCount++;
        else
            StoreLevels(intra16);

        CodeMacroblock(coder, null, mbX, intra16, _secondOrderLevels, _secondOrderLast, _lumaLevels, _lumaLast, _chromaLevels, _chromaLast);
    }

    private void StoreLevels(bool intra16)
    {
        if (intra16)
            StoreBlock(_secondOrderLevels, 0, _secondOrderLast);

        for (var n = 0; n < 16; n++)
            StoreBlock(_lumaLevels, n * 16, _lumaLast[n]);

        for (var n = 0; n < 8; n++)
            StoreBlock(_chromaLevels, n * 16, _chromaLast[n]);
    }

    private void StoreBlock(short[] levels, int offset, int last)
    {
        _levels.Add((short)last);

        for (var i = 0; i <= last; i++)
            _levels.Add(levels[offset + i]);
    }

    // Codes (or counts) all blocks of one macroblock and updates the "non-zero" contexts.
    private void CodeMacroblock(
        Vp8TokenCoder coder, Vp8BoolEncoder? writer, int mbX, bool intra16,
        ReadOnlySpan<short> secondOrder, int secondOrderLast,
        short[] luma, int[] lumaLast, short[] chroma, int[] chromaLast)
    {
        int first;
        int lumaType;

        if (intra16)
        {
            var context = (_nonZeroDcTop[mbX] ? 1 : 0) + (_nonZeroDcLeft ? 1 : 0);
            var nonZero = coder.CodeBlock(writer, Vp8TokenCoder.TypeLumaDc, context, 0, secondOrderLast, secondOrder);
            _nonZeroDcTop[mbX] = _nonZeroDcLeft = nonZero;
            first = 1;
            lumaType = Vp8TokenCoder.TypeLumaAc;
        }
        else
        {
            first = 0;
            lumaType = Vp8TokenCoder.TypeLuma4x4;
        }

        var top = mbX * 8;

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var n = (y * 4) + x;
                var context = (_nonZeroTop[top + x] ? 1 : 0) + (_nonZeroLeft[y] ? 1 : 0);
                var nonZero = coder.CodeBlock(writer, lumaType, context, first, lumaLast[n], luma.AsSpan(n * 16, 16));
                _nonZeroTop[top + x] = _nonZeroLeft[y] = nonZero;
            }
        }

        for (var channel = 0; channel < 2; channel++)
        {
            for (var y = 0; y < 2; y++)
            {
                for (var x = 0; x < 2; x++)
                {
                    var n = (channel * 4) + (y * 2) + x;
                    var topIndex = top + 4 + (channel * 2) + x;
                    var leftIndex = 4 + (channel * 2) + y;
                    var context = (_nonZeroTop[topIndex] ? 1 : 0) + (_nonZeroLeft[leftIndex] ? 1 : 0);
                    var nonZero = coder.CodeBlock(writer, Vp8TokenCoder.TypeChroma, context, 0, chromaLast[n], chroma.AsSpan(n * 16, 16));
                    _nonZeroTop[topIndex] = _nonZeroLeft[leftIndex] = nonZero;
                }
            }
        }
    }

    private byte[] WriteFirstPartition(bool useSkip, int skipProbability)
    {
        var header = new Vp8BoolEncoder();

        // Colour space and clamping type.
        header.PutBitUniform(0);
        header.PutBitUniform(0);

        // No segments.
        header.PutBitUniform(0);

        // Loop filter: normal, the level, sharpness 0, no per-mode deltas.
        header.PutBitUniform(0);
        header.PutBits(_filterLevel, 6);
        header.PutBits(0, 3);
        header.PutBitUniform(0);

        // One token partition.
        header.PutBits(0, 2);

        header.PutBits(_quantizer, 7);
        header.PutSignedBits(0, 4);
        header.PutSignedBits(0, 4);
        header.PutSignedBits(0, 4);
        header.PutSignedBits(ChromaDcDelta, 4);
        header.PutSignedBits(0, 4);

        // Whether probabilities persist to later frames; irrelevant for a single frame.
        header.PutBitUniform(0);
        _tokens.WriteProbabilities(header);

        header.PutBitUniform(useSkip ? 1 : 0);

        if (useSkip)
            header.PutBits(skipProbability, 8);

        Array.Clear(_modesTop);

        for (var mbY = 0; mbY < _macroblocksHigh; mbY++)
        {
            Array.Clear(_modesLeft);

            for (var mbX = 0; mbX < _macroblocksWide; mbX++)
                WriteModes(header, (mbY * _macroblocksWide) + mbX, mbX, useSkip, skipProbability);
        }

        return header.Finish();
    }

    private void WriteModes(Vp8BoolEncoder header, int index, int mbX, bool useSkip, int skipProbability)
    {
        if (useSkip)
            header.PutBit(_isEmpty[index] ? 1 : 0, skipProbability);

        header.PutBit(_isIntra16[index] ? 1 : 0, 145);

        if (_isIntra16[index])
        {
            int mode = _modes[index * 16];

            if (mode is Vp8Predictor.TrueMotionMode or Vp8Predictor.HorizontalMode)
            {
                header.PutBit(1, 156);
                header.PutBit(mode == Vp8Predictor.TrueMotionMode ? 1 : 0, 128);
            }
            else
            {
                header.PutBit(0, 156);
                header.PutBit(mode == Vp8Predictor.VerticalMode ? 1 : 0, 163);
            }

            Array.Fill(_modesTop, (byte)mode, mbX * 4, 4);
            Array.Fill(_modesLeft, (byte)mode);
        }
        else
        {
            var probabilities = Vp8Tables.SubblockModeProbabilities;

            for (var y = 0; y < 4; y++)
            {
                int left = _modesLeft[y];

                for (var x = 0; x < 4; x++)
                {
                    int mode = _modes[(index * 16) + (y * 4) + x];
                    var p = ((_modesTop[(mbX * 4) + x] * 10) + left) * 9;
                    WriteSubblockMode(header, mode, probabilities, p);
                    _modesTop[(mbX * 4) + x] = (byte)mode;
                    left = mode;
                }

                _modesLeft[y] = (byte)left;
            }
        }

        int chromaMode = _chromaModes[index];
        header.PutBit(chromaMode != Vp8Predictor.DcMode ? 1 : 0, 142);

        if (chromaMode == Vp8Predictor.DcMode)
            return;

        header.PutBit(chromaMode != Vp8Predictor.VerticalMode ? 1 : 0, 114);

        if (chromaMode != Vp8Predictor.VerticalMode)
            header.PutBit(chromaMode != Vp8Predictor.HorizontalMode ? 1 : 0, 183);
    }

    private static void WriteSubblockMode(Vp8BoolEncoder header, int mode, byte[] probabilities, int p)
    {
        header.PutBit(mode != 0 ? 1 : 0, probabilities[p]);

        if (mode == 0)
            return;

        header.PutBit(mode != 1 ? 1 : 0, probabilities[p + 1]);

        if (mode == 1)
            return;

        header.PutBit(mode != 2 ? 1 : 0, probabilities[p + 2]);

        if (mode == 2)
            return;

        if (mode < 6)
        {
            header.PutBit(0, probabilities[p + 3]);
            header.PutBit(mode != 3 ? 1 : 0, probabilities[p + 4]);

            if (mode != 3)
                header.PutBit(mode != 4 ? 1 : 0, probabilities[p + 5]);

            return;
        }

        header.PutBit(1, probabilities[p + 3]);
        header.PutBit(mode != 6 ? 1 : 0, probabilities[p + 6]);

        if (mode == 6)
            return;

        header.PutBit(mode != 7 ? 1 : 0, probabilities[p + 7]);

        if (mode != 7)
            header.PutBit(mode != 8 ? 1 : 0, probabilities[p + 8]);
    }

    private byte[] WriteTokens(bool useSkip)
    {
        var writer = new Vp8BoolEncoder();
        var secondOrder = new short[16];
        var luma = new short[16 * 16];
        var lumaLast = new int[16];
        var chroma = new short[8 * 16];
        var chromaLast = new int[8];
        var position = 0;

        Array.Clear(_nonZeroTop);
        Array.Clear(_nonZeroDcTop);

        for (var mbY = 0; mbY < _macroblocksHigh; mbY++)
        {
            Array.Clear(_nonZeroLeft);
            _nonZeroDcLeft = false;

            for (var mbX = 0; mbX < _macroblocksWide; mbX++)
            {
                var index = (mbY * _macroblocksWide) + mbX;
                var intra16 = _isIntra16[index];

                if (_isEmpty[index])
                {
                    if (useSkip)
                    {
                        // A skipped macroblock resets its contexts, the DC one only for 16x16 prediction.
                        Array.Clear(_nonZeroTop, mbX * 8, 8);
                        Array.Clear(_nonZeroLeft);

                        if (intra16)
                            _nonZeroDcTop[mbX] = _nonZeroDcLeft = false;

                        continue;
                    }

                    Array.Clear(secondOrder);
                    Array.Clear(luma);
                    Array.Clear(chroma);
                    Array.Fill(lumaLast, -1);
                    Array.Fill(chromaLast, -1);
                    CodeMacroblock(_tokens, writer, mbX, intra16, secondOrder, -1, luma, lumaLast, chroma, chromaLast);

                    continue;
                }

                var secondOrderLast = -1;

                if (intra16)
                    secondOrderLast = LoadBlock(ref position, secondOrder, 0);

                for (var n = 0; n < 16; n++)
                    lumaLast[n] = LoadBlock(ref position, luma, n * 16);

                for (var n = 0; n < 8; n++)
                    chromaLast[n] = LoadBlock(ref position, chroma, n * 16);

                CodeMacroblock(_tokens, writer, mbX, intra16, secondOrder, secondOrderLast, luma, lumaLast, chroma, chromaLast);
            }
        }

        return writer.Finish();
    }

    private int LoadBlock(ref int position, short[] levels, int offset)
    {
        int last = _levels[position++];
        Array.Clear(levels, offset, 16);

        for (var i = 0; i <= last; i++)
            levels[offset + i] = _levels[position++];

        return last;
    }

    private byte[] AssembleFrame(byte[] header, byte[] tokens)
    {
        var frame = new byte[10 + header.Length + tokens.Length];

        // Key frame, version 0, shown, and the size of the first partition.
        var tag = (1 << 4) | (header.Length << 5);
        frame[0] = (byte)tag;
        frame[1] = (byte)(tag >> 8);
        frame[2] = (byte)(tag >> 16);
        frame[3] = 0x9D;
        frame[4] = 0x01;
        frame[5] = 0x2A;
        frame[6] = (byte)_width;
        frame[7] = (byte)(_width >> 8);
        frame[8] = (byte)_height;
        frame[9] = (byte)(_height >> 8);
        header.CopyTo(frame, 10);
        tokens.CopyTo(frame, 10 + header.Length);

        return frame;
    }

    private void ConvertToYuv(RasterImage image)
    {
        var pixels = image.Pixels;
        var width = _width;
        var height = _height;

        for (var y = 0; y < _macroblocksHigh * 16; y++)
        {
            var sourceRow = Math.Min(y, height - 1) * width * 4;
            var targetRow = y * _strideY;

            for (var x = 0; x < _strideY; x++)
            {
                var source = sourceRow + (Math.Min(x, width - 1) * 4);
                int r = pixels[source], g = pixels[source + 1], b = pixels[source + 2];
                _planeY[targetRow + x] = (byte)(((16839 * r) + (33059 * g) + (6420 * b) + (1 << 15) + (16 << 16)) >> 16);
            }
        }

        var chromaWidth = (width + 1) >> 1;
        var chromaHeight = (height + 1) >> 1;

        for (var y = 0; y < _macroblocksHigh * 8; y++)
        {
            var cy = Math.Min(y, chromaHeight - 1);
            var row0 = 2 * cy * width * 4;
            var row1 = Math.Min((2 * cy) + 1, height - 1) * width * 4;

            for (var x = 0; x < _strideUv; x++)
            {
                var cx = Math.Min(x, chromaWidth - 1);
                var column0 = 2 * cx * 4;
                var column1 = Math.Min((2 * cx) + 1, width - 1) * 4;

                // Sums of the four pixels the chroma sample covers; a missing pixel repeats its neighbour.
                var r = pixels[row0 + column0] + pixels[row0 + column1] + pixels[row1 + column0] + pixels[row1 + column1];
                var g = pixels[row0 + column0 + 1] + pixels[row0 + column1 + 1] + pixels[row1 + column0 + 1] + pixels[row1 + column1 + 1];
                var b = pixels[row0 + column0 + 2] + pixels[row0 + column1 + 2] + pixels[row1 + column0 + 2] + pixels[row1 + column1 + 2];

                _planeU[(y * _strideUv) + x] = ClipChroma((-9719 * r) - (19081 * g) + (28800 * b));
                _planeV[(y * _strideUv) + x] = ClipChroma((28800 * r) - (24116 * g) - (4684 * b));
            }
        }

        if (image.HasTransparency())
            FlattenTransparentBlocks(pixels);
    }

    private static byte ClipChroma(int value)
    {
        var result = (value + (1 << 17) + (128 << 18)) >> 18;

        return result < 0 ? (byte)0 : result > 255 ? (byte)255 : (byte)result;
    }

    // The colour under fully transparent pixels is invisible, so it is chosen to be cheap, as libwebp
    // does: fully transparent 8x8 areas are flattened to the colour where a run of them starts, and in
    // partly transparent ones the luma of the transparent pixels becomes the mean of the others.
    private void FlattenTransparentBlocks(byte[] pixels)
    {
        int y;

        for (y = 0; y + 8 <= _height; y += 8)
        {
            var reset = true;
            byte flatY = 0, flatU = 0, flatV = 0;
            int x;

            for (x = 0; x + 8 <= _width; x += 8)
            {
                if (!SmoothenBlock(pixels, x, y, 8, 8))
                {
                    reset = true;
                    continue;
                }

                if (reset)
                {
                    flatY = _planeY[(y * _strideY) + x];
                    flatU = _planeU[((y >> 1) * _strideUv) + (x >> 1)];
                    flatV = _planeV[((y >> 1) * _strideUv) + (x >> 1)];
                    reset = false;
                }

                for (var row = 0; row < 8; row++)
                    Array.Fill(_planeY, flatY, ((y + row) * _strideY) + x, 8);

                for (var row = 0; row < 4; row++)
                {
                    Array.Fill(_planeU, flatU, (((y >> 1) + row) * _strideUv) + (x >> 1), 4);
                    Array.Fill(_planeV, flatV, (((y >> 1) + row) * _strideUv) + (x >> 1), 4);
                }
            }

            if (x < _width)
                SmoothenBlock(pixels, x, y, _width - x, 8);
        }

        if (y < _height)
        {
            int x;

            for (x = 0; x + 8 <= _width; x += 8)
                SmoothenBlock(pixels, x, y, 8, _height - y);

            if (x < _width)
                SmoothenBlock(pixels, x, y, _width - x, _height - y);
        }
    }

    // Returns true when the area is fully transparent; otherwise gives its transparent pixels the mean luma of the rest.
    private bool SmoothenBlock(byte[] pixels, int left, int top, int width, int height)
    {
        var sum = 0;
        var count = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[((((top + y) * _width) + left + x) * 4) + 3] != 0)
                {
                    count++;
                    sum += _planeY[((top + y) * _strideY) + left + x];
                }
            }
        }

        if (count > 0 && count < width * height)
        {
            var average = (byte)(sum / count);

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (pixels[((((top + y) * _width) + left + x) * 4) + 3] == 0)
                        _planeY[((top + y) * _strideY) + left + x] = average;
                }
            }
        }

        return count == 0;
    }

    private void LoadSource(int mbX, int mbY)
    {
        for (var y = 0; y < 16; y++)
            Array.Copy(_planeY, ((mbY * 16) + y) * _strideY + (mbX * 16), _source, LumaOffset + (y * Stride), 16);

        for (var y = 0; y < 8; y++)
        {
            var row = ((mbY * 8) + y) * _strideUv + (mbX * 8);
            Array.Copy(_planeU, row, _source, UOffset + (y * Stride), 8);
            Array.Copy(_planeV, row, _source, VOffset + (y * Stride), 8);
        }
    }

    // The same borders the decoder sets up at the start of a macroblock row.
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

    // The left column, the row above and the top-right pixels, as the decoder sets them up.
    private void PrepareMacroblock(int mbX, int mbY)
    {
        var work = _work;

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

            var topRight = LumaOffset - Stride + 16;

            if (mbX >= _macroblocksWide - 1)
                Array.Fill(work, _topY[(mbX * 16) + 15], topRight, 4);
            else
                Array.Copy(_topY, (mbX + 1) * 16, work, topRight, 4);
        }

        var corner = LumaOffset - Stride + 16;

        for (var row = 4; row <= 12; row += 4)
            Array.Copy(work, corner, work, corner + (row * Stride), 4);
    }

    private void SaveTopSamples(int mbX, int mbY)
    {
        if (mbY >= _macroblocksHigh - 1)
            return;

        Array.Copy(_work, LumaOffset + (15 * Stride), _topY, mbX * 16, 16);
        Array.Copy(_work, UOffset + (7 * Stride), _topU, mbX * 8, 8);
        Array.Copy(_work, VOffset + (7 * Stride), _topV, mbX * 8, 8);
    }

    private long Distortion(int offset, int width, int height)
    {
        long sum = 0;

        for (var y = 0; y < height; y++)
        {
            var row = offset + (y * Stride);

            for (var x = 0; x < width; x++)
            {
                var difference = _source[row + x] - _work[row + x];
                sum += difference * difference;
            }
        }

        return sum;
    }

    private bool IsFlatSource()
    {
        var value = _source[LumaOffset];

        for (var y = 0; y < 16; y++)
        {
            var row = LumaOffset + (y * Stride);

            for (var x = 0; x < 16; x++)
            {
                if (_source[row + x] != value)
                    return false;
            }
        }

        return true;
    }

    private static int CheckMode(int mbX, int mbY, int mode)
    {
        if (mode != Vp8Predictor.DcMode)
            return mode;

        if (mbX == 0)
            return mbY == 0 ? Vp8Predictor.DcNoTopLeftMode : Vp8Predictor.DcNoLeftMode;

        return mbY == 0 ? Vp8Predictor.DcNoTopMode : Vp8Predictor.DcMode;
    }
}
