namespace Captioner.Core;

public sealed record TimelineIssue(string Code, string Message, string? AnchorId = null);

public sealed record TimelineValidationResult(IReadOnlyList<TimelineIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public static class TimelineValidator
{
    public static TimelineValidationResult ValidateAnchors(
        IReadOnlyList<TimedAnchor> anchors,
        long mediaDurationMs,
        long allowedOverlapMs = 250)
    {
        var issues = new List<TimelineIssue>();
        long previousStart = -1;
        long previousEnd = -1;

        foreach (var anchor in anchors)
        {
            ValidateRange(anchor.Id, anchor.StartMs, anchor.EndMs, mediaDurationMs, issues);

            if (anchor.StartMs < previousStart)
            {
                issues.Add(new("non_monotonic", "Anchor starts before the previous anchor.", anchor.Id));
            }

            if (previousEnd >= 0 && previousEnd - anchor.StartMs > allowedOverlapMs)
            {
                issues.Add(new("excessive_overlap", "Anchor overlap exceeds the configured allowance.", anchor.Id));
            }

            previousStart = anchor.StartMs;
            previousEnd = Math.Max(previousEnd, anchor.EndMs);
        }

        return new(issues);
    }

    public static TimelineValidationResult ValidateCues(
        IReadOnlyList<SubtitleCue> cues,
        long mediaDurationMs,
        long allowedOverlapMs = 250)
    {
        var issues = new List<TimelineIssue>();
        long previousStart = -1;
        long previousEnd = -1;

        foreach (var cue in cues)
        {
            ValidateRange(cue.Id, cue.StartMs, cue.EndMs, mediaDurationMs, issues);

            if (string.IsNullOrWhiteSpace(cue.SourceText))
            {
                issues.Add(new("empty_text", "Cue source text is empty.", cue.Id));
            }

            if (cue.StartMs < previousStart)
            {
                issues.Add(new("non_monotonic", "Cue starts before the previous cue.", cue.Id));
            }

            if (previousEnd >= 0 && previousEnd - cue.StartMs > allowedOverlapMs)
            {
                issues.Add(new("excessive_overlap", "Cue overlap exceeds the configured allowance.", cue.Id));
            }

            previousStart = cue.StartMs;
            previousEnd = Math.Max(previousEnd, cue.EndMs);
        }

        return new(issues);
    }

    private static void ValidateRange(
        string id,
        long startMs,
        long endMs,
        long mediaDurationMs,
        ICollection<TimelineIssue> issues)
    {
        if (startMs < 0)
        {
            issues.Add(new("negative_start", "Start time is negative.", id));
        }

        if (endMs <= startMs)
        {
            issues.Add(new("non_positive_duration", "End time must be greater than start time.", id));
        }

        var latestAllowedEnd = mediaDurationMs > long.MaxValue - 500
            ? long.MaxValue
            : mediaDurationMs + 500;
        if (endMs > latestAllowedEnd)
        {
            issues.Add(new("outside_media", "End time is outside the media duration.", id));
        }
    }
}
