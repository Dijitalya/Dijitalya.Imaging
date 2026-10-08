namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// A canonical prefix code the VP8L encoder builds from symbol counts, limited to a maximum code
/// length, and knows how to describe in the bitstream: as a "simple" code of one or two symbols, or
/// with code lengths that are themselves run-length and prefix coded (RFC 9649 section 3.7.2).
/// </summary>
internal sealed class Vp8LEncoderCode
{
    private const int MaxLength = 15;
    private const int MaxCodeLengthLength = 7;

    private static readonly int[] CodeLengthCodeOrder = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private readonly int[] _lengths;
    private readonly uint[] _codes;

    // A code with a single symbol takes no bits in the data.
    private readonly bool _isTrivial;

    private Vp8LEncoderCode(int[] lengths)
    {
        _lengths = lengths;
        _codes = CanonicalCodes(lengths);
        _isTrivial = lengths.Count(length => length > 0) <= 1;
    }

    /// <summary>Builds the code for an alphabet of <c>counts.Length</c> symbols.</summary>
    public static Vp8LEncoderCode Build(long[] counts) => new(BuildLengths(counts, MaxLength));

    /// <summary>Writes one symbol (reversed, since the decoder reads codes most significant bit first).</summary>
    public void WriteSymbol(Vp8LBitWriter writer, int symbol)
    {
        if (!_isTrivial)
            writer.WriteBits(_codes[symbol], _lengths[symbol]);
    }

    /// <summary>The number of bits the code spends on <paramref name="counts"/>, without its own description.</summary>
    public long Cost(long[] counts)
    {
        long bits = 0;

        if (_isTrivial)
            return 0;

        for (var i = 0; i < counts.Length; i++)
            bits += counts[i] * _lengths[i];

        return bits;
    }

    /// <summary>Writes the description of the code.</summary>
    public void WriteDescription(Vp8LBitWriter writer)
    {
        var usedCount = 0;
        var first = 0;
        var second = 0;

        for (var i = 0; i < _lengths.Length; i++)
        {
            if (_lengths[i] == 0)
                continue;

            if (usedCount == 0)
                first = i;
            else if (usedCount == 1)
                second = i;

            usedCount++;
        }

        // No symbol at all (an alphabet the data never uses) is written as a code of symbol 0.
        if (usedCount == 0 || (usedCount <= 2 && (usedCount == 1 ? first : second) < 256))
        {
            writer.WriteBits(1, 1);
            writer.WriteBits(usedCount == 2 ? 1u : 0u, 1);

            if (first < 2)
            {
                writer.WriteBits(0, 1);
                writer.WriteBits((uint)first, 1);
            }
            else
            {
                writer.WriteBits(1, 1);
                writer.WriteBits((uint)first, 8);
            }

            if (usedCount == 2)
                writer.WriteBits((uint)second, 8);

            return;
        }

        WriteFullDescription(writer);
    }

