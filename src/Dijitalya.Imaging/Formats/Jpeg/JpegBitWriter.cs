namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>Writes entropy-coded JPEG data most significant bit first, stuffing a 0x00 after every 0xFF.</summary>
internal sealed class JpegBitWriter(Stream output)
{
    private uint _buffer;
    private int _count;

    /// <summary>Writes the low <paramref name="count"/> bits (at most 24) of <paramref name="bits"/>.</summary>
    public void Write(int bits, int count)
    {
        if (count == 0)
            return;

        _buffer = (_buffer << count) | ((uint)bits & ((1u << count) - 1));
        _count += count;

        while (_count >= 8)
        {
            var value = (byte)(_buffer >> (_count - 8));
            output.WriteByte(value);

            if (value == 0xFF)
                output.WriteByte(0x00);

            _count -= 8;
        }
    }

    /// <summary>Fills the last byte with one bits, as T.81 asks before a marker.</summary>
    public void Flush()
    {
        if (_count > 0)
            Write((1 << (8 - _count)) - 1, 8 - _count);
    }
}
