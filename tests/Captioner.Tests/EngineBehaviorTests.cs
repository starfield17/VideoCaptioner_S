using Captioner.Core;
using Captioner.Engine;

namespace Captioner.Tests;

public sealed class EngineBehaviorTests
{
    [Fact]
    public void AnchorEstimator_expands_segment_anchors_with_estimated_timing_and_exact_outer_bounds()
    {
        var source = new[]
        {
            new TimedAnchor("segment", "one two three", 123, 1_234, TimingOrigin.ProviderSegment)
        };

        var result = AnchorEstimator.EnsureSplittableAnchors(source);

        Assert.Equal(3, result.Count);
        Assert.Equal(123, result[0].StartMs);
        Assert.Equal(1_234, result[^1].EndMs);
        Assert.All(result, anchor => Assert.Equal(TimingOrigin.Estimated, anchor.Origin));
        Assert.Equal(new[] { "one", "two", "three" }, result.Select(anchor => anchor.Text));
    }

    [Fact]
    public void AnchorEstimator_leaves_provider_word_anchors_unchanged()
    {
        var source = new[]
        {
            new TimedAnchor("word-1", "hello", 10, 100, TimingOrigin.ProviderWord),
            new TimedAnchor("word-2", "world", 100, 250, TimingOrigin.ProviderWord)
        };

        var result = AnchorEstimator.EnsureSplittableAnchors(source);

        Assert.Equal(source, result);
        Assert.Same(source, result);
    }

    [Fact]
    public void CueSegmenter_honors_requested_boundaries_and_preserves_contiguous_timing()
    {
        var anchors = new[]
        {
            new TimedAnchor("a", "Hello", 0, 500, TimingOrigin.ProviderWord),
            new TimedAnchor("b", "world", 500, 1_000, TimingOrigin.ProviderWord),
            new TimedAnchor("c", "Again", 1_000, 1_500, TimingOrigin.ProviderWord)
        };

        var cues = CueSegmenter.BuildCues(anchors, ["b"], maxCueCharacters: 100, maxCueDurationMs: 10_000);

        Assert.Equal(2, cues.Count);
        Assert.Equal((0, 1_000), (cues[0].StartMs, cues[0].EndMs));
        Assert.Equal("Hello world", cues[0].SourceText);
        Assert.Equal((1_000, 1_500), (cues[1].StartMs, cues[1].EndMs));
        Assert.Equal("Again", cues[1].SourceText);
        Assert.Equal(cues[0].EndMs, cues[1].StartMs);
    }

    [Fact]
    public void CueSegmenter_applies_deterministic_character_and_duration_cuts_with_estimated_origin()
    {
        var anchors = new[]
        {
            new TimedAnchor("a", "aa", 0, 400, TimingOrigin.Estimated),
            new TimedAnchor("b", "bb", 400, 800, TimingOrigin.Estimated),
            new TimedAnchor("c", "cc", 800, 1_200, TimingOrigin.Estimated),
            new TimedAnchor("d", "dd", 1_200, 1_600, TimingOrigin.Estimated)
        };

        var byCharacters = CueSegmenter.BuildCues(anchors, [], maxCueCharacters: 5, maxCueDurationMs: 10_000);
        var byDuration = CueSegmenter.BuildCues(anchors, [], maxCueCharacters: 100, maxCueDurationMs: 900);

        Assert.Equal(new[] { "aa bb", "cc dd" }, byCharacters.Select(cue => cue.SourceText));
        Assert.Equal(new[] { "aa bb cc", "dd" }, byDuration.Select(cue => cue.SourceText));
        Assert.Equal(new[] { (0L, 800L), (800L, 1_600L) }, byCharacters.Select(cue => (cue.StartMs, cue.EndMs)));
        Assert.Equal(new[] { (0L, 1_200L), (1_200L, 1_600L) }, byDuration.Select(cue => (cue.StartMs, cue.EndMs)));
        Assert.All(byCharacters.Concat(byDuration), cue => Assert.Equal(TimingOrigin.Estimated, cue.TimingOrigin));
        Assert.Equal(byCharacters.Select(cue => cue.Id), byDuration.Select(cue => cue.Id));
    }

