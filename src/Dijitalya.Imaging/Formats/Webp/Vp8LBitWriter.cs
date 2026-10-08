namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>Writes a VP8L bitstream: values of up to 32 bits, least significant bit first.</summary>
internal sealed class Vp8LBitWriter
{
    private byte[] _buffer = new byte[1024];
    private int _position;
    private ulong _bits;
    private int _count;

    public void WriteBits(uint value, int count)
    {
        if (count == 0)
            return;

        _bits |= (ulong)(value & (uint)((1UL << count) - 1)) << _count;
        _count += count;

        while (_count >= 8)
        {
            if (_position == _buffer.Length)
                Array.Resize(ref _buffer, _buffer.Length * 2);

            _buffer[_position++] = (byte)_bits;
            _bits >>= 8;
            _count -= 8;
        }
    }

    /// <summary>The bytes written, the last one padded with zero bits.</summary>
    public byte[] ToArray()
    {
        var length = _position + (_count > 0 ? 1 : 0);
        var output = new byte[length];
        Array.Copy(_buffer, output, _position);

        if (_count > 0)
            output[_position] = (byte)_bits;

        return output;
    }
}
