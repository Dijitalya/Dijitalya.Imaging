using System.Buffers.Binary;
using System.IO.Compression;
using Dijitalya.Imaging.Common;
using Dijitalya.Imaging.Formats.Bmp;
using Dijitalya.Imaging.Formats.Gif;
using Dijitalya.Imaging.Formats.Jpeg;
using Dijitalya.Imaging.Formats.Png;
using Dijitalya.Imaging.Formats.Webp;
using Dijitalya.Imaging.Processing;

namespace Dijitalya.Imaging.Tests;

public sealed class HardeningTests
{
    [Theory]
    [InlineData(1, 8192, 8192, 1)]
    [InlineData(1, 32768, 32768, 1)]
    [InlineData(32768, 1, 1, 32768)]
    public void ExtremeAspectRatios_StayWithinASmallAllocationBudget(int width, int height, int targetWidth, int targetHeight)
    {
        var source = new RasterImage(width, height);
        Array.Fill(source.Pixels, (byte)255);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = ImageResizer.Resize(source, targetWidth, targetHeight);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal((targetWidth, targetHeight), (result.Width, result.Height));
        Assert.All(result.Pixels, value => Assert.Equal((byte)255, value));
        Assert.True(allocated < 64L * 1024 * 1024, $"Allocated {allocated} bytes for a thin image.");
    }

    [Theory]
    [InlineData(1, 32768)]
    [InlineData(32768, 1)]
    public void Crop_HandlesExtremeSourceRatios(int width, int height)
    {
        var source = new RasterImage(width, height);
        Array.Fill(source.Pixels, (byte)255);
        var result = ImageResizer.ResizeToBox(source, 300, 300, ResizeMode.Crop);
        Assert.Equal((300, 300), (result.Width, result.Height));
        Assert.All(result.Pixels, value => Assert.Equal((byte)255, value));
    }

    [Theory]
    [InlineData(37, 23, 12, 19)]
    [InlineData(23, 37, 19, 12)]
    [InlineData(11, 7, 31, 17)]
    public void DirectCrop_MatchesResizeThenCrop(int width, int height, int targetWidth, int targetHeight)
    {
        var source = SamplePictures.Noise(width, height, 123);
        var original = (byte[])source.Pixels.Clone();
        var ratio = Math.Max((double)targetWidth / width, (double)targetHeight / height);
        var scaledWidth = Math.Max(targetWidth, (int)Math.Round(width * ratio));
        var scaledHeight = Math.Max(targetHeight, (int)Math.Round(height * ratio));
        var scaled = ImageResizer.Resize(source, scaledWidth, scaledHeight);
        var expected = ImageTransforms.Crop(scaled, (scaledWidth - targetWidth) / 2, (scaledHeight - targetHeight) / 2, targetWidth, targetHeight);
        var actual = ImageResizer.ResizeToBox(source, targetWidth, targetHeight, ResizeMode.Crop);
        for (var index = 0; index < actual.Pixels.Length; index++)
            Assert.InRange(Math.Abs(actual.Pixels[index] - expected.Pixels[index]), 0, 1);
        Assert.Equal(original, source.Pixels);
    }

