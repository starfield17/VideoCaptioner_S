namespace Captioner.Core;

public enum StageStatus
{
    Pending,
    Running,
    Ready,
    RetryableFailed,
    Blocked,
    Invalidated
}

public static class StageNames
{
    public const string Probe = "probe";
    public const string Chunks = "chunks";
    public const string Transcribe = "transcribe";
    public const string Segment = "segment";
    public const string Correct = "correct";
    public const string Translate = "translate";
    public const string Export = "export";

    public static readonly IReadOnlyList<string> Ordered =
    [Probe, Chunks, Transcribe, Segment, Correct, Translate, Export];
}

public sealed record ArtifactReference(string RelativePath, string Sha256);

public sealed record StageRecord(
    string Name,
    StageStatus Status,
    string Fingerprint,
    ArtifactReference? Artifact = null,
    string? ErrorCategory = null,
    string? Diagnostic = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null);

public enum SourceKind
{
    Media,
    Subtitle
}

public sealed record JobManifest(
    int SchemaVersion,
    string JobId,
    string BatchId,
    string InputPath,
    string RelativeInputPath,
    string InputSha256,
    string OutputPath,
    IReadOnlyDictionary<string, StageRecord> Stages,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    SourceKind SourceKind = SourceKind.Media);

public sealed record BatchJob(string JobId, string InputPath, string RelativeInputPath);

public sealed record BatchManifest(
    int SchemaVersion,
    string BatchId,
    IReadOnlyList<BatchJob> Jobs,
    DateTimeOffset CreatedAt,
    PipelineOptions? Options = null);
