namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// A canonical prefix code of VP8L built from code lengths (at most 15 bits). Codes are read bit by
/// bit from the least significant end of the stream, most significant code bit first. Codes of up to
/// 8 bits are found with one table lookup whose size follows the longest code, so the many small
/// codes of a file stay small in memory. A code with a single symbol takes no bits at all. A set of
/// lengths that does not form a complete code is refused, as libwebp refuses it.
/// </summary>
internal sealed class Vp8LHuffmanCode
{
    private const int MaxLength = 15;
    private const int MaxFastBits = 8;

    // (length << 12) | symbol for each pattern of the table's bits whose code is that short; 0 otherwise.
    private readonly ushort[] _fast;
    private readonly int _fastMask;

    // For codes longer than the table: per length, the first code, the number of codes and where their symbols start in _sorted.
    private readonly int[]? _firstCode;
    private readonly int[]? _count;
    private readonly int[]? _offset;
    private readonly int[]? _sorted;
    private readonly int _singleSymbol;

    private Vp8LHuffmanCode(int singleSymbol)
    {
        _fast = [];
        _singleSymbol = singleSymbol;
    }

    private Vp8LHuffmanCode(int fastBits, bool needsSlowPath, int symbols)
    {
        _fast = DecodeContext.Allocate<ushort>(checked(1 << fastBits));
        _fastMask = (1 << fastBits) - 1;
        _singleSymbol = -1;

        if (needsSlowPath)
        {
            _firstCode = DecodeContext.Allocate<int>(checked(MaxLength + 1));
            _count = DecodeContext.Allocate<int>(checked(MaxLength + 1));
            _offset = DecodeContext.Allocate<int>(checked(MaxLength + 1));
            _sorted = DecodeContext.Allocate<int>(checked(symbols));
        }
    }

    /// <summary>Builds the code; throws <see cref="InvalidImageContentException"/> for lengths that do not form one.</summary>
    public static Vp8LHuffmanCode Create(ReadOnlySpan<int> lengths)
    {
        Span<int> counts = stackalloc int[MaxLength + 1];
        counts.Clear();
        var symbols = 0;
        var last = -1;
        var longest = 0;

        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            var length = lengths[symbol];

            if (length is < 0 or > MaxLength)
                throw new InvalidImageContentException("A WebP prefix code length is out of range.");

            if (length > 0)
            {
                counts[length]++;
                symbols++;
                last = symbol;
                longest = Math.Max(longest, length);
            }
        }

        if (symbols == 0)
            throw new InvalidImageContentException("A WebP prefix code has no symbols.");

        if (symbols == 1)
            return new Vp8LHuffmanCode(last);

        // The code must be complete: every branch of the tree used, none oversubscribed.
        var open = 1;

        for (var length = 1; length <= MaxLength; length++)
        {
            open = (open << 1) - counts[length];

            if (open < 0)
                throw new InvalidImageContentException("A WebP prefix code is oversubscribed.");
        }

        if (open != 0)
            throw new InvalidImageContentException("A WebP prefix code is incomplete.");

        var fastBits = Math.Min(longest, MaxFastBits);
        var code = new Vp8LHuffmanCode(fastBits, longest > MaxFastBits, symbols);

        Span<int> firstCode = stackalloc int[MaxLength + 1];
        firstCode.Clear();
        Span<int> offset = stackalloc int[MaxLength + 1];
        offset.Clear();
        var next = 0;
        var index = 0;

        for (var length = 1; length <= MaxLength; length++)
        {
            firstCode[length] = next;
            offset[length] = index;
            next = (next + counts[length]) << 1;
            index += counts[length];
        }

        if (code._sorted is not null)
        {
            firstCode.CopyTo(code._firstCode);
            counts.CopyTo(code._count);
            offset.CopyTo(code._offset);
        }

        Span<int> position = stackalloc int[MaxLength + 1];
        offset.CopyTo(position);

        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            var length = lengths[symbol];

            if (length == 0)
                continue;

            var rank = position[length]++;

            if (code._sorted is not null)
                code._sorted[rank] = symbol;

            if (length > fastBits)
                continue;

            var value = firstCode[length] + (rank - offset[length]);
            var reversed = Reverse(value, length);
            var entry = (ushort)((length << 12) | symbol);

            for (var pattern = reversed; pattern < 1 << fastBits; pattern += 1 << length)
                code._fast[pattern] = entry;
        }

        return code;
    }

    public int Decode(Vp8LBitReader reader)
    {
        if (_singleSymbol >= 0)
            return _singleSymbol;

        var bits = reader.PeekBits(MaxLength);
        var entry = _fast[bits & _fastMask];

        if (entry != 0)
        {
            reader.SkipBits(entry >> 12);
            return entry & 0xFFF;
        }

        return DecodeSlow(reader, bits);
    }

    private int DecodeSlow(Vp8LBitReader reader, int bits)
    {
        if (_sorted is null || _firstCode is null || _count is null || _offset is null)
            throw new InvalidImageContentException("The WebP data holds a code its prefix code does not have.");

        var code = 0;

        for (var length = 1; length <= MaxLength; length++)
        {
            code = (code << 1) | ((bits >> (length - 1)) & 1);
            var index = code - _firstCode[length];

            if (index >= 0 && index < _count[length])
            {
                reader.SkipBits(length);
                return _sorted[_offset[length] + index];
            }
        }

        throw new InvalidImageContentException("The WebP data holds a code its prefix code does not have.");
    }

    private static int Reverse(int value, int length)
    {
        var result = 0;

        for (var bit = 0; bit < length; bit++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }
}
