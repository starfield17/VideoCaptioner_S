using System.Globalization;
using System.Text;
using Captioner.Core;

namespace Captioner.Engine;

public static class AnchorEstimator
{
    public static IReadOnlyList<TimedAnchor> EnsureSplittableAnchors(IReadOnlyList<TimedAnchor> anchors)
    {
        if (anchors.Any(anchor => anchor.Origin == TimingOrigin.ProviderWord))
        {
            return anchors;
        }

        var estimated = new List<TimedAnchor>();
        foreach (var anchor in anchors)
        {
            var tokens = Tokenize(anchor.Text);
            if (tokens.Count <= 1)
            {
                estimated.Add(anchor);
                continue;
            }

            var duration = Math.Max(tokens.Count, anchor.EndMs - anchor.StartMs);
            var weights = tokens.Select(GetWeight).ToArray();
            var totalWeight = weights.Sum();
            var cursor = anchor.StartMs;

            for (var index = 0; index < tokens.Count; index++)
            {
                var end = index == tokens.Count - 1
                    ? anchor.EndMs
                    : Math.Min(anchor.EndMs, cursor + Math.Max(1, duration * weights[index] / totalWeight));

                estimated.Add(new(
                    $"{anchor.Id}.e{index + 1}",
                    tokens[index],
                    cursor,
                    end,
                    TimingOrigin.Estimated));
                cursor = end;
            }
        }

        return estimated;
    }

    private static int GetWeight(string token) =>
        Math.Max(1, new StringInfo(token).LengthInTextElements);

    private static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();

        void FlushWord()
        {
            if (word.Length == 0)
            {
                return;
            }

            tokens.Add(word.ToString());
            word.Clear();
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                FlushWord();
                continue;
            }

            if (IsCjk(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherPunctuation)
            {
                FlushWord();
                tokens.Add(rune.ToString());
                continue;
            }

            word.Append(rune.ToString());
        }

        FlushWord();
        return tokens;
    }

    private static bool IsCjk(Rune rune) => rune.Value is
        >= 0x3400 and <= 0x9FFF or
        >= 0x3040 and <= 0x30FF or
        >= 0xAC00 and <= 0xD7AF;
}
