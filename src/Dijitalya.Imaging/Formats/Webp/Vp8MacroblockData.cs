namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>What the bitstream says about one macroblock: its segment, prediction modes and coefficients.</summary>
internal sealed class Vp8MacroblockData
{
    public int Segment { get; set; }

    public bool Skip { get; set; }

    public bool IsIntra4x4 { get; set; }

    /// <summary>The 16x16 luma mode in entry 0, or the sixteen 4x4 modes in raster order.</summary>
    public byte[] Modes { get; } = new byte[16];

    public int ChromaMode { get; set; }

    /// <summary>
    /// The dequantised coefficients of the 16 luma, 4 U and 4 V blocks, 16 each in raster order
    /// within the block.
    /// </summary>
    public short[] Coefficients { get; } = new short[384];

    /// <summary>Two bits per luma block, first block in the top bits: 0 none, 1 DC only, 2 a few, 3 many coefficients.</summary>
    public uint NonZeroY { get; set; }

    /// <summary>The same for the chroma blocks: U in the low byte, V in the next.</summary>
    public uint NonZeroUv { get; set; }
}
