namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>The five prefix codes VP8L uses together: green with lengths and cache indexes, red, blue, alpha and distance.</summary>
internal sealed class Vp8LHuffmanGroup(Vp8LHuffmanCode green, Vp8LHuffmanCode red, Vp8LHuffmanCode blue, Vp8LHuffmanCode alpha, Vp8LHuffmanCode distance)
{
    public Vp8LHuffmanCode Green { get; } = green;

    public Vp8LHuffmanCode Red { get; } = red;

    public Vp8LHuffmanCode Blue { get; } = blue;

    public Vp8LHuffmanCode Alpha { get; } = alpha;

    public Vp8LHuffmanCode Distance { get; } = distance;
}
