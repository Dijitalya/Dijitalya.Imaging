using System.Buffers.Binary;
using Dijitalya.Imaging.Metadata;

namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Reads the RIFF container of WebP: the simple forms (one VP8 or VP8L chunk) and the extended form
/// (VP8X) with its ALPH, ANIM/ANMF, ICCP and EXIF chunks. Of an animation the first frame is decoded
/// and placed on a transparent canvas, as libwebp's animation decoder shows it. The EXIF orientation
/// and an RGB colour profile are reported with the picture. Every chunk size is checked against the
/// data before it is used.
/// </summary>
internal static class WebpDecoder
{
    private const int ChunkHeaderSize = 8;
    private const int Vp8XChunkSize = 10;
    private const int AnimationFlag = 0x02;
    private const int AnmfHeaderSize = 16;

    public static DecodedImage Decode(byte[] data)
    {
        if (data.Length < 12 || !IsTag(data, 0, "RIFF") || !IsTag(data, 8, "WEBP"))
            throw new InvalidImageContentException("The file is not a WebP.");

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));

        if (riffSize < 4 + ChunkHeaderSize)
            throw new InvalidImageContentException("The WebP RIFF size is invalid.");

        if (riffSize > data.Length - 8L)
            throw new InvalidImageContentException("The WebP file is cut off.");

        // Bytes after the RIFF payload do not belong to the picture.
        var end = 8 + (int)riffSize;
        var position = 12;
        var orientation = FindOrientation(data, position, end);
        var iccProfile = FindIccProfile(data, position, end);

        if (!TryReadChunk(data, position, end, out var tag, out var payload, out var payloadLength, out _))
            throw new InvalidImageContentException("The WebP file has no image data.");

        if (tag != "VP8X")
            return new DecodedImage(DecodeFrame(data, position, end, null), ImageFormat.Webp, orientation);

        if (payloadLength != Vp8XChunkSize)
            throw new InvalidImageContentException("The WebP extended header is invalid.");

        var flags = data[payload];
        var canvasWidth = ReadUInt24(data, payload + 4) + 1;
        var canvasHeight = ReadUInt24(data, payload + 7) + 1;
        ImageLimits.EnsureSize(canvasWidth, canvasHeight);

        position = NextChunk(payload, payloadLength);

        if ((flags & AnimationFlag) != 0)
            return new DecodedImage(DecodeFirstAnimationFrame(data, position, end, canvasWidth, canvasHeight), ImageFormat.Webp, orientation, iccProfile);

        var image = DecodeFrame(data, position, end, null);

        if (image.Width != canvasWidth || image.Height != canvasHeight)
            throw new InvalidImageContentException("The WebP image does not match its canvas.");

        return new DecodedImage(image, ImageFormat.Webp, orientation, iccProfile);
    }

    // Decodes the first VP8 or VP8L chunk from 'position' on, with the last ALPH chunk before it.
    private static RasterImage DecodeFrame(byte[] data, int position, int end, (int Width, int Height)? expectedSize)
    {
        var alphaStart = -1;
        var alphaLength = 0;

        while (TryReadChunk(data, position, end, out var tag, out var payload, out var payloadLength, out var next))
        {
            switch (tag)
            {
                case "ALPH":
                    alphaStart = payload;
                    alphaLength = payloadLength;
                    break;

                case "VP8L":
                {
                    CheckFrameSize(Vp8LDecoder.ReadSize(data, payload, payloadLength), expectedSize);

                    return Vp8LDecoder.Decode(data, payload, payloadLength);
                }

                case "VP8 ":
                {
                    CheckFrameSize(Vp8Decoder.ReadSize(data, payload, payloadLength), expectedSize);
                    var image = Vp8Decoder.Decode(data, payload, payloadLength);

                    if (alphaStart >= 0)
                        WebpAlphaDecoder.Apply(data, alphaStart, alphaLength, image);

                    return image;
                }
            }

            position = next;
        }

        throw new InvalidImageContentException("The WebP file has no image data.");
    }

    private static void CheckFrameSize((int Width, int Height) size, (int Width, int Height)? expectedSize)
    {
        ImageLimits.EnsureSize(size.Width, size.Height);

        if (expectedSize is { } expected && (size.Width != expected.Width || size.Height != expected.Height))
            throw new InvalidImageContentException("A WebP frame does not match its declared size.");
    }

    private static RasterImage DecodeFirstAnimationFrame(byte[] data, int position, int end, int canvasWidth, int canvasHeight)
    {
        while (TryReadChunk(data, position, end, out var tag, out var payload, out var payloadLength, out var next))
        {
            if (tag == "ANMF")
            {
                if (payloadLength < AnmfHeaderSize)
                    throw new InvalidImageContentException("The WebP animation frame header is cut off.");

                var x = ReadUInt24(data, payload) * 2;
                var y = ReadUInt24(data, payload + 3) * 2;
                var width = ReadUInt24(data, payload + 6) + 1;
                var height = ReadUInt24(data, payload + 9) + 1;

                if ((long)x + width > canvasWidth || (long)y + height > canvasHeight)
                    throw new InvalidImageContentException("A WebP animation frame lies outside the canvas.");

                var frame = DecodeFrame(data, payload + AnmfHeaderSize, payload + payloadLength, (width, height));

                // The first frame is drawn on a fully transparent canvas; there is nothing below it to blend with.
                var canvas = new RasterImage(canvasWidth, canvasHeight);

                for (var row = 0; row < height; row++)
                    Array.Copy(frame.Pixels, row * width * 4, canvas.Pixels, (((y + row) * canvasWidth) + x) * 4, width * 4);

                return canvas;
            }

            position = next;
        }

        throw new InvalidImageContentException("The WebP animation has no frames.");
    }

    private static int FindOrientation(byte[] data, int position, int end)
    {
        while (TryReadChunk(data, position, end, out var tag, out var payload, out var payloadLength, out var next))
        {
            if (tag == "EXIF")
                return ExifOrientationReader.Read(data.AsSpan(payload, payloadLength));

            position = next;
        }

        return 1;
    }

    // Only the extended container can carry a profile; the simple one has no room for it.
    private static byte[]? FindIccProfile(byte[] data, int position, int end)
    {
        while (TryReadChunk(data, position, end, out var tag, out var payload, out var payloadLength, out var next))
        {
            if (tag == "ICCP")
            {
                if (payloadLength > DecodeContext.MaxMetadataBytes)
                    throw new InvalidImageContentException("The WebP colour profile exceeds the metadata limit.");
                return IccProfiles.RgbOrNull(DecodeContext.Copy(data.AsSpan(payload, payloadLength)));
            }

            position = next;
        }

        return null;
    }

    // Reads the chunk header at 'position'. False at the end of the data, and for a chunk whose
    // payload does not fit in it, which ends the reading. Chunks are padded to an even size.
    private static bool TryReadChunk(byte[] data, int position, int end, out string tag, out int payload, out int payloadLength, out int next)
    {
        tag = string.Empty;
        payload = 0;
        payloadLength = 0;
        next = 0;

        DecodeContext.Checkpoint();
        if (end - position < ChunkHeaderSize)
        {
            if (DecodeContext.Strict && position != end)
                throw new InvalidImageContentException("The WebP chunk header is truncated.");
            return false;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));

        if ((long)size + (size & 1) > end - position - ChunkHeaderSize)
        {
            if (DecodeContext.Strict)
                throw new InvalidImageContentException("The WebP chunk payload is truncated.");
            return false;
        }

        tag = System.Text.Encoding.ASCII.GetString(data, position, 4);
        payload = position + ChunkHeaderSize;
        payloadLength = (int)size;
        next = NextChunk(payload, payloadLength);

        return true;
    }

    private static int NextChunk(int payload, int payloadLength) => payload + payloadLength + (payloadLength & 1);

    private static bool IsTag(byte[] data, int offset, string tag) =>
        data[offset] == tag[0] && data[offset + 1] == tag[1] && data[offset + 2] == tag[2] && data[offset + 3] == tag[3];

    private static int ReadUInt24(byte[] data, int offset) => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
}
