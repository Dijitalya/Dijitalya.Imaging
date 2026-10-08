using System.Buffers.Binary;

namespace Dijitalya.Imaging.Metadata;

/// <summary>
/// Finds the orientation tag (0x0112) in EXIF data. Only the first image file directory is read:
/// that is where the tag of the main picture lives. Anything malformed reads as 1 (upright), because
/// a picture must not be refused over its metadata.
/// </summary>
internal static class ExifOrientationReader
{
    private const ushort OrientationTag = 0x0112;
    private const ushort ShortType = 3;

    /// <summary>The orientation (1 to 8) in TIFF-structured EXIF data, with or without the "Exif\0\0" prefix.</summary>
    public static int Read(ReadOnlySpan<byte> exif)
    {
        if (exif.Length >= 6 && exif[0] == (byte)'E' && exif[1] == (byte)'x' && exif[2] == (byte)'i' && exif[3] == (byte)'f'
            && exif[4] == 0 && exif[5] == 0)
        {
            exif = exif[6..];
        }

        if (exif.Length < 8)
            return 1;

        bool littleEndian;

        if (exif[0] == (byte)'I' && exif[1] == (byte)'I')
            littleEndian = true;
        else if (exif[0] == (byte)'M' && exif[1] == (byte)'M')
            littleEndian = false;
        else
            return 1;

        if (ReadUInt16(exif, 2, littleEndian) != 42)
            return 1;

        var directoryOffset = ReadUInt32(exif, 4, littleEndian);

        if (directoryOffset < 8 || directoryOffset > (uint)exif.Length - 2)
            return 1;

        var offset = (int)directoryOffset;
        var count = ReadUInt16(exif, offset, littleEndian);
        offset += 2;

        for (var entry = 0; entry < count; entry++)
        {
            var entryOffset = offset + (entry * 12);

            if (entryOffset + 12 > exif.Length)
                return 1;

            if (ReadUInt16(exif, entryOffset, littleEndian) != OrientationTag)
                continue;

            if (ReadUInt16(exif, entryOffset + 2, littleEndian) != ShortType)
                return 1;

            var value = ReadUInt16(exif, entryOffset + 8, littleEndian);

            return value is >= 1 and <= 8 ? value : 1;
        }

        return 1;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
}
