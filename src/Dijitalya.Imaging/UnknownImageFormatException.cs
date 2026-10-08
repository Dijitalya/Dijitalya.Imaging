namespace Dijitalya.Imaging;

/// <summary>The content is not in any format the codecs know: it is not a picture they can read.</summary>
public sealed class UnknownImageFormatException : Exception
{
    /// <summary>A new exception saying that the content is not a picture in a supported format.</summary>
    public UnknownImageFormatException()
        : base("The content is not a picture in a supported format.")
    {
    }

    /// <summary>A new exception with the given message.</summary>
    /// <param name="message">What went wrong.</param>
    public UnknownImageFormatException(string message)
        : base(message)
    {
    }

    /// <summary>A new exception with the given message, caused by another exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public UnknownImageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
