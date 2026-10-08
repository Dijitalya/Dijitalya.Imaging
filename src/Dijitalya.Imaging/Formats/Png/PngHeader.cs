namespace Dijitalya.Imaging.Formats.Png;

/// <summary>What the IHDR chunk of a PNG says about its picture.</summary>
internal readonly record struct PngHeader(int Width, int Height, int BitDepth, int ColorType, int Channels, bool Interlaced);
