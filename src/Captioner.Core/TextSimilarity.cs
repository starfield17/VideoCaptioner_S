using System.Globalization;
using System.Text;

namespace Captioner.Core;

/// <summary>Ratcliff/Obershelp-style similarity used to reject over-aggressive correction.</summary>
public static class TextSimilarity
{
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static double Ratio(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        return 2d * MatchLength(a.AsSpan(), b.AsSpan()) / (a.Length + b.Length);
    }

    public static double CorrectionThreshold(string original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return Measure(original) <= 10 ? 0.3 : 0.7;
    }

    public static int GraphemeCount(string value) =>
        new StringInfo(value ?? string.Empty).LengthInTextElements;

    public static int LatinWordCount(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    public static bool IsMainlyCjk(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var cjk = 0;
        var other = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherPunctuation)
            {
                continue;
            }

            if (IsCjk(rune))
            {
                cjk++;
            }
            else
            {
                other++;
            }
        }

        return cjk > 0 && cjk >= other;
    }

    public static int Measure(string value) =>
        IsMainlyCjk(value) ? GraphemeCount(Normalize(value).Replace(" ", string.Empty, StringComparison.Ordinal))
            : LatinWordCount(value);

    public static bool IsCjk(Rune rune) => rune.Value is
        >= 0x3400 and <= 0x9FFF or
        >= 0x3040 and <= 0x30FF or
        >= 0xAC00 and <= 0xD7AF;

    private static int MatchLength(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var (indexLeft, indexRight, length) = LongestMatch(left, right);
        if (length == 0)
        {
            return 0;
        }

        var total = length;
        if (indexLeft > 0 && indexRight > 0)
        {
            total += MatchLength(left[..indexLeft], right[..indexRight]);
        }

        var leftRest = indexLeft + length;
        var rightRest = indexRight + length;
        if (leftRest < left.Length && rightRest < right.Length)
        {
            total += MatchLength(left[leftRest..], right[rightRest..]);
        }

        return total;
    }

    private static (int Left, int Right, int Length) LongestMatch(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var bestLeft = 0;
        var bestRight = 0;
        var bestLength = 0;
        for (var i = 0; i < left.Length; i++)
        {
            for (var j = 0; j < right.Length; j++)
            {
                var length = 0;
                while (i + length < left.Length &&
                       j + length < right.Length &&
                       left[i + length] == right[j + length])
                {
                    length++;
                }

                if (length > bestLength)
                {
                    bestLeft = i;
                    bestRight = j;
                    bestLength = length;
                }
            }
        }

        return (bestLeft, bestRight, bestLength);
    }
}
