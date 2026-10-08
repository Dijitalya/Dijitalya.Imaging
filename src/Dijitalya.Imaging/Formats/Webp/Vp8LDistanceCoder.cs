namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Maps VP8L backward-reference distances to and from distance codes. Codes 1 to 120 name the
/// nearest pixels in two dimensions (a few rows up, a few columns left or right), so the distance
/// they stand for depends on the image width; larger codes are the distance plus 120.
/// </summary>
internal sealed class Vp8LDistanceCoder
{
    private const int PlaneCodes = 120;

    // (y << 4) | (8 - x) of the pixel each short code names, nearest first.
    private static readonly byte[] CodeToPlane =
    [
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a,
        0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a, 0x25, 0x2b, 0x48, 0x04,
        0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45,
        0x4b, 0x34, 0x3c, 0x03, 0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d,
        0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
        0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e,
        0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f, 0x64, 0x6c, 0x42, 0x4e,
        0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e,
        0x00, 0x74, 0x7c, 0x41, 0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d,
        0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    ];

    private readonly Dictionary<int, int> _shortCodes = [];

    /// <summary>Prepares the short codes for images of the given width.</summary>
    public Vp8LDistanceCoder(int width)
    {
        // Lower codes are cheaper, so the first code naming a distance wins.
        for (var code = 1; code <= PlaneCodes; code++)
            _shortCodes.TryAdd(ToDistance(width, code), code);
    }

    /// <summary>The distance code for a distance.</summary>
    public int Code(int distance) => _shortCodes.TryGetValue(distance, out var code) ? code : distance + PlaneCodes;

    /// <summary>The distance a code stands for in an image of the given width.</summary>
    public static int ToDistance(int width, int code)
    {
        if (code > PlaneCodes)
            return code - PlaneCodes;

        var plane = CodeToPlane[code - 1];
        var distance = ((plane >> 4) * width) + 8 - (plane & 0xF);

        // Tiny widths make some codes point before the pixel's own row start; the format clamps them.
        return distance >= 1 ? distance : 1;
    }
}
