namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// Codes the quantised coefficients of VP8 blocks as tokens (RFC 6386 section 13). The same walk
/// either counts how often each probability is used with each outcome, or writes the bits; between
/// the two the encoder picks, for each of the 1056 token probabilities, the default or a better one
/// learnt from the counts when that pays for its own signalling, as libwebp does.
/// </summary>
internal sealed class Vp8TokenCoder
{
    private const int ProbabilityCount = 11;

    // Per token probability: how often it was used, and how often the coded bit was 1.
    private readonly long[] _totals = new long[Vp8Tables.CoefficientProbabilities.Length];
    private readonly long[] _ones = new long[Vp8Tables.CoefficientProbabilities.Length];
    private readonly byte[] _probabilities = (byte[])Vp8Tables.CoefficientProbabilities.Clone();

    // Block types, as the token probabilities are organised.
    public const int TypeLumaAc = 0;
    public const int TypeLumaDc = 1;
    public const int TypeChroma = 2;
    public const int TypeLuma4x4 = 3;

    /// <summary>The probabilities to code with: the defaults until <see cref="FinalizeProbabilities"/> runs.</summary>
    public byte[] Probabilities => _probabilities;

    /// <summary>Adds the counts of another coder (blocks counted aside) to this one.</summary>
    public void Add(Vp8TokenCoder other)
    {
        for (var i = 0; i < _totals.Length; i++)
        {
            _totals[i] += other._totals[i];
            _ones[i] += other._ones[i];
        }
    }

    /// <summary>Picks the probability for each token from the counts, and writes the choice into the header.</summary>
    public void FinalizeProbabilities()
    {
        var defaults = Vp8Tables.CoefficientProbabilities;
        var updates = Vp8Tables.CoefficientUpdateProbabilities;

        for (var i = 0; i < _probabilities.Length; i++)
        {
            var total = _totals[i];
            var ones = _ones[i];
            var oldProbability = defaults[i];
            var newProbability = ones == 0 ? 255 : (int)(255 - (ones * 255 / total));

            if (newProbability < 1)
                newProbability = 1;

            var oldCost = BranchCost(ones, total, oldProbability) + BitCost(0, updates[i]);
            var newCost = BranchCost(ones, total, newProbability) + BitCost(1, updates[i]) + (8 * 256);

            _probabilities[i] = oldCost > newCost ? (byte)newProbability : oldProbability;
        }
    }

    /// <summary>Writes the token probabilities that differ from the defaults into the frame header.</summary>
    public void WriteProbabilities(Vp8BoolEncoder header)
    {
        var defaults = Vp8Tables.CoefficientProbabilities;
        var updates = Vp8Tables.CoefficientUpdateProbabilities;

        for (var i = 0; i < _probabilities.Length; i++)
        {
            var update = _probabilities[i] != defaults[i] ? 1 : 0;
            header.PutBit(update, updates[i]);

            if (update == 1)
                header.PutBits(_probabilities[i], 8);
        }
    }

    /// <summary>
    /// Counts (when <paramref name="writer"/> is null) or writes the tokens of one block whose levels
    /// are in zigzag order. Coding starts at <paramref name="first"/>; <paramref name="last"/> is the
    /// index of the last non-zero level or -1. Returns whether the block has a non-zero level.
    /// </summary>
    public bool CodeBlock(Vp8BoolEncoder? writer, int type, int context, int first, int last, ReadOnlySpan<short> levels)
    {
        var n = first;
        var p = Offset(type, Vp8Tables.Bands[n], context);

        if (!Put(writer, last >= n ? 1 : 0, p))
            return false;

        while (n < 16)
        {
            int level = levels[n++];
            var negative = level < 0;
            var value = negative ? -level : level;

            if (!Put(writer, value != 0 ? 1 : 0, p + 1))
            {
                p = Offset(type, Vp8Tables.Bands[n], 0);
                continue;
            }

            if (!Put(writer, value > 1 ? 1 : 0, p + 2))
            {
                p = Offset(type, Vp8Tables.Bands[n], 1);
            }
            else
            {
                PutLargeValue(writer, value, p);
                p = Offset(type, Vp8Tables.Bands[n], 2);
            }

            writer?.PutBitUniform(negative ? 1 : 0);

            if (n == 16 || !Put(writer, n <= last ? 1 : 0, p))
                return true;
        }

        return true;
    }

    private void PutLargeValue(Vp8BoolEncoder? writer, int value, int p)
    {
        if (!Put(writer, value > 4 ? 1 : 0, p + 3))
        {
            if (Put(writer, value != 2 ? 1 : 0, p + 4))
                Put(writer, value == 4 ? 1 : 0, p + 5);

            return;
        }

        if (!Put(writer, value > 10 ? 1 : 0, p + 6))
        {
            if (!Put(writer, value > 6 ? 1 : 0, p + 7))
            {
                writer?.PutBit(value == 6 ? 1 : 0, 159);
            }
            else
            {
                writer?.PutBit(value >= 9 ? 1 : 0, 165);
                writer?.PutBit((value & 1) == 0 ? 1 : 0, 145);
            }

            return;
        }

        int category;

        if (value < 3 + (8 << 1))
        {
            Put(writer, 0, p + 8);
            Put(writer, 0, p + 9);
            category = 0;
        }
        else if (value < 3 + (8 << 2))
        {
            Put(writer, 0, p + 8);
            Put(writer, 1, p + 9);
            category = 1;
        }
        else if (value < 3 + (8 << 3))
        {
            Put(writer, 1, p + 8);
            Put(writer, 0, p + 10);
            category = 2;
        }
        else
        {
            Put(writer, 1, p + 8);
            Put(writer, 1, p + 10);
            category = 3;
        }

        if (writer is null)
            return;

        var extra = value - (3 + (8 << category));
        var probabilities = Vp8Tables.CategoryProbabilities[category];

        for (var bit = probabilities.Length - 1; bit >= 0; bit--)
            writer.PutBit((extra >> bit) & 1, probabilities[probabilities.Length - 1 - bit]);
    }

    private bool Put(Vp8BoolEncoder? writer, int bit, int index)
    {
        if (writer is null)
        {
            _totals[index]++;
            _ones[index] += bit;
        }
        else
        {
            writer.PutBit(bit, _probabilities[index]);
        }

        return bit != 0;
    }

    private static int Offset(int type, int band, int context) => (((type * 8) + band) * 3 + context) * ProbabilityCount;

    /// <summary>The cost in 1/256 bit of coding <paramref name="bit"/> with a probability of 0 of <paramref name="probability"/>/256.</summary>
    public static int BitCost(int bit, int probability) =>
        bit == 0 ? Vp8EncoderTables.EntropyCost[probability] : Vp8EncoderTables.EntropyCost[255 - probability];

    private static long BranchCost(long ones, long total, int probability) =>
        (ones * BitCost(1, probability)) + ((total - ones) * BitCost(0, probability));
}
