namespace Dijitalya.Imaging;

/// <summary>
/// The content starts like a picture of a known format but cannot be decoded: it is damaged, uses a
/// variant of the format the codecs do not support, or describes a picture above <see cref="ImageLimits"/>.
/// </summary>
public sealed class InvalidImageContentException : Exception
{
    /// <summary>A new exception saying that the picture is damaged or not supported.</summary>
    public InvalidImageContentException()
        : base("The picture is damaged or not supported.")
    {
    }

    /// <summary>A new exception with the given message.</summary>
    /// <param name="message">What went wrong.</param>
    public InvalidImageContentException(string message)
        : base(message)
    {
    }

    /// <summary>A new exception with the given message, caused by another exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public InvalidImageContentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
