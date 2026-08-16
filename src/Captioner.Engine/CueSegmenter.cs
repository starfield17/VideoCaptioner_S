using System.Globalization;
using Captioner.Core;

namespace Captioner.Engine;

public static class CueSegmenter
{
    public static IReadOnlyList<SubtitleCue> BuildCues(
        IReadOnlyList<TimedAnchor> anchors,
        IReadOnlyList<string> requestedBoundaryIds,
        int maxCueCharacters,
        long maxCueDurationMs)
    {
        if (anchors.Count == 0)
        {
            return [];
        }

        var requested = requestedBoundaryIds.ToHashSet(StringComparer.Ordinal);
        var cues = new List<SubtitleCue>();
        var current = new List<TimedAnchor>();
        var cueIndex = 1;

        foreach (var anchor in anchors)
        {
            current.Add(anchor);
            var text = JoinText(current);
            var duration = current[^1].EndMs - current[0].StartMs;
            var mustCut = TextLength(text) >= maxCueCharacters || duration >= maxCueDurationMs;
            var requestedCut = requested.Contains(anchor.Id);

            if ((mustCut || requestedCut) && current.Count > 0)
            {
                cues.Add(CreateCue(current, cueIndex++));
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            cues.Add(CreateCue(current, cueIndex));
        }

        return cues;
    }

    private static SubtitleCue CreateCue(IReadOnlyList<TimedAnchor> anchors, int index)
    {
        var origin = anchors.Any(anchor => anchor.Origin == TimingOrigin.Estimated)
            ? TimingOrigin.Estimated
            : anchors.All(anchor => anchor.Origin == TimingOrigin.ProviderWord)
                ? TimingOrigin.ProviderWord
                : TimingOrigin.ProviderSegment;

        return new(
            $"c{index:D6}",
            anchors[0].StartMs,
            anchors[^1].EndMs,
            JoinText(anchors),
            TimingOrigin: origin);
    }

    private static int TextLength(string value) => new StringInfo(value).LengthInTextElements;

    private static string JoinText(IReadOnlyList<TimedAnchor> anchors)
    {
        var result = string.Empty;
        foreach (var anchor in anchors)
        {
            var text = anchor.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (result.Length > 0 && NeedsSpace(result[^1], text[0]))
            {
                result += " ";
            }

            result += text;
        }

        return result.Trim();
    }

    private static bool NeedsSpace(char left, char right) =>
        IsLatinLike(left) && IsLatinLike(right);

    private static bool IsLatinLike(char value) => char.IsLetterOrDigit(value) &&
        !(value is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF');
}
