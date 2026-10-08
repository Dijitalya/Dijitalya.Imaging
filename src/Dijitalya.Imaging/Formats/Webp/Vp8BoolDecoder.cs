namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// The boolean entropy decoder of VP8 (RFC 6386 section 7), in the formulation of libwebp so that
/// every decision matches it bit for bit. Past the end of its data it reads zero bytes and reports
/// <see cref="IsEndOfStream"/>.
/// </summary>
internal sealed class Vp8BoolDecoder
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _position;
    private ulong _value;
    private int _range;
    private int _bits;
    private bool _atEnd;

    public Vp8BoolDecoder(byte[] data, int start, int length)
    {
        _data = data;
        _position = start;
        _end = start + length;
        _range = 255 - 1;
        _bits = -8;
        LoadByte();
    }

    /// <summary>True once the decoder has had to read beyond its data.</summary>
    public bool IsEndOfStream => _atEnd;

    /// <summary>Reads one bit whose probability of being 0 is <paramref name="probability"/>/256.</summary>
    public int GetBit(int probability)
    {
        if (_bits < 0)
            LoadByte();

        var range = _range;
        var split = (range * probability) >> 8;
        var value = (int)(_value >> _bits);
        int bit;

        if (value > split)
        {
            range -= split;
            _value -= (ulong)(split + 1) << _bits;
            bit = 1;
        }
        else
        {
            range = split + 1;
            bit = 0;
        }

        // Renormalise the range into [128, 255].
        var shift = 7 ^ Log2Floor(range);
        range <<= shift;
        _bits -= shift;
        _range = range - 1;

        return bit;
    }

    /// <summary>An unsigned value of <paramref name="count"/> bits, most significant first, each with probability 1/2.</summary>
    public int GetValue(int count)
    {
        var value = 0;

        while (count-- > 0)
            value |= GetBit(0x80) << count;

        return value;
    }

    /// <summary>A value of <paramref name="count"/> bits followed by its sign.</summary>
    public int GetSignedValue(int count)
    {
        var value = GetValue(count);

        return GetBit(0x80) == 1 ? -value : value;
    }

    /// <summary>Returns <paramref name="value"/> or its negation, chosen by one bit of probability 1/2.</summary>
    public int GetSigned(int value) => GetBit(0x80) == 1 ? -value : value;

    private void LoadByte()
    {
        if (_position < _end)
        {
            _bits += 8;
            _value = _data[_position++] | (_value << 8);
        }
        else if (!_atEnd)
        {
            _value <<= 8;
            _bits += 8;
            _atEnd = true;
        }
        else
        {
            _bits = 0;
        }
    }

    private static int Log2Floor(int value) => 31 - int.LeadingZeroCount(value);
}
