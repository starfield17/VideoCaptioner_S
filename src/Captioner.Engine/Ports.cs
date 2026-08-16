using Captioner.Core;

namespace Captioner.Engine;

public interface IMediaTool
{
    Task<MediaInfoArtifact> ProbeAsync(string inputPath, CancellationToken cancellationToken);

    Task<MediaChunksArtifact> CreateChunksAsync(
        string inputPath,
        string workingDirectory,
        MediaInfoArtifact media,
        EndpointCapabilities capabilities,
        CancellationToken cancellationToken);

    Task<bool> VerifyChunksAsync(MediaChunksArtifact chunks, CancellationToken cancellationToken);
}

public interface IAsrClient
{
    Task<TranscriptDocument> TranscribeAsync(
        MediaChunk chunk,
        EndpointProfile profile,
        string? language,
        CancellationToken cancellationToken);

    Task<string?> CheckAsync(EndpointProfile profile, CancellationToken cancellationToken);
}

public interface ILlmClient
{
    Task<IReadOnlyList<string>> SelectCueBoundariesAsync(
        IReadOnlyList<TimedAnchor> anchors,
        EndpointProfile profile,
        string? language,
        int maxCueCharacters,
        long maxCueDurationMs,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> CorrectAsync(
        IReadOnlyList<SubtitleCue> cues,
        EndpointProfile profile,
        string? language,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<SubtitleCue> cues,
        EndpointProfile profile,
        string targetLanguage,
        CancellationToken cancellationToken);

    Task<string?> CheckAsync(EndpointProfile profile, CancellationToken cancellationToken);
}

public interface IJobWorkspace
{
    string GetJobDirectory(string jobId);

    Task SaveBatchAsync(BatchManifest manifest, CancellationToken cancellationToken);
    Task<BatchManifest?> LoadBatchAsync(string batchId, CancellationToken cancellationToken);

    Task SaveJobAsync(JobManifest manifest, CancellationToken cancellationToken);
    Task<JobManifest?> LoadJobAsync(string jobId, CancellationToken cancellationToken);

    Task<ArtifactReference> WriteArtifactAsync<T>(
        string jobId,
        string name,
        T value,
        CancellationToken cancellationToken);

    Task<T> ReadArtifactAsync<T>(
        string jobId,
        ArtifactReference artifact,
        CancellationToken cancellationToken);

    Task<bool> VerifyArtifactAsync(
        string jobId,
        ArtifactReference artifact,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListBatchIdsAsync(CancellationToken cancellationToken);
    Task CleanBatchAsync(string batchId, CancellationToken cancellationToken);
}

public interface ISubtitlePublisher
{
    Task<ExportArtifact> PublishAsync(
        SubtitleDocument document,
        string outputPath,
        SubtitleLayout layout,
        bool overwrite,
        CancellationToken cancellationToken);

    Task<bool> VerifyAsync(ExportArtifact artifact, CancellationToken cancellationToken);
}
