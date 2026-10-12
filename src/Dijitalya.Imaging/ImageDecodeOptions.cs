namespace Dijitalya.Imaging;

/// <summary>Immutable limits and validation policy for one decode operation.</summary>
public sealed class ImageDecodeOptions
{
    /// <summary>Maximum input length, including metadata. Cannot exceed the library file limit.</summary>
    public long MaxFileSize { get; init; } = ImageLimits.MaxFileSize;

    /// <summary>Maximum width or height. Cannot exceed the library dimension limit.</summary>
    public int MaxDimension { get; init; } = ImageLimits.MaxDimension;

    /// <summary>Maximum number of output pixels. Cannot exceed the library pixel limit.</summary>
    public long MaxPixelCount { get; init; } = ImageLimits.MaxPixelCount;

    /// <summary>
    /// Cumulative budget for input and accounted decoder buffers, including retired buffers.
    /// This is deliberately conservative, not a measurement or a cap on process working set.
    /// Object headers, small collections and runtime/native decompressor overhead are not counted.
    /// </summary>
    public long MaxAllocationBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Maximum size of one decompressed or assembled ICC profile.</summary>
    public int MaxMetadataBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Maximum number of JPEG scans processed, including progressive scans.</summary>
    public int MaxJpegScans { get; init; } = 1024;

    /// <summary>
    /// Reject incomplete pixel data and invalid structural checks supported by each decoder.
    /// False permits best-effort recovery; it never disables resource limits. This is not a
    /// complete conformance validator for every optional feature in every format.
    /// </summary>
    public bool Strict { get; init; } = true;

    internal void Validate()
    {
        if (MaxFileSize <= 0 || MaxFileSize > ImageLimits.MaxFileSize)
            throw new ArgumentOutOfRangeException(nameof(MaxFileSize));
        if (MaxDimension <= 0 || MaxDimension > ImageLimits.MaxDimension)
            throw new ArgumentOutOfRangeException(nameof(MaxDimension));
        if (MaxPixelCount <= 0 || MaxPixelCount > ImageLimits.MaxPixelCount)
            throw new ArgumentOutOfRangeException(nameof(MaxPixelCount));
        if (MaxAllocationBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxAllocationBytes));
        if (MaxMetadataBytes <= 0 || MaxMetadataBytes > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxMetadataBytes));
        if (MaxJpegScans <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxJpegScans));
    }
}
