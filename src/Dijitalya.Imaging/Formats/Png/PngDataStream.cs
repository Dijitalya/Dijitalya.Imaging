namespace Dijitalya.Imaging.Formats.Png;

// Presents IDAT payloads as one stream without copying the compressed image. Chunk boundaries
// and checksums have already been validated by PngDecoder. The caller owns the input array.
internal sealed class PngDataStream(byte[] data, IReadOnlyList<(int Offset, int Length)> segments) : Stream
{
    private int _segment;
    private int _offset;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        DecodeContext.Checkpoint();
        var written = 0;
        while (!buffer.IsEmpty && _segment < segments.Count)
        {
            var segment = segments[_segment];
            var count = Math.Min(buffer.Length, segment.Length - _offset);
            data.AsSpan(segment.Offset + _offset, count).CopyTo(buffer);
            written += count;
            buffer = buffer[count..];
            _offset += count;
            if (_offset == segment.Length)
            {
                _segment++;
                _offset = 0;
            }
        }
        return written;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
