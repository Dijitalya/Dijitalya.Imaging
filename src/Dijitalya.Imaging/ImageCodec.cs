using System.Buffers.Binary;
using Dijitalya.Imaging.Formats.Bmp;
using Dijitalya.Imaging.Formats.Gif;
using Dijitalya.Imaging.Formats.Jpeg;
using Dijitalya.Imaging.Formats.Png;
using Dijitalya.Imaging.Formats.Webp;

namespace Dijitalya.Imaging;

/// <summary>
/// The entry point of the codecs: recognises a format by the content (never by a file name),
/// decodes a picture into a <see cref="RasterImage"/> and encodes one back into a file.
/// </summary>
public static class ImageCodec
{
    /// <summary>The highest JPEG and WebP quality; 100 keeps the most detail and makes the largest files.</summary>
    public const int MaxQuality = 100;

    // Enough bytes for every signature DetectFormat knows (BMP needs the most: 18).
    private const int SignatureLength = 32;

    /// <summary>The format the content is in, judged by its first bytes; null when it is none the codecs know.</summary>
    public static ImageFormat? DetectFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return ImageFormat.Jpeg;

        if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
            return ImageFormat.Png;

        if (data.Length >= 6 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8'
            && (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a')
        {
            return ImageFormat.Gif;
        }

        if (data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
        {
            return ImageFormat.Webp;
        }

        // "BM" alone is too weak a signature; the size of the header that follows must be one of the known ones.
        if (data.Length >= 18 && data[0] == (byte)'B' && data[1] == (byte)'M'
            && BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4)) is 12 or 16 or 40 or 52 or 56 or 64 or 108 or 124)
        {
            return ImageFormat.Bmp;
        }

        return null;
    }

    /// <summary>
    /// The format and size of a picture, read from its header without decoding it; null when the
    /// content is in no format the codecs know or its header is cut off or invalid. Cheap enough to
    /// measure an upload before deciding to decode it.
    /// </summary>
    public static (ImageFormat Format, int Width, int Height)? Identify(ReadOnlySpan<byte> data)
    {
        if (DetectFormat(data) is not { } format)
            return null;

        return ImageHeaderReader.Read(data, format) is { } size ? (format, size.Width, size.Height) : null;
    }

    /// <summary>Decodes a picture from its file content.</summary>
    /// <exception cref="UnknownImageFormatException">The content is not in a format the codecs know.</exception>
    /// <exception cref="InvalidImageContentException">The content is damaged, unsupported or too large.</exception>
    public static DecodedImage Decode(byte[] data) => Decode(data, new ImageDecodeOptions());

    /// <summary>Decodes with per-operation limits, validation and cooperative cancellation.</summary>
    public static DecodedImage Decode(byte[] data, ImageDecodeOptions options, CancellationToken cancellationToken = default) =>
        DecodeCore(data, options, cancellationToken, 0);

    private static DecodedImage DecodeCore(byte[] data, ImageDecodeOptions options, CancellationToken cancellationToken, long bufferingBytes)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (data.LongLength > options.MaxFileSize)
            throw new InvalidImageContentException("The input exceeds the configured file size limit.");
        using var context = DecodeContext.Begin(options, cancellationToken, checked(data.LongLength + bufferingBytes));

        var format = DetectFormat(data) ?? throw new UnknownImageFormatException();

