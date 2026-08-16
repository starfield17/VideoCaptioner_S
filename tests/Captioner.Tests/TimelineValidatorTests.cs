using Captioner.Core;

namespace Captioner.Tests;

public sealed class TimelineValidatorTests
{
    [Fact]
    public void ValidateAnchors_accepts_a_valid_monotonic_timeline()
    {
        var anchors = new[]
        {
            new TimedAnchor("a", "hello", 0, 500, TimingOrigin.ProviderWord),
            new TimedAnchor("b", "world", 500, 1_000, TimingOrigin.ProviderWord)
        };

        var result = TimelineValidator.ValidateAnchors(anchors, 1_000);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ValidateAnchors_reports_negative_non_positive_and_outside_ranges()
    {
        var anchors = new[]
        {
            new TimedAnchor("negative", "x", -1, 20, TimingOrigin.ProviderSegment),
            new TimedAnchor("zero", "x", 20, 20, TimingOrigin.ProviderSegment),
            new TimedAnchor("outside", "x", 30, 1_501, TimingOrigin.ProviderSegment)
        };

        var result = TimelineValidator.ValidateAnchors(anchors, 1_000);

        Assert.Contains(result.Issues, issue => issue.Code == "negative_start" && issue.AnchorId == "negative");
        Assert.Contains(result.Issues, issue => issue.Code == "non_positive_duration" && issue.AnchorId == "zero");
        Assert.Contains(result.Issues, issue => issue.Code == "outside_media" && issue.AnchorId == "outside");
    }

    [Fact]
    public void ValidateAnchors_reports_non_monotonic_starts()
    {
        var anchors = new[]
        {
            new TimedAnchor("first", "one", 500, 700, TimingOrigin.ProviderSegment),
            new TimedAnchor("second", "two", 400, 600, TimingOrigin.ProviderSegment)
        };

        var result = TimelineValidator.ValidateAnchors(anchors, 1_000);

        Assert.Contains(result.Issues, issue => issue.Code == "non_monotonic" && issue.AnchorId == "second");
    }

    [Fact]
    public void ValidateAnchors_allows_bounded_overlap_but_rejects_excessive_overlap()
    {
        var bounded = new[]
        {
            new TimedAnchor("first", "one", 0, 500, TimingOrigin.ProviderSegment),
            new TimedAnchor("second", "two", 400, 700, TimingOrigin.ProviderSegment)
        };
        var excessive = new[]
        {
            new TimedAnchor("first", "one", 0, 500, TimingOrigin.ProviderSegment),
            new TimedAnchor("second", "two", 200, 700, TimingOrigin.ProviderSegment)
        };

        var boundedResult = TimelineValidator.ValidateAnchors(bounded, 1_000, allowedOverlapMs: 100);
        var excessiveResult = TimelineValidator.ValidateAnchors(excessive, 1_000, allowedOverlapMs: 100);

        Assert.DoesNotContain(boundedResult.Issues, issue => issue.Code == "excessive_overlap");
        Assert.Contains(excessiveResult.Issues, issue => issue.Code == "excessive_overlap" && issue.AnchorId == "second");
    }
}
