namespace Dijitalya.Imaging.Formats.Jpeg;

/// <summary>A Huffman table for encoding: the code and its length for each symbol, built canonically from a DHT definition.</summary>
internal sealed class JpegHuffmanEncoderTable
{
    public JpegHuffmanEncoderTable(byte[] counts, byte[] symbols)
    {
        Counts = counts;
        Symbols = symbols;

        var code = 0;
        var index = 0;

        for (var length = 1; length <= 16; length++)
        {
            for (var item = 0; item < counts[length - 1]; item++)
            {
                Codes[symbols[index]] = code;
                Lengths[symbols[index]] = length;
                code++;
                index++;
            }

            code <<= 1;
        }
    }

    public byte[] Counts { get; }

    public byte[] Symbols { get; }

    public int[] Codes { get; } = new int[256];

    public int[] Lengths { get; } = new int[256];

    public void Write(JpegBitWriter writer, int symbol) => writer.Write(Codes[symbol], Lengths[symbol]);
}
