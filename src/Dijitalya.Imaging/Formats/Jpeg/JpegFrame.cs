namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>The frame of a JPEG (SOF0, SOF1 or SOF2): the size of the picture and its components.</summary>
internal sealed class JpegFrame
{
    public JpegFrame(int width, int height, bool progressive, IReadOnlyList<JpegComponent> components)
    {
        Width = width;
        Height = height;
        Progressive = progressive;
        Components = components;
        MaxHorizontalFactor = components.Max(component => component.HorizontalFactor);
        MaxVerticalFactor = components.Max(component => component.VerticalFactor);
        McusPerLine = DivideRoundingUp(width, 8 * MaxHorizontalFactor);
        McusPerColumn = DivideRoundingUp(height, 8 * MaxVerticalFactor);

        foreach (var component in components)
        {
            component.SampleWidth = DivideRoundingUp(width * component.HorizontalFactor, MaxHorizontalFactor);
            component.SampleHeight = DivideRoundingUp(height * component.VerticalFactor, MaxVerticalFactor);
            component.BlocksPerLine = DivideRoundingUp(component.SampleWidth, 8);
            component.BlocksPerColumn = DivideRoundingUp(component.SampleHeight, 8);
            component.BlocksPerLineForMcu = McusPerLine * component.HorizontalFactor;
            component.BlocksPerColumnForMcu = McusPerColumn * component.VerticalFactor;
            component.Coefficients = DecodeContext.Allocate<short>(checked((long)component.BlocksPerLineForMcu * component.BlocksPerColumnForMcu * 64));
        }
    }

    public int Width { get; }

    public int Height { get; }

    public bool Progressive { get; }

    public IReadOnlyList<JpegComponent> Components { get; }

    public int MaxHorizontalFactor { get; }

    public int MaxVerticalFactor { get; }

    public int McusPerLine { get; }

    public int McusPerColumn { get; }

    private static int DivideRoundingUp(int value, int divisor) => (value + divisor - 1) / divisor;
}