    [Fact]
    public void PublicArguments_RejectOverflowAndUnknownEnums()
    {
        var image = new RasterImage(2, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageTransforms.Crop(image, int.MaxValue, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageResizer.ResizeToBox(image, 1, 1, (ResizeMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageCodec.ExtensionOf((ImageFormat)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageCodec.ContentTypeOf((ImageFormat)99));
        Assert.Throws<InvalidImageContentException>(() => ImageResizer.ResizeToBox(image, int.MaxValue, 0, ResizeMode.Max));
    }

    [Fact]
    public void PngDecode_UsesRowsInsteadOfAFullInflatedCopy()
    {
        var source = SamplePictures.Noise(1024, 1024, 42);
        var data = ImageCodec.Encode(source, ImageFormat.Png);
        var options = new ImageDecodeOptions { MaxAllocationBytes = data.Length + source.Pixels.LongLength + 128 * 1024 };
        var decoded = ImageCodec.Decode(data, options);
        Assert.Equal(source.Pixels, decoded.Image.Pixels);
    }

    [Fact]
    public void DecodeLimits_AreEnforcedBeforeLargeBuffersAreAllocated()
    {
        var data = ImageCodec.Encode(SamplePictures.Photo(64, 64), ImageFormat.Png);
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(data, new ImageDecodeOptions { MaxPixelCount = 100 }));
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(data, new ImageDecodeOptions { MaxDimension = 32 }));
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(data, new ImageDecodeOptions { MaxFileSize = data.Length - 1 }));
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(data, new ImageDecodeOptions { MaxAllocationBytes = data.Length + 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageCodec.Decode(data, new ImageDecodeOptions { MaxAllocationBytes = 0 }));
        Assert.Equal(64, ImageCodec.Decode(data).Image.Width);
    }

    [Fact]
    public async Task ParallelDecodes_DoNotShareLimits()
    {
        var data = ImageCodec.Encode(SamplePictures.Photo(64, 64), ImageFormat.Png);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            if ((index & 1) == 0)
                Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(data, new ImageDecodeOptions { MaxPixelCount = 1 }));
            else
                Assert.Equal(64, ImageCodec.Decode(data).Image.Width);
        })));
    }

    [Fact]
    public void Cancellation_IsObservedInsideEveryDecoder()
    {
        var source = SamplePictures.Photo(32, 32);
        foreach (var format in Enum.GetValues<ImageFormat>())
        {
            var data = ImageCodec.Encode(source, format);
            using var cancellation = new CancellationTokenSource();
            using var context = DecodeContext.Begin(new ImageDecodeOptions(), cancellation.Token, data.Length);
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                _ = format switch
                {
                    ImageFormat.Png => PngDecoder.Decode(data),
                    ImageFormat.Jpeg => JpegDecoder.Decode(data),
                    ImageFormat.Gif => GifDecoder.Decode(data),
                    ImageFormat.Bmp => BmpDecoder.Decode(data),
                    _ => WebpDecoder.Decode(data),
                };
            });
        }
    }

    [Fact]
    public async Task CanceledOperations_PreserveTheCancellationException()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var image = new RasterImage(8, 8);
        var data = ImageCodec.Encode(image, ImageFormat.Png);
        Assert.ThrowsAny<OperationCanceledException>(() => ImageCodec.Decode(data, new ImageDecodeOptions(), cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ImageResizer.Resize(image, 4, 4, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageCodec.DecodeAsync(new MemoryStream(data), cancellation.Token));
    }

    [Fact]
    public async Task SeekableAndNonSeekableStreams_ProduceTheSamePixels_AndStayOpen()
    {
        var data = ImageCodec.Encode(SamplePictures.Photo(32, 24), ImageFormat.Png);
        using var seekable = new MemoryStream(data);
        using var nonSeekable = new ForwardOnlyStream(data);
        var first = await ImageCodec.DecodeAsync(seekable);
        var second = await ImageCodec.DecodeAsync(nonSeekable);
        Assert.Equal(first.Image.Pixels, second.Image.Pixels);
        Assert.True(seekable.CanRead);
        Assert.True(nonSeekable.CanRead);
        using var limited = new ForwardOnlyStream(data);
        await Assert.ThrowsAsync<InvalidImageContentException>(() => ImageCodec.DecodeAsync(limited,
            new ImageDecodeOptions { MaxFileSize = 40 }, CancellationToken.None));
    }

    [Fact]
    public void WebpIdentify_DoesNotCopyThePayload()
    {
        var encoded = ImageCodec.Encode(SamplePictures.Photo(16, 16), ImageFormat.Webp);
        var padded = new byte[8 * 1024 * 1024];
        encoded.CopyTo(padded, 0);
        _ = ImageCodec.Identify(encoded);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var size = ImageCodec.Identify(padded);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(size);
        Assert.Equal(16, size.Value.Width);
        Assert.True(allocated < 4096, $"Identify allocated {allocated} bytes.");
    }

    [Fact]
    public void StrictPng_RejectsMissingEndAndPartialRows_WhileRecoveryIsExplicit()
    {
        var original = ImageCodec.Encode(SamplePictures.Photo(8, 8), ImageFormat.Png);
        var truncated = original[..^12];
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(truncated));
        Assert.Equal(8, ImageCodec.Decode(truncated, new ImageDecodeOptions { Strict = false }).Image.Width);
        var partial = CreatePng(2, 1, [0, 255, 0, 0, 255]);
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(partial));
        Assert.Equal(2, ImageCodec.Decode(partial, new ImageDecodeOptions { Strict = false }).Image.Width);
    }

    [Fact]
    public void StrictPng_RejectsExcessRowsDuplicateHeadersAndBadAncillaryCrc()
    {
        var extra = CreatePng(1, 1, [0, 255, 0, 0, 255, 0]);
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(extra));
        var valid = CreatePng(1, 1, [0, 255, 0, 0, 255]);
        byte[] duplicate = [.. valid[..33], .. valid[8..33], .. valid[33..]];
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(duplicate));
        var ancillary = Chunk("tEXt"u8, "key\0value"u8);
        ancillary[^1] ^= 1;
        byte[] damaged = [.. valid[..33], .. ancillary, .. valid[33..]];
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(damaged));
        Assert.Equal(1, ImageCodec.Decode(damaged, new ImageDecodeOptions { Strict = false }).Image.Width);
    }

    [Fact]
    public void StrictJpeg_RejectsMissingEndMarker()
    {
        var data = ImageCodec.Encode(SamplePictures.Photo(32, 24), ImageFormat.Jpeg);
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(data[..^2]));
        Assert.Equal(32, ImageCodec.Decode(data[..^2], new ImageDecodeOptions { Strict = false }).Image.Width);
    }

    [Fact]
    public void StrictBmp_RejectsTruncatedPixels()
    {
        var data = ImageCodec.Encode(SamplePictures.Photo(32, 24), ImageFormat.Bmp);
        Assert.Throws<InvalidImageContentException>(() => ImageCodec.Decode(data[..^5]));
    }

    private static byte[] CreatePng(int width, int height, byte[] raw)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR"u8, header), .. Chunk("IDAT"u8, compressed.ToArray()), .. Chunk("IEND"u8, [])];
    }

    private static byte[] Chunk(ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        var chunk = new byte[body.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, body.Length);
        type.CopyTo(chunk.AsSpan(4));
        body.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), Crc32.Compute(chunk.AsSpan(4, body.Length + 4)));
        return chunk;
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 17));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 17)], cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
