namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// How strongly the loop filter treats the edges of one kind of macroblock (a segment, with or
/// without 4x4 prediction). A <see cref="Limit"/> of 0 turns the filter off.
/// </summary>
internal sealed class Vp8FilterStrength
{
    public int Limit { get; init; }

    public int InnerLevel { get; init; }

    public int HighEdgeVarianceThreshold { get; init; }
}
