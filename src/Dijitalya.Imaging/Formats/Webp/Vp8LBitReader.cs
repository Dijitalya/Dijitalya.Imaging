namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Reads a VP8L (lossless WebP) bitstream least significant bit first. Reading past the end yields
/// zero bits and sets <see cref="IsEndOfStream"/>, which the decoder treats as a truncated file.
/// </summary>
internal sealed class Vp8LBitReader(byte[] data, int start, int length)
{
    private readonly int _end = start + length;
    private readonly long _totalBits = (long)length * 8;
    private ulong _value;
    private int _count;
    private int _position = start;
    private long _consumed;

    public bool IsEndOfStream => _consumed > _totalBits;

    /// <summary>The number of bits not read yet.</summary>
    public long RemainingBits => Math.Max(0, _totalBits - _consumed);

    /// <summary>Reads <paramref name="count"/> bits (0 to 32).</summary>
    public int ReadBits(int count)
    {
        if (count == 0)
            return 0;

        var value = PeekBits(count);
        SkipBits(count);

        return value;
    }

    /// <summary>The next <paramref name="count"/> bits (at most 32), without taking them.</summary>
    public int PeekBits(int count)
    {
        if (_count < count)
            Fill();

        return (int)(_value & ((1UL << count) - 1));
    }

    public void SkipBits(int count)
    {
        _value >>= count;
        _count -= count;
        _consumed += count;
    }

    private void Fill()
    {
        while (_count <= 56)
        {
            if (_position < _end)
                _value |= (ulong)data[_position++] << _count;

            _count += 8;
        }
    }
}