    [Fact]
    public void StageFingerprint_is_deterministic_and_changes_for_version_upstream_or_settings()
    {
        var settings = new { Model = "model-a", Limit = 42 };
        var baseline = StageFingerprint.Create("segment", 1, "upstream-a", settings);

        Assert.Equal(baseline, StageFingerprint.Create("segment", 1, "upstream-a", new { Model = "model-a", Limit = 42 }));
        Assert.NotEqual(baseline, StageFingerprint.Create("segment", 2, "upstream-a", settings));
        Assert.NotEqual(baseline, StageFingerprint.Create("segment", 1, "upstream-b", settings));
        Assert.NotEqual(baseline, StageFingerprint.Create("segment", 1, "upstream-a", new { Model = "model-b", Limit = 42 }));
    }

    [Fact]
    public async Task BatchRunner_prepare_scopes_jobs_to_the_batch_and_saves_pending_manifests()
    {
        var workspace = new RecordingWorkspace();
        var runner = new BatchRunner(null!, workspace);
        var input = new MediaInput("/media/one.mp4", "nested/one.mp4", "same-sha", "/out/one.srt");

        var first = await runner.PrepareAsync([input], CancellationToken.None);
        var second = await runner.PrepareAsync([input with { Path = "/other/path.mp4", OutputPath = "/other/out.srt" }], CancellationToken.None);

        Assert.NotEqual(first.Jobs[0].JobId, second.Jobs[0].JobId);
        Assert.StartsWith(first.BatchId, first.Jobs[0].JobId, StringComparison.Ordinal);
        Assert.StartsWith(second.BatchId, second.Jobs[0].JobId, StringComparison.Ordinal);
        Assert.Equal(2, workspace.SavedJobs.Count);
        Assert.Equal(2, workspace.SavedBatches.Count);
        var saved = workspace.SavedJobs[0];
        Assert.Equal(first.BatchId, saved.BatchId);
        Assert.Equal(StageNames.Ordered, saved.Stages.Keys);
        Assert.All(saved.Stages.Values, stage =>
        {
            Assert.Equal(StageStatus.Pending, stage.Status);
            Assert.Equal(string.Empty, stage.Fingerprint);
        });
    }

    private sealed class RecordingWorkspace : IJobWorkspace
    {
        public List<JobManifest> SavedJobs { get; } = [];
        public List<BatchManifest> SavedBatches { get; } = [];

        public string GetJobDirectory(string jobId) => $"/tmp/{jobId}";

        public Task SaveBatchAsync(BatchManifest manifest, CancellationToken cancellationToken)
        {
            SavedBatches.Add(manifest);
            return Task.CompletedTask;
        }

        public Task<BatchManifest?> LoadBatchAsync(string batchId, CancellationToken cancellationToken) =>
            Task.FromResult<BatchManifest?>(SavedBatches.SingleOrDefault(batch => batch.BatchId == batchId));

        public Task SaveJobAsync(JobManifest manifest, CancellationToken cancellationToken)
        {
            SavedJobs.Add(manifest);
            return Task.CompletedTask;
        }

        public Task<JobManifest?> LoadJobAsync(string jobId, CancellationToken cancellationToken) =>
            Task.FromResult<JobManifest?>(SavedJobs.LastOrDefault(job => job.JobId == jobId));

        public Task<ArtifactReference> WriteArtifactAsync<T>(string jobId, string name, T value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<T> ReadArtifactAsync<T>(string jobId, ArtifactReference artifact, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> VerifyArtifactAsync(string jobId, ArtifactReference artifact, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListBatchIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(SavedBatches.Select(batch => batch.BatchId).ToArray());

        public Task CleanBatchAsync(string batchId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
