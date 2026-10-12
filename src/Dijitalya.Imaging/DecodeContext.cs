using System.Runtime.CompilerServices;

namespace Dijitalya.Imaging;

// Decode is synchronous after input buffering. A thread-local scope lets internal codec helpers
// share one budget without public global state. Nested scopes restore their predecessor, and no
// scope is ever held across an await or dispatched to another thread.
internal sealed class DecodeContext : IDisposable
{
    [ThreadStatic]
    private static DecodeContext? _current;
    private readonly DecodeContext? _previous;
    private readonly CancellationToken _cancellationToken;
    private long _remaining;

    private DecodeContext(ImageDecodeOptions options, CancellationToken cancellationToken, long inputBytes)
    {
        Options = options;
        _cancellationToken = cancellationToken;
        _remaining = options.MaxAllocationBytes;
        _previous = _current;
        cancellationToken.ThrowIfCancellationRequested();
        if (inputBytes > _remaining)
            throw new InvalidImageContentException("The input exceeds the allocation budget.");
        _remaining -= inputBytes;
        _current = this;
    }

    internal ImageDecodeOptions Options { get; }
    internal static bool Strict => _current?.Options.Strict ?? false;
    internal static int MaxMetadataBytes => _current?.Options.MaxMetadataBytes ?? 16 * 1024 * 1024;
    internal static int MaxJpegScans => _current?.Options.MaxJpegScans ?? 1024;

    internal static DecodeContext Begin(ImageDecodeOptions options, CancellationToken token, long inputBytes) =>
        new(options, token, inputBytes);

    internal static void Checkpoint() => _current?._cancellationToken.ThrowIfCancellationRequested();

    internal static void EnsureSize(long width, long height)
    {
        Checkpoint();
        var options = _current?.Options;
        if (options is not null && (width > options.MaxDimension || height > options.MaxDimension ||
            width * height > options.MaxPixelCount))
            throw new InvalidImageContentException("The image exceeds the configured dimension or pixel limit.");
    }

    internal static void Charge(long bytes)
    {
        Checkpoint();
        var context = _current;
        if (bytes < 0 || (context is not null && bytes > context._remaining))
            throw new InvalidImageContentException("The image exceeds the decoder allocation budget.");
        if (context is not null)
            context._remaining -= bytes;
    }

    internal static T[] Allocate<T>(long count)
    {
        if (count < 0 || count > Array.MaxLength)
            throw new InvalidImageContentException("The requested decoder buffer is too large.");
        Charge(checked(count * Unsafe.SizeOf<T>()));
        return new T[(int)count];
    }

    internal static byte[] Copy(ReadOnlySpan<byte> source)
    {
        var result = Allocate<byte>(source.Length);
        source.CopyTo(result);
        return result;
    }

    public void Dispose() => _current = _previous;
}
