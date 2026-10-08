namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>The dequantisation factors of one segment: DC and AC for luma, the second-order luma block and chroma.</summary>
internal sealed class Vp8QuantMatrix
{
    public int Y1Dc { get; init; }

    public int Y1Ac { get; init; }

    public int Y2Dc { get; init; }

    public int Y2Ac { get; init; }

    public int UvDc { get; init; }

    public int UvAc { get; init; }
}