    private void WriteFullDescription(Vp8LBitWriter writer)
    {
        // Run-length tokens of the code lengths: 0-15 literal, 16 repeat previous, 17 and 18 runs of zeros.
        var tokens = new List<(int Code, int Extra)>();
        var previous = 8;
        var position = 0;

        while (position < _lengths.Length)
        {
            var value = _lengths[position];
            var run = 1;

            while (position + run < _lengths.Length && _lengths[position + run] == value)
                run++;

            position += run;

            if (value == 0)
            {
                while (run > 0)
                {
                    if (run < 3)
                    {
                        for (; run > 0; run--)
                            tokens.Add((0, 0));
                    }
                    else if (run <= 10)
                    {
                        tokens.Add((17, run - 3));
                        run = 0;
                    }
                    else
                    {
                        var repeat = Math.Min(run, 138);
                        tokens.Add((18, repeat - 11));
                        run -= repeat;
                    }
                }

                continue;
            }

            if (value != previous)
            {
                tokens.Add((value, 0));
                previous = value;
                run--;
            }

            while (run > 0)
            {
                if (run < 3)
                {
                    for (; run > 0; run--)
                        tokens.Add((value, 0));
                }
                else
                {
                    var repeat = Math.Min(run, 6);
                    tokens.Add((16, repeat - 3));
                    run -= repeat;
                }
            }
        }

        var counts = new long[19];

        foreach (var (code, _) in tokens)
            counts[code]++;

        var lengthLengths = BuildLengths(counts, MaxCodeLengthLength);
        var lengthCodes = CanonicalCodes(lengthLengths);
        var codeCount = 19;

        while (codeCount > 4 && lengthLengths[CodeLengthCodeOrder[codeCount - 1]] == 0)
            codeCount--;

        writer.WriteBits(0, 1);
        writer.WriteBits((uint)(codeCount - 4), 4);

        for (var i = 0; i < codeCount; i++)
            writer.WriteBits((uint)lengthLengths[CodeLengthCodeOrder[i]], 3);

        // All symbols are described; no shortened length.
        writer.WriteBits(0, 1);

        foreach (var (code, extra) in tokens)
        {
            if (lengthLengths[code] > 0)
                writer.WriteBits(lengthCodes[code], lengthLengths[code]);

            switch (code)
            {
                case 16:
                    writer.WriteBits((uint)extra, 2);
                    break;
                case 17:
                    writer.WriteBits((uint)extra, 3);
                    break;
                case 18:
                    writer.WriteBits((uint)extra, 7);
                    break;
            }
        }
    }

    // Huffman code lengths of at most 'maxLength' bits. When the plain Huffman tree is too deep, the
    // rare symbols' counts are raised step by step until it fits, as libwebp does.
    private static int[] BuildLengths(long[] counts, int maxLength)
    {
        var lengths = new int[counts.Length];
        var used = 0;
        var last = 0;

        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                used++;
                last = i;
            }
        }

        if (used == 0)
            return lengths;

        if (used == 1)
        {
            lengths[last] = 1;
            return lengths;
        }

        for (long minimum = 1; ; minimum *= 2)
        {
            if (TryBuildTree(counts, minimum, maxLength, lengths))
                return lengths;
        }
    }

    private static bool TryBuildTree(long[] counts, long minimum, int maxLength, int[] lengths)
    {
        // Nodes 0..n-1 are leaves (symbols); the rest are internal nodes.
        var symbols = new List<int>();

        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
                symbols.Add(i);
        }

        var leafCount = symbols.Count;
        var parent = new int[(2 * leafCount) - 1];
        var queue = new PriorityQueue<int, (long Weight, int Order)>();

        for (var i = 0; i < leafCount; i++)
            queue.Enqueue(i, (Math.Max(counts[symbols[i]], minimum), i));

        var next = leafCount;

        while (queue.Count > 1)
        {
            queue.TryDequeue(out var a, out var weightA);
            queue.TryDequeue(out var b, out var weightB);
            parent[a] = next;
            parent[b] = next;
            queue.Enqueue(next, (weightA.Weight + weightB.Weight, next));
            next++;
        }

        var root = next - 1;
        var depth = new int[parent.Length];

        for (var node = root - 1; node >= 0; node--)
            depth[node] = depth[parent[node]] + 1;

        for (var i = 0; i < leafCount; i++)
        {
            if (depth[i] > maxLength)
                return false;
        }

        Array.Clear(lengths);

        for (var i = 0; i < leafCount; i++)
            lengths[symbols[i]] = depth[i];

        return true;
    }

    // Canonical codes, bit-reversed so they can be written least significant bit first.
    private static uint[] CanonicalCodes(int[] lengths)
    {
        var codes = new uint[lengths.Length];
        var counts = new int[MaxLength + 1];

        foreach (var length in lengths)
        {
            if (length > 0)
                counts[length]++;
        }

        var next = new int[MaxLength + 1];
        var code = 0;

        for (var length = 1; length <= MaxLength; length++)
        {
            next[length] = code;
            code = (code + counts[length]) << 1;
        }

        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            var length = lengths[symbol];

            if (length > 0)
                codes[symbol] = Reverse((uint)next[length]++, length);
        }

        return codes;
    }

    private static uint Reverse(uint value, int length)
    {
        uint result = 0;

        for (var bit = 0; bit < length; bit++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }
}
