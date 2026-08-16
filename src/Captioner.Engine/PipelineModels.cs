using Captioner.Core;

namespace Captioner.Engine;

public sealed record MediaInput(
    string Path,
    string RelativePath,
    string Sha256,
    string OutputPath);

public sealed record MediaInfoArtifact(long DurationMs, long SizeBytes, bool HasAudio);

public sealed record MediaChunk(
    string Path,
    long OffsetMs,
    long DurationMs,
    long SizeBytes,
    int Index);

public sealed record MediaChunksArtifact(IReadOnlyList<MediaChunk> Chunks);

public sealed record ExportArtifact(string OutputPath, string Sha256, int CueCount);

public sealed record JobRunResult(string JobId, bool Succeeded, string? OutputPath, string? Error);

public sealed record BatchRunResult(string BatchId, IReadOnlyList<JobRunResult> Jobs)
{
    public bool Succeeded => Jobs.All(job => job.Succeeded);
}

public sealed class PipelineBlockedException(string message) : Exception(message);
