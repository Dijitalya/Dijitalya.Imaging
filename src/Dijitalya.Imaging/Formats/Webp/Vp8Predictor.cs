namespace Dijitalya.Imaging.Formats.Webp;

/// <summary>
/// The intra predictors of VP8 (RFC 6386 section 12) in libwebp's form. They work on a buffer whose
/// rows are <see cref="Stride"/> bytes apart and read the row above and the column to the left of the
/// block they fill, which the decoder keeps there.
/// </summary>
internal static class Vp8Predictor
{
    /// <summary>The distance between two rows of the work buffer.</summary>
    public const int Stride = 32;

    public const int DcMode = 0;
    public const int TrueMotionMode = 1;
    public const int VerticalMode = 2;
    public const int HorizontalMode = 3;

    // DC prediction without the row above, without the left column, or without either.
    public const int DcNoTopMode = 4;
    public const int DcNoLeftMode = 5;
    public const int DcNoTopLeftMode = 6;

    /// <summary>Fills a 16x16 luma block with one of the seven whole-block predictions.</summary>
    public static void PredictLuma16(int mode, byte[] buffer, int offset)
    {
        switch (mode)
        {
            case DcMode:
            {
                var dc = 16;

                for (var j = 0; j < 16; j++)
                    dc += buffer[offset - 1 + (j * Stride)] + buffer[offset + j - Stride];

                Fill(buffer, offset, 16, (byte)(dc >> 5));
                break;
            }

            case TrueMotionMode:
                TrueMotion(buffer, offset, 16);
                break;

            case VerticalMode:
                for (var j = 0; j < 16; j++)
                    Array.Copy(buffer, offset - Stride, buffer, offset + (j * Stride), 16);
                break;

            case HorizontalMode:
                for (var j = 0; j < 16; j++)
                {
                    var row = offset + (j * Stride);
                    Array.Fill(buffer, buffer[row - 1], row, 16);
                }

                break;

            case DcNoTopMode:
            {
                var dc = 8;

                for (var j = 0; j < 16; j++)
                    dc += buffer[offset - 1 + (j * Stride)];

                Fill(buffer, offset, 16, (byte)(dc >> 4));
                break;
            }

            case DcNoLeftMode:
            {
                var dc = 8;

                for (var i = 0; i < 16; i++)
                    dc += buffer[offset + i - Stride];

                Fill(buffer, offset, 16, (byte)(dc >> 4));
                break;
            }

            default:
                Fill(buffer, offset, 16, 0x80);
                break;
        }
    }

    /// <summary>Fills an 8x8 chroma block with one of the seven whole-block predictions.</summary>
    public static void PredictChroma8(int mode, byte[] buffer, int offset)
    {
        switch (mode)
        {
            case DcMode:
            {
                var dc = 8;

                for (var i = 0; i < 8; i++)
                    dc += buffer[offset + i - Stride] + buffer[offset - 1 + (i * Stride)];

                Fill(buffer, offset, 8, (byte)(dc >> 4));
                break;
            }

            case TrueMotionMode:
                TrueMotion(buffer, offset, 8);
                break;

            case VerticalMode:
                for (var j = 0; j < 8; j++)
                    Array.Copy(buffer, offset - Stride, buffer, offset + (j * Stride), 8);
                break;

            case HorizontalMode:
                for (var j = 0; j < 8; j++)
                {
                    var row = offset + (j * Stride);
                    Array.Fill(buffer, buffer[row - 1], row, 8);
                }

                break;

            case DcNoTopMode:
            {
                var dc = 4;

                for (var i = 0; i < 8; i++)
                    dc += buffer[offset - 1 + (i * Stride)];

                Fill(buffer, offset, 8, (byte)(dc >> 3));
                break;
            }

            case DcNoLeftMode:
            {
                var dc = 4;

                for (var i = 0; i < 8; i++)
                    dc += buffer[offset + i - Stride];

                Fill(buffer, offset, 8, (byte)(dc >> 3));
                break;
            }

            default:
                Fill(buffer, offset, 8, 0x80);
                break;
        }
    }

