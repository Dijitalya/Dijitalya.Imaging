namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>
/// A Huffman table of a JPEG (DHT), built for decoding: codes of up to <see cref="LookupBits"/> bits are
/// found with one table lookup, longer ones (rare) by comparing with the largest code of each length.
/// </summary>
internal sealed class JpegHuffmanTable
{
    public const int LookupBits = 9;

    // (length << 8) | symbol for every bit pattern of LookupBits bits whose code is that short; 0 otherwise.
    private readonly ushort[] _lookup = DecodeContext.Allocate<ushort>(checked(1 << LookupBits));
    private readonly int[] _maxCode = DecodeContext.Allocate<int>(checked(17));
    private readonly int[] _valueOffset = DecodeContext.Allocate<int>(checked(17));
    private readonly byte[] _symbols;

    /// <param name="counts">How many codes have each length from 1 to 16 bits.</param>
    /// <param name="symbols">The symbols, in the order of their codes.</param>
    public JpegHuffmanTable(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
    {
        var total = 0;

        foreach (var count in counts)
            total += count;

        if (counts.Length != 16 || total > 256 || total != symbols.Length)
            throw new InvalidImageContentException("A JPEG Huffman table is malformed.");

        _symbols = DecodeContext.Copy(symbols);

        var code = 0;
        var index = 0;

        for (var length = 1; length <= 16; length++)
        {
            int count = counts[length - 1];

            if (code + count > 1 << length)
                throw new InvalidImageContentException("A JPEG Huffman table has more codes than fit.");

            _valueOffset[length] = index - code;

            for (var item = 0; item < count; item++)
            {
                if (length <= LookupBits)
                {
                    var shift = LookupBits - length;
                    var start = code << shift;
                    var entry = (ushort)((length << 8) | _symbols[index]);

                    for (var fill = 0; fill < 1 << shift; fill++)
                        _lookup[start + fill] = entry;
                }

                code++;
                index++;
            }

            _maxCode[length] = count > 0 ? code - 1 : -1;
            code <<= 1;
        }
    }

    /// <summary>Reads one symbol.</summary>
    public int Decode(JpegBitReader reader)
    {
        reader.EnsureBits(16);

        var entry = _lookup[reader.PeekBits(LookupBits)];

        if (entry != 0)
        {
            reader.SkipBits(entry >> 8);
            return entry & 0xFF;
        }

        var bits = reader.PeekBits(16);

        for (var length = LookupBits + 1; length <= 16; length++)
        {
            var code = bits >> (16 - length);

            if (code <= _maxCode[length])
            {
                reader.SkipBits(length);
                return _symbols[code + _valueOffset[length]];
            }
        }

        throw new InvalidImageContentException("The JPEG data holds a code its Huffman table does not have.");
    }
}