        try
        {
            return format switch
            {
                ImageFormat.Jpeg => JpegDecoder.Decode(data),
                ImageFormat.Png => PngDecoder.Decode(data),
                ImageFormat.Gif => GifDecoder.Decode(data),
                ImageFormat.Bmp => BmpDecoder.Decode(data),
                _ => WebpDecoder.Decode(data),
            };
        }
        catch (InvalidImageContentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            // A damaged file can trip any of the decoders' internal checks (an index past the end of
            // a table, a stream that ends early); to the caller all of them mean the same thing.
            throw new InvalidImageContentException($"The {format} picture is damaged or not supported.", exception);
        }
    }

    /// <summary>
    /// Decodes the picture in a stream. The first bytes are checked before the rest is read, so a
    /// file that is no picture (a PDF or an archive handed in to be checked) is turned away without
    /// being loaded; a stream longer than <see cref="ImageLimits.MaxFileSize"/> is refused.
    /// </summary>
    /// <exception cref="UnknownImageFormatException">The content is not in a format the codecs know.</exception>
    /// <exception cref="InvalidImageContentException">The content is damaged, unsupported or too large.</exception>
    public static Task<DecodedImage> DecodeAsync(Stream stream, CancellationToken cancellationToken = default) =>
        DecodeAsync(stream, new ImageDecodeOptions(), cancellationToken);

    /// <summary>Reads and decodes a stream with per-operation limits and cancellation.</summary>
    public static async Task<DecodedImage> DecodeAsync(Stream stream, ImageDecodeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining < 0 || remaining > options.MaxFileSize || remaining > options.MaxAllocationBytes)
                throw new InvalidImageContentException("The input exceeds the configured file or allocation limit.");

            var header = new byte[SignatureLength];
            var count = await stream.ReadAtLeastAsync(header, (int)Math.Min(remaining, SignatureLength),
                throwOnEndOfStream: false, cancellationToken);
            if (DetectFormat(header.AsSpan(0, count)) is null)
                throw new UnknownImageFormatException();
            if (count > remaining)
                throw new InvalidImageContentException("The stream length changed while it was read.");

            // Allocate once for streams with a known length; avoid MemoryStream growth and ToArray.
            var data = new byte[(int)remaining];
            header.AsSpan(0, count).CopyTo(data);
            try
            {
                await stream.ReadExactlyAsync(data.AsMemory(count), cancellationToken);
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidImageContentException("The image stream is truncated.", exception);
            }
            var extra = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
            if (extra != 0)
                throw new InvalidImageContentException("The stream length changed while it was read.");
            return Decode(data, options, cancellationToken);
        }

        var signature = new byte[SignatureLength];
        var signatureLength = await stream.ReadAtLeastAsync(signature, signature.Length,
            throwOnEndOfStream: false, cancellationToken);
        if (DetectFormat(signature.AsSpan(0, signatureLength)) is null)
            throw new UnknownImageFormatException();
        if (signatureLength > options.MaxFileSize || signatureLength > options.MaxAllocationBytes / 3)
            throw new InvalidImageContentException("The input exceeds the configured buffering limit.");

        using var buffer = new MemoryStream();
        buffer.Write(signature, 0, signatureLength);
        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(0, 81920), cancellationToken)) > 0)
            {
                var length = buffer.Length + read;
                // Include geometric capacity growth and the final exact-length copy.
                var capacity = length > buffer.Capacity ? Math.Max(256, Math.Max(length, (long)buffer.Capacity * 2)) : buffer.Capacity;
                if (length > options.MaxFileSize || capacity + length > options.MaxAllocationBytes)
                    throw new InvalidImageContentException("The input exceeds the configured buffering limit.");
                buffer.Write(chunk, 0, read);
            }
            var data = buffer.ToArray();
            return DecodeCore(data, options, cancellationToken, buffer.Capacity);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
        }
    }

    /// <summary>
    /// Encodes a picture. <paramref name="quality"/> (1 to 100) applies to JPEG and to WebP, which is
    /// written lossy with its transparency kept losslessly. PNG and BMP keep every pixel; GIF keeps a
    /// picture of up to 256 colours exactly and reduces one of more. JPEG has no transparency:
    /// transparent pixels are laid on white. Only JPEG, PNG and WebP carry the ICC profile.
    /// </summary>
    /// <param name="image">The pixels.</param>
    /// <param name="format">Any of the formats.</param>
    /// <param name="quality">1 (smallest) to 100 (best), for JPEG and WebP.</param>
    /// <param name="iccProfile">
    /// The colour profile of the pixels, usually <see cref="DecodedImage.IccProfile"/> of the source;
    /// it is embedded so the colours stay as they were. Null for sRGB.
    /// </param>
    /// <exception cref="NotSupportedException">The codecs do not write <paramref name="format"/>.</exception>
    public static byte[] Encode(RasterImage image, ImageFormat format, int quality = 75, byte[]? iccProfile = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        return format switch
        {
            ImageFormat.Jpeg => JpegEncoder.Encode(image, Math.Clamp(quality, 1, MaxQuality), iccProfile),
            ImageFormat.Png => PngEncoder.Encode(image, iccProfile),
            ImageFormat.Webp => WebpEncoder.Encode(image, Math.Clamp(quality, 1, MaxQuality), iccProfile),
            ImageFormat.Gif => GifEncoder.Encode(image),
            ImageFormat.Bmp => BmpEncoder.Encode(image),
            _ => throw new NotSupportedException($"Writing {format} is not supported."),
        };
    }

    /// <summary>The file extension a format is written with, including the dot.</summary>
    public static string ExtensionOf(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Webp => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>The media type of a format.</summary>
    public static string ContentTypeOf(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Png => "image/png",
        ImageFormat.Gif => "image/gif",
        ImageFormat.Bmp => "image/bmp",
        ImageFormat.Webp => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
}