    /// <summary>Fills a 4x4 luma block with one of the ten subblock predictions (0 to 9).</summary>
    public static void PredictLuma4(int mode, byte[] buffer, int offset)
    {
        switch (mode)
        {
            case 0:
                Dc4(buffer, offset);
                break;
            case 1:
                TrueMotion(buffer, offset, 4);
                break;
            case 2:
                Vertical4(buffer, offset);
                break;
            case 3:
                Horizontal4(buffer, offset);
                break;
            case 4:
                DownRight4(buffer, offset);
                break;
            case 5:
                VerticalRight4(buffer, offset);
                break;
            case 6:
                DownLeft4(buffer, offset);
                break;
            case 7:
                VerticalLeft4(buffer, offset);
                break;
            case 8:
                HorizontalDown4(buffer, offset);
                break;
            default:
                HorizontalUp4(buffer, offset);
                break;
        }
    }

    private static void Fill(byte[] buffer, int offset, int size, byte value)
    {
        for (var j = 0; j < size; j++)
            Array.Fill(buffer, value, offset + (j * Stride), size);
    }

    private static void TrueMotion(byte[] buffer, int offset, int size)
    {
        var top = offset - Stride;
        var topLeft = buffer[top - 1];

        for (var y = 0; y < size; y++)
        {
            var row = offset + (y * Stride);
            var left = buffer[row - 1] - topLeft;

            for (var x = 0; x < size; x++)
                buffer[row + x] = Clip(buffer[top + x] + left);
        }
    }

