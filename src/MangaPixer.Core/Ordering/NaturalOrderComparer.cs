namespace com.lifepixer.mangaplex.Core.Ordering;

/// <summary>
/// Natural-order comparison for folder names, archive filenames, and archive entry paths.
/// Compares digit runs numerically using length and lexicographic comparison,
/// not fixed-width integer parsing. Text is compared culture-independently (ordinal),
/// then exact spelling is used as a tie-breaker.
/// </summary>
public sealed class NaturalOrderComparer : IComparer<string>, IComparer<ReadOnlySpan<char>>
{
    public static readonly NaturalOrderComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return Compare(x.AsSpan(), y.AsSpan());
    }

    public int Compare(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        int xi = 0, yi = 0;
        while (xi < x.Length && yi < y.Length)
        {
            var xc = x[xi];
            var yc = y[yi];

            if (char.IsDigit(xc) && char.IsDigit(yc))
            {
                var (xResult, xEnd) = CompareDigitRun(x, xi, y, yi);
                if (xResult != 0) return xResult;
                xi = xEnd;
                // yi is advanced inside CompareDigitRun via the returned value
                // but we need to find y's end too
                yi = FindDigitEnd(y, yi);
            }
            else
            {
                var cmp = CompareChars(xc, yc);
                if (cmp != 0) return cmp;
                xi++;
                yi++;
            }
        }

        // Shorter string comes first if all preceding parts were equal
        return (x.Length - xi).CompareTo(y.Length - yi);
    }

    private static (int result, int xEnd) CompareDigitRun(ReadOnlySpan<char> x, int xi, ReadOnlySpan<char> y, int yi)
    {
        int xStart = xi, yStart = yi;
        xi = FindDigitEnd(x, xi);
        yi = FindDigitEnd(y, yi);

        int xLen = xi - xStart;
        int yLen = yi - yStart;

        // Always compare by effective length first (strip leading zeros).
        // This handles cases like "002" (eff=1) vs "010" (eff=2) correctly,
        // even when total digit run lengths are equal.
        int xZeros = CountLeadingZeros(x, xStart, xLen);
        int yZeros = CountLeadingZeros(y, yStart, yLen);
        int xEffective = xLen - xZeros;
        int yEffective = yLen - yZeros;

        if (xEffective != yEffective)
            return (xEffective.CompareTo(yEffective), xi);

        // Same effective length: lexicographic comparison of the significant digits
        int xOff = xStart + xZeros;
        int yOff = yStart + yZeros;
        int xSigLen = xi - xOff;
        int ySigLen = yi - yOff;

        int sigCmp = x.Slice(xOff, xSigLen).CompareTo(y.Slice(yOff, ySigLen), StringComparison.Ordinal);
        if (sigCmp != 0)
            return (sigCmp, xi);

        // Leading-zero tie: more leading zeros comes first (deterministic, matches zero-padded ordering: 001 < 01 < 1)
        if (xZeros != yZeros)
            return (yZeros.CompareTo(xZeros), xi);

        return (0, xi);
    }

    private static int FindDigitEnd(ReadOnlySpan<char> s, int start)
    {
        int i = start;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        return i;
    }

    private static int CountLeadingZeros(ReadOnlySpan<char> s, int start, int length)
    {
        int zeros = 0;
        for (int i = start; i < start + length && s[i] == '0'; i++) zeros++;
        return zeros;
    }

    private static int CompareChars(char x, char y)
    {
        return x.CompareTo(y);
    }
}
