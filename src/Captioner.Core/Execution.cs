namespace Captioner.Core;

/// <summary>The complete, durable set of choices that affect pipeline output or scheduling.</summary>
public sealed record PipelineOptions(
    EndpointProfile Asr,
    EndpointProfile Llm,
    string? SourceLanguage = null,
    string? TargetLanguage = null,
    SubtitleLayout Layout = SubtitleLayout.Bilingual,
    bool EnableSegmentation = true,
    bool EnableCorrection = true,
    int MaxCueCharacters = 42,
    long MaxCueDurationMs = 7_000,
    int MaxFileConcurrency = 2,
    bool Overwrite = false);
