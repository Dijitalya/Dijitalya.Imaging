namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>
/// Reads the entropy-coded data of a scan bit by bit, most significant bit first. A 0xFF byte is
/// followed by a stuffed 0x00, which is skipped. A marker ends the data: the reader stops in front of
/// it and from then on supplies zero bits, so damaged or truncated data decodes to something instead
/// of reading past the scan.
/// </summary>
internal sealed class JpegBitReader(byte[] data, int position)
{
    private ulong _buffer;
    private int _count;
    private bool _atMarker;

    /// <summary>The next byte not yet taken into the buffer; at a marker, the marker's 0xFF.</summary>
    public int Position { get; private set; } = position;

    public void EnsureBits(int count)
    {
        while (_count < count)
            FillByte();
    }

    /// <summary>The next <paramref name="count"/> (at most 16) bits, without taking them; call <see cref="EnsureBits"/> first.</summary>
    public int PeekBits(int count) => (int)((_buffer >> (_count - count)) & ((1UL << count) - 1));

    public void SkipBits(int count) => _count -= count;

    /// <summary>Reads <paramref name="count"/> bits (0 to 16) as an unsigned number.</summary>
    public int ReadBits(int count)
    {
        if (count == 0)
            return 0;

        EnsureBits(count);
        var value = PeekBits(count);
        _count -= count;

        return value;
    }

    public int ReadBit() => ReadBits(1);

    /// <summary>
    /// Reads <paramref name="count"/> bits and extends them to a signed value as T.81 F.2.2.1 says:
    /// codes below half the range stand for the negative values.
    /// </summary>
    public int ReceiveExtend(int count)
    {
        if (count == 0)
            return 0;

        var value = ReadBits(count);

        return value < 1 << (count - 1) ? value - (1 << count) + 1 : value;
    }

    /// <summary>
    /// Drops what is left of the current byte and finds the next marker from here: what a restart
    /// interval or the end of a scan needs. Returns the position of the marker's 0xFF, or the end of
    /// the data.
    /// </summary>
    public int AlignToMarker()
    {
        _buffer = 0;
        _count = 0;
        _atMarker = false;
        Position = JpegDecoder.FindMarker(data, Position);

        return Position;
    }

    /// <summary>Steps over the restart marker the reader stands in front of.</summary>
    public void SkipMarker() => Position = Math.Min(data.Length, Position + 2);

    private void FillByte()
    {
        if (_atMarker || Position >= data.Length)
        {
            _buffer <<= 8;
            _count += 8;
            return;
        }

        var value = data[Position];

        if (value == 0xFF)
        {
            var next = Position + 1 < data.Length ? data[Position + 1] : 0xD9;

            if (next != 0)
            {
                _atMarker = true;
                _buffer <<= 8;
                _count += 8;
                return;
            }

            Position += 2;
        }
        else
        {
            Position++;
        }

        _buffer = (_buffer << 8) | value;
        _count += 8;
    }
}
