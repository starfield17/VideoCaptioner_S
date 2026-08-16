using Captioner.Core;
using Captioner.Infrastructure;

namespace Captioner.Tests;

public sealed class FileJobWorkspaceTests
{
    [Fact]
    public async Task Write_read_and_verify_round_trip_and_reject_escaping_paths()
    {
        using var temporary = new DisposableDirectory();
        var workspace = new FileJobWorkspace(temporary.Path);
        var job = new JobManifest(
            1, "batch-aaaa-job", "batch-aaaa", "/in.mp4", "in.mp4", "sha", "/out.srt",
            new Dictionary<string, StageRecord>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        await workspace.SaveJobAsync(job, CancellationToken.None);
        var artifact = await workspace.WriteArtifactAsync(job.JobId, "probe.json", new { Ok = true }, CancellationToken.None);
        var loaded = await workspace.ReadArtifactAsync<JsonProbe>(job.JobId, artifact, CancellationToken.None);

        Assert.True(loaded.Ok);
        Assert.True(await workspace.VerifyArtifactAsync(job.JobId, artifact, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            workspace.WriteArtifactAsync(job.JobId, "../escape.json", new { Ok = false }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            workspace.WriteArtifactAsync(job.JobId, "/rooted.json", new { Ok = false }, CancellationToken.None));
    }

    private sealed record JsonProbe(bool Ok);

    private sealed class DisposableDirectory : IDisposable
    {
        public DisposableDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "captioner-ws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
