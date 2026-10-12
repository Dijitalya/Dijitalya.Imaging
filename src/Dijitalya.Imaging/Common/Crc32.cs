namespace Dijitalya.Imaging.Common;

/// <summary>The CRC-32 of PNG chunks (ISO 3309 / ITU-T V.42, polynomial 0xEDB88320).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    /// <summary>Continues a running CRC; start with 0xFFFFFFFF and invert the end result.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            DecodeContext.Checkpoint();
            var block = data[..Math.Min(data.Length, 65536)];
            foreach (var value in block)
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            data = data[block.Length..];
        }

        return crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            var value = index;

            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;

            table[index] = value;
        }

        return table;
    }
}