    private static byte Clip(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;

    private static byte Average3(int a, int b, int c) => (byte)((a + (2 * b) + c + 2) >> 2);

    private static byte Average2(int a, int b) => (byte)((a + b + 1) >> 1);

    private static void Set(byte[] buffer, int offset, int x, int y, byte value) => buffer[offset + x + (y * Stride)] = value;

    private static void Dc4(byte[] buffer, int offset)
    {
        var dc = 4;

        for (var i = 0; i < 4; i++)
            dc += buffer[offset + i - Stride] + buffer[offset - 1 + (i * Stride)];

        Fill(buffer, offset, 4, (byte)(dc >> 3));
    }

    private static void Vertical4(byte[] buffer, int offset)
    {
        var top = offset - Stride;
        Span<byte> values =
        [
            Average3(buffer[top - 1], buffer[top], buffer[top + 1]),
            Average3(buffer[top], buffer[top + 1], buffer[top + 2]),
            Average3(buffer[top + 1], buffer[top + 2], buffer[top + 3]),
            Average3(buffer[top + 2], buffer[top + 3], buffer[top + 4]),
        ];

        for (var y = 0; y < 4; y++)
            values.CopyTo(buffer.AsSpan(offset + (y * Stride), 4));
    }

    private static void Horizontal4(byte[] buffer, int offset)
    {
        int a = buffer[offset - 1 - Stride];
        int b = buffer[offset - 1];
        int c = buffer[offset - 1 + Stride];
        int d = buffer[offset - 1 + (2 * Stride)];
        int e = buffer[offset - 1 + (3 * Stride)];

        Array.Fill(buffer, Average3(a, b, c), offset, 4);
        Array.Fill(buffer, Average3(b, c, d), offset + Stride, 4);
        Array.Fill(buffer, Average3(c, d, e), offset + (2 * Stride), 4);
        Array.Fill(buffer, Average3(d, e, e), offset + (3 * Stride), 4);
    }

    private static void DownRight4(byte[] buffer, int offset)
    {
        int i = buffer[offset - 1];
        int j = buffer[offset - 1 + Stride];
        int k = buffer[offset - 1 + (2 * Stride)];
        int l = buffer[offset - 1 + (3 * Stride)];
        int x = buffer[offset - 1 - Stride];
        int a = buffer[offset - Stride];
        int b = buffer[offset + 1 - Stride];
        int c = buffer[offset + 2 - Stride];
        int d = buffer[offset + 3 - Stride];

        Set(buffer, offset, 0, 3, Average3(j, k, l));
        var ijk = Average3(i, j, k);
        Set(buffer, offset, 1, 3, ijk);
        Set(buffer, offset, 0, 2, ijk);
        var xij = Average3(x, i, j);
        Set(buffer, offset, 2, 3, xij);
        Set(buffer, offset, 1, 2, xij);
        Set(buffer, offset, 0, 1, xij);
        var axi = Average3(a, x, i);
        Set(buffer, offset, 3, 3, axi);
        Set(buffer, offset, 2, 2, axi);
        Set(buffer, offset, 1, 1, axi);
        Set(buffer, offset, 0, 0, axi);
        var bax = Average3(b, a, x);
        Set(buffer, offset, 3, 2, bax);
        Set(buffer, offset, 2, 1, bax);
        Set(buffer, offset, 1, 0, bax);
        var cba = Average3(c, b, a);
        Set(buffer, offset, 3, 1, cba);
        Set(buffer, offset, 2, 0, cba);
        Set(buffer, offset, 3, 0, Average3(d, c, b));
    }

    private static void DownLeft4(byte[] buffer, int offset)
    {
        var top = offset - Stride;
        int a = buffer[top];
        int b = buffer[top + 1];
        int c = buffer[top + 2];
        int d = buffer[top + 3];
        int e = buffer[top + 4];
        int f = buffer[top + 5];
        int g = buffer[top + 6];
        int h = buffer[top + 7];

        Set(buffer, offset, 0, 0, Average3(a, b, c));
        var bcd = Average3(b, c, d);
        Set(buffer, offset, 1, 0, bcd);
        Set(buffer, offset, 0, 1, bcd);
        var cde = Average3(c, d, e);
        Set(buffer, offset, 2, 0, cde);
        Set(buffer, offset, 1, 1, cde);
        Set(buffer, offset, 0, 2, cde);
        var def = Average3(d, e, f);
        Set(buffer, offset, 3, 0, def);
        Set(buffer, offset, 2, 1, def);
        Set(buffer, offset, 1, 2, def);
        Set(buffer, offset, 0, 3, def);
        var efg = Average3(e, f, g);
        Set(buffer, offset, 3, 1, efg);
        Set(buffer, offset, 2, 2, efg);
        Set(buffer, offset, 1, 3, efg);
        var fgh = Average3(f, g, h);
        Set(buffer, offset, 3, 2, fgh);
        Set(buffer, offset, 2, 3, fgh);
        Set(buffer, offset, 3, 3, Average3(g, h, h));
    }

    private static void VerticalRight4(byte[] buffer, int offset)
    {
        int i = buffer[offset - 1];
        int j = buffer[offset - 1 + Stride];
        int k = buffer[offset - 1 + (2 * Stride)];
        int x = buffer[offset - 1 - Stride];
        int a = buffer[offset - Stride];
        int b = buffer[offset + 1 - Stride];
        int c = buffer[offset + 2 - Stride];
        int d = buffer[offset + 3 - Stride];

        var xa = Average2(x, a);
        Set(buffer, offset, 0, 0, xa);
        Set(buffer, offset, 1, 2, xa);
        var ab = Average2(a, b);
        Set(buffer, offset, 1, 0, ab);
        Set(buffer, offset, 2, 2, ab);
        var bc = Average2(b, c);
        Set(buffer, offset, 2, 0, bc);
        Set(buffer, offset, 3, 2, bc);
        Set(buffer, offset, 3, 0, Average2(c, d));

        Set(buffer, offset, 0, 3, Average3(k, j, i));
        Set(buffer, offset, 0, 2, Average3(j, i, x));
        var ixa = Average3(i, x, a);
        Set(buffer, offset, 0, 1, ixa);
        Set(buffer, offset, 1, 3, ixa);
        var xab = Average3(x, a, b);
        Set(buffer, offset, 1, 1, xab);
        Set(buffer, offset, 2, 3, xab);
        var abc = Average3(a, b, c);
        Set(buffer, offset, 2, 1, abc);
        Set(buffer, offset, 3, 3, abc);
        Set(buffer, offset, 3, 1, Average3(b, c, d));
    }

    private static void VerticalLeft4(byte[] buffer, int offset)
    {
        var top = offset - Stride;
        int a = buffer[top];
        int b = buffer[top + 1];
        int c = buffer[top + 2];
        int d = buffer[top + 3];
        int e = buffer[top + 4];
        int f = buffer[top + 5];
        int g = buffer[top + 6];
        int h = buffer[top + 7];

        Set(buffer, offset, 0, 0, Average2(a, b));
        var bc = Average2(b, c);
        Set(buffer, offset, 1, 0, bc);
        Set(buffer, offset, 0, 2, bc);
        var cd = Average2(c, d);
        Set(buffer, offset, 2, 0, cd);
        Set(buffer, offset, 1, 2, cd);
        var de = Average2(d, e);
        Set(buffer, offset, 3, 0, de);
        Set(buffer, offset, 2, 2, de);

        Set(buffer, offset, 0, 1, Average3(a, b, c));
        var bcd = Average3(b, c, d);
        Set(buffer, offset, 1, 1, bcd);
        Set(buffer, offset, 0, 3, bcd);
        var cde = Average3(c, d, e);
        Set(buffer, offset, 2, 1, cde);
        Set(buffer, offset, 1, 3, cde);
        var def = Average3(d, e, f);
        Set(buffer, offset, 3, 1, def);
        Set(buffer, offset, 2, 3, def);
        Set(buffer, offset, 3, 2, Average3(e, f, g));
        Set(buffer, offset, 3, 3, Average3(f, g, h));
    }

    private static void HorizontalUp4(byte[] buffer, int offset)
    {
        int i = buffer[offset - 1];
        int j = buffer[offset - 1 + Stride];
        int k = buffer[offset - 1 + (2 * Stride)];
        int l = buffer[offset - 1 + (3 * Stride)];

        Set(buffer, offset, 0, 0, Average2(i, j));
        var jk = Average2(j, k);
        Set(buffer, offset, 2, 0, jk);
        Set(buffer, offset, 0, 1, jk);
        var kl = Average2(k, l);
        Set(buffer, offset, 2, 1, kl);
        Set(buffer, offset, 0, 2, kl);
        Set(buffer, offset, 1, 0, Average3(i, j, k));
        var jkl = Average3(j, k, l);
        Set(buffer, offset, 3, 0, jkl);
        Set(buffer, offset, 1, 1, jkl);
        var kll = Average3(k, l, l);
        Set(buffer, offset, 3, 1, kll);
        Set(buffer, offset, 1, 2, kll);
        var last = (byte)l;
        Set(buffer, offset, 3, 2, last);
        Set(buffer, offset, 2, 2, last);
        Set(buffer, offset, 0, 3, last);
        Set(buffer, offset, 1, 3, last);
        Set(buffer, offset, 2, 3, last);
        Set(buffer, offset, 3, 3, last);
    }

    private static void HorizontalDown4(byte[] buffer, int offset)
    {
        int i = buffer[offset - 1];
        int j = buffer[offset - 1 + Stride];
        int k = buffer[offset - 1 + (2 * Stride)];
        int l = buffer[offset - 1 + (3 * Stride)];
        int x = buffer[offset - 1 - Stride];
        int a = buffer[offset - Stride];
        int b = buffer[offset + 1 - Stride];
        int c = buffer[offset + 2 - Stride];

        var ix = Average2(i, x);
        Set(buffer, offset, 0, 0, ix);
        Set(buffer, offset, 2, 1, ix);
        var ji = Average2(j, i);
        Set(buffer, offset, 0, 1, ji);
        Set(buffer, offset, 2, 2, ji);
        var kj = Average2(k, j);
        Set(buffer, offset, 0, 2, kj);
        Set(buffer, offset, 2, 3, kj);
        Set(buffer, offset, 0, 3, Average2(l, k));

        Set(buffer, offset, 3, 0, Average3(a, b, c));
        Set(buffer, offset, 2, 0, Average3(x, a, b));
        var ixa = Average3(i, x, a);
        Set(buffer, offset, 1, 0, ixa);
        Set(buffer, offset, 3, 1, ixa);
        var jix = Average3(j, i, x);
        Set(buffer, offset, 1, 1, jix);
        Set(buffer, offset, 3, 2, jix);
        var kji = Average3(k, j, i);
        Set(buffer, offset, 1, 2, kji);
        Set(buffer, offset, 3, 3, kji);
        Set(buffer, offset, 1, 3, Average3(l, k, j));
    }
}
