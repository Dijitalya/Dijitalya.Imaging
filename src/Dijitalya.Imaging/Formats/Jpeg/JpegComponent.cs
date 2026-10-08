namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>
/// One colour component of a JPEG frame (Y, Cb, Cr, ...) with its sampling factors and the
/// coefficients of its blocks, collected over all the scans before they are transformed.
/// </summary>
internal sealed class JpegComponent(int id, int horizontalFactor, int verticalFactor, int quantizationTable)
{
    public int Id { get; } = id;

    public int HorizontalFactor { get; } = horizontalFactor;

    public int VerticalFactor { get; } = verticalFactor;

    public int QuantizationTable { get; } = quantizationTable;

    /// <summary>The blocks that hold the component's samples, across and down.</summary>
    public int BlocksPerLine { get; set; }

    public int BlocksPerColumn { get; set; }

    /// <summary>The blocks across and down once the picture is padded to whole MCUs; the coefficients are laid out this wide.</summary>
    public int BlocksPerLineForMcu { get; set; }

    public int BlocksPerColumnForMcu { get; set; }

    /// <summary>64 coefficients per block in natural order, blocks row by row, <see cref="BlocksPerLineForMcu"/> wide.</summary>
    public short[] Coefficients { get; set; } = [];

    /// <summary>The width and height of the component's samples (before upsampling).</summary>
    public int SampleWidth { get; set; }

    public int SampleHeight { get; set; }

    public int DcPredictor { get; set; }

    public JpegHuffmanTable? DcTable { get; set; }

    public JpegHuffmanTable? AcTable { get; set; }

    public int BlockOffset(int blockRow, int blockColumn) => ((blockRow * BlocksPerLineForMcu) + blockColumn) * 64;
}
