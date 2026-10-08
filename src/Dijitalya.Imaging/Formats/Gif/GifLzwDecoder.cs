namespace Dijitalya.Imaging.Formats.Gif;

/// <summary>
/// The variable-length LZW of GIF: codes are read least significant bit first, grow from
/// (minimum code size + 1) to 12 bits, and the clear code starts the dictionary over. Decoding stops
/// at the end-of-information code, at the end of the data, when the output is full, or at the first
/// code that cannot be valid; what was decoded until then is kept.
/// </summary>
internal static class GifLzwDecoder
{
    private const int MaxCodeSize = 12;
    private const int MaxCodes = 1 << MaxCodeSize;

    /// <param name="data">The image data with its sub-block lengths already removed.</param>
    /// <param name="minimumCodeSize">The LZW minimum code size of the image (1 to 11).</param>
    /// <param name="output">Receives the colour indexes; decoding stops when it is full.</param>
    /// <returns>How many indexes were written.</returns>
    public static int Decode(ReadOnlySpan<byte> data, int minimumCodeSize, Span<byte> output)
    {
        if (minimumCodeSize is < 1 or > 11)
            throw new InvalidImageContentException("The GIF uses an invalid LZW code size.");

        var prefixes = new short[MaxCodes];
        var suffixes = new byte[MaxCodes];
        var firsts = new byte[MaxCodes];
        var stack = new byte[MaxCodes + 1];

        var clearCode = 1 << minimumCodeSize;
        var endCode = clearCode + 1;
        var nextCode = clearCode + 2;
        var codeSize = minimumCodeSize + 1;
        var previous = -1;
        var written = 0;

        for (var code = 0; code < clearCode; code++)
        {
            prefixes[code] = -1;
            suffixes[code] = (byte)code;
            firsts[code] = (byte)code;
        }

        var bitBuffer = 0;
        var bitCount = 0;
        var position = 0;

        while (written < output.Length)
        {
            while (bitCount < codeSize && position < data.Length)
            {
                bitBuffer |= data[position++] << bitCount;
                bitCount += 8;
            }

            if (bitCount < codeSize)
                break;

            var code = bitBuffer & ((1 << codeSize) - 1);
            bitBuffer >>= codeSize;
            bitCount -= codeSize;

            if (code == clearCode)
            {
                nextCode = clearCode + 2;
                codeSize = minimumCodeSize + 1;
                previous = -1;
                continue;
            }

            if (code == endCode)
                break;

            if (previous == -1)
            {
                if (code >= clearCode)
                    break;

                output[written++] = (byte)code;
                previous = code;
                continue;
            }

            int length;

            if (code < nextCode)
            {
                length = Expand(code, prefixes, suffixes, stack);
            }
            else if (code == nextCode)
            {
                // The code being defined right now: the previous string followed by its own first index.
                length = Expand(previous, prefixes, suffixes, stack);
                stack[length++] = firsts[previous];
            }
            else
            {
                break;
            }

            if (length == 0)
                break;

            var first = stack[0];

            for (var index = 0; index < length && written < output.Length; index++)
                output[written++] = stack[index];

            if (nextCode < MaxCodes)
            {
                prefixes[nextCode] = (short)previous;
                suffixes[nextCode] = first;
                firsts[nextCode] = firsts[previous];
                nextCode++;

                if (nextCode == 1 << codeSize && codeSize < MaxCodeSize)
                    codeSize++;
            }

            previous = code;
        }

        return written;
    }

    /// <summary>Writes the string of a code into <paramref name="stack"/> in reading order; returns its length (0 for a damaged chain).</summary>
    private static int Expand(int code, short[] prefixes, byte[] suffixes, byte[] stack)
    {
        var length = 0;

        while (code >= 0)
        {
            if (length >= MaxCodes)
                return 0;

            stack[length++] = suffixes[code];
            code = prefixes[code];
        }

        Array.Reverse(stack, 0, length);

        return length;
    }
}
