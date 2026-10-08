namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// The boolean entropy encoder of VP8 (RFC 6386 section 7), the counterpart of
/// <see cref="Vp8BoolDecoder"/>, in libwebp's formulation: carries into bytes already written are
/// handled by holding back runs of 0xFF bytes.
/// </summary>
internal sealed class Vp8BoolEncoder
{
    private byte[] _buffer = new byte[4096];
    private int _position;
    private int _range = 255 - 1;
    private int _value;
    private int _run;
    private int _bits = -8;

    /// <summary>Codes <paramref name="bit"/> with a probability of <paramref name="probability"/>/256 of being 0.</summary>
    public void PutBit(int bit, int probability)
    {
        var split = (_range * probability) >> 8;

        if (bit != 0)
        {
            _value += split + 1;
            _range -= split + 1;
        }
        else
        {
            _range = split;
        }

        if (_range < 127)
        {
            // Renormalise: the stored range is the real range minus one.
            var shift = 7 - (31 - int.LeadingZeroCount(_range + 1));
            _range = ((_range + 1) << shift) - 1;
            _value <<= shift;
            _bits += shift;

            if (_bits > 0)
                Flush();
        }
    }

    public void PutBitUniform(int bit) => PutBit(bit, 0x80);

    /// <summary>Writes the <paramref name="count"/> low bits of <paramref name="value"/>, most significant first.</summary>
    public void PutBits(int value, int count)
    {
        for (var mask = 1 << (count - 1); mask != 0; mask >>= 1)
            PutBitUniform((value & mask) != 0 ? 1 : 0);
    }

    /// <summary>A flag for a non-zero value, then its magnitude in <paramref name="count"/> bits and its sign.</summary>
    public void PutSignedBits(int value, int count)
    {
        if (value == 0)
        {
            PutBitUniform(0);
            return;
        }

        PutBitUniform(1);
        PutBits(value < 0 ? (-value << 1) | 1 : value << 1, count + 1);
    }

    /// <summary>Ends the stream and returns its bytes.</summary>
    public byte[] Finish()
    {
        PutBits(0, 9 - _bits);
        _bits = 0;
        Flush();

        return _buffer.AsSpan(0, _position).ToArray();
    }

    private void Flush()
    {
        var shift = 8 + _bits;
        var bits = _value >> shift;
        _value -= bits << shift;
        _bits -= 8;

        if ((bits & 0xFF) == 0xFF)
        {
            // A later carry could still turn this byte into 0x00; hold it back.
            _run++;
            return;
        }

        EnsureCapacity(_run + 1);

        if ((bits & 0x100) != 0 && _position > 0)
            _buffer[_position - 1]++;

        if (_run > 0)
        {
            var pending = (bits & 0x100) != 0 ? (byte)0x00 : (byte)0xFF;

            for (; _run > 0; _run--)
                _buffer[_position++] = pending;
        }

        _buffer[_position++] = (byte)(bits & 0xFF);
    }

    private void EnsureCapacity(int extra)
    {
        if (_position + extra <= _buffer.Length)
            return;

        Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _position + extra));
    }
}
