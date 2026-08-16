namespace Captioner.Core;

public enum TimingOrigin
{
    ProviderWord,
    ProviderSegment,
    Estimated
}

public sealed record TimedAnchor(
    string Id,
    string Text,
    long StartMs,
    long EndMs,
    TimingOrigin Origin);

public sealed record TranscriptDocument(
    string? Language,
    long MediaDurationMs,
    IReadOnlyList<TimedAnchor> Anchors);

public sealed record SubtitleCue(
    string Id,
    long StartMs,
    long EndMs,
    string SourceText,
    string? TranslatedText = null,
    TimingOrigin TimingOrigin = TimingOrigin.ProviderSegment);

public sealed record SubtitleDocument(
    string? SourceLanguage,
    string? TargetLanguage,
    long MediaDurationMs,
    IReadOnlyList<SubtitleCue> Cues);

public enum SubtitleLayout
{
    Source,
    Target,
    Bilingual
}
