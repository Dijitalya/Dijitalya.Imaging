namespace Dijitalya.Imaging.Formats.Gif;

/// <summary>
/// The variable-length LZW of GIF, the counterpart of <see cref="GifLzwDecoder"/>: codes grow from
/// (minimum code size + 1) to 12 bits, and a clear code starts the dictionary over when it is full.
/// The dictionary is a hash table, as in the classic compress-based GIF encoders.
/// </summary>
internal sealed class GifLzwEncoder
{
    private const int MaxBits = 12;
    private const int MaxMaxCode = 1 << MaxBits;
    private const int HashSize = 5003;
    private const int MaxSubBlock = 255;

    private readonly Stream _output;
    private readonly int _initialBits;
    private readonly int _clearCode;
    private readonly byte[] _block = new byte[MaxSubBlock];
    private int _blockLength;
    private int _accumulator;
    private int _bits;
    private int _codeSize;
    private int _maxCode;
    private int _nextCode;
    private bool _clearing;

    private GifLzwEncoder(int minimumCodeSize, Stream output)
    {
        _output = output;
        _initialBits = minimumCodeSize + 1;
        _clearCode = 1 << minimumCodeSize;
        _codeSize = _initialBits;
        _maxCode = (1 << _codeSize) - 1;
        _nextCode = _clearCode + 2;
    }

    /// <summary>Writes the LZW minimum code size, then the compressed indexes as data sub-blocks and their terminator.</summary>
    public static void Encode(ReadOnlySpan<byte> indexes, int minimumCodeSize, Stream output)
    {
        output.WriteByte((byte)minimumCodeSize);
        new GifLzwEncoder(minimumCodeSize, output).Compress(indexes);
    }

    private void Compress(ReadOnlySpan<byte> indexes)
    {
        var hashes = new int[HashSize];
        var codes = new int[HashSize];
        Array.Fill(hashes, -1);

        var shift = 0;

        for (var size = HashSize; size < 65536; size *= 2)
            shift++;

        shift = 8 - shift;

        Output(_clearCode);

        var prefix = (int)indexes[0];

        for (var position = 1; position < indexes.Length; position++)
        {
            int symbol = indexes[position];
            var key = (symbol << MaxBits) + prefix;
            var slot = (symbol << shift) ^ prefix;

            if (hashes[slot] == key)
            {
                prefix = codes[slot];
                continue;
            }

            if (hashes[slot] >= 0 && Probe(hashes, key, ref slot))
            {
                prefix = codes[slot];
                continue;
            }

            Output(prefix);
            prefix = symbol;

            if (_nextCode < MaxMaxCode)
            {
                codes[slot] = _nextCode++;
                hashes[slot] = key;
            }
            else
            {
                // The dictionary is full: start over.
                Array.Fill(hashes, -1);
                _nextCode = _clearCode + 2;
                _clearing = true;
                Output(_clearCode);
            }
        }

        Output(prefix);
        Output(_clearCode + 1);
        Finish();
    }

    // Secondary probing: true when the key is found (slot points at it), false at the first free slot.
    private static bool Probe(int[] hashes, int key, ref int slot)
    {
        var step = slot == 0 ? 1 : HashSize - slot;

        do
        {
            slot -= step;

            if (slot < 0)
                slot += HashSize;

            if (hashes[slot] == key)
                return true;
        }
        while (hashes[slot] >= 0);

        return false;
    }

    private void Output(int code)
    {
        _accumulator &= (1 << _bits) - 1;
        _accumulator |= code << _bits;
        _bits += _codeSize;

        while (_bits >= 8)
        {
            Put((byte)_accumulator);
            _accumulator >>= 8;
            _bits -= 8;
        }

        // The code size grows once the next code would not fit, exactly when the decoder's does.
        if (_nextCode > _maxCode || _clearing)
        {
            if (_clearing)
            {
                _codeSize = _initialBits;
                _maxCode = (1 << _codeSize) - 1;
                _clearing = false;
            }
            else
            {
                _codeSize++;
                _maxCode = _codeSize == MaxBits ? MaxMaxCode : (1 << _codeSize) - 1;
            }
        }
    }

    private void Finish()
    {
        while (_bits > 0)
        {
            Put((byte)_accumulator);
            _accumulator >>= 8;
            _bits -= 8;
        }

        Flush();
        _output.WriteByte(0);
    }

    private void Put(byte value)
    {
        _block[_blockLength++] = value;

        if (_blockLength == MaxSubBlock)
            Flush();
    }

    private void Flush()
    {
        if (_blockLength == 0)
            return;

        _output.WriteByte((byte)_blockLength);
        _output.Write(_block, 0, _blockLength);
        _blockLength = 0;
    }
}
