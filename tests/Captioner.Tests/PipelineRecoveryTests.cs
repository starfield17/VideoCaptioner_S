using Captioner.Core;
using Captioner.Engine;
using Captioner.Infrastructure;

namespace Captioner.Tests;

public sealed class PipelineRecoveryTests
{
    [Fact]
    public async Task Retry_resumes_after_the_failed_stage_and_settings_invalidate_only_downstream_work()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = new FileJobWorkspace(Path.Combine(temporary.Path, "workspace"));
        var media = new FakeMediaTool();
        var asr = new FakeAsrClient();
        var llm = new FakeLlmClient { FailNextTranslation = true };
        var subtitles = new SrtSubtitlePublisher();
        var pipeline = new PipelineRunner(media, asr, llm, workspace, subtitles, subtitles);
        var batchRunner = new BatchRunner(pipeline, workspace);
        var output = Path.Combine(temporary.Path, "out", "sample.captioned.zh.bilingual.srt");
        var options = Options("zh-CN");
        var batch = await batchRunner.PrepareAsync(
            [new MediaInput(Path.Combine(temporary.Path, "sample.mp4"), "sample.mp4", "input-sha", output)],
            CancellationToken.None,
            options);

        Assert.Equal(options, (await workspace.LoadBatchAsync(batch.BatchId, CancellationToken.None))?.Options);

        var failed = await batchRunner.RunAsync(batch, options, CancellationToken.None);

        Assert.False(failed.Succeeded);
        Assert.Equal(1, media.ProbeCalls);
        Assert.Equal(1, media.ChunkCalls);
        Assert.Equal(1, asr.Calls);
        Assert.Equal(1, llm.BoundaryCalls);
        Assert.Equal(1, llm.CorrectionCalls);
        Assert.Equal(1, llm.TranslationCalls);
        Assert.False(File.Exists(output));
        var failedManifest = await workspace.LoadJobAsync(batch.Jobs[0].JobId, CancellationToken.None);
        Assert.Equal(StageStatus.RetryableFailed, failedManifest?.Stages[StageNames.Translate].Status);

        var resumed = await batchRunner.RunAsync(batch, options, CancellationToken.None);

        Assert.True(resumed.Succeeded);
        Assert.True(File.Exists(output));
        Assert.Equal(1, media.ProbeCalls);
        Assert.Equal(1, media.ChunkCalls);
        Assert.Equal(1, asr.Calls);
        Assert.Equal(1, llm.BoundaryCalls);
        Assert.Equal(1, llm.CorrectionCalls);
        Assert.Equal(2, llm.TranslationCalls);

        var fullyCached = await batchRunner.RunAsync(batch, options, CancellationToken.None);

        Assert.True(fullyCached.Succeeded);
        Assert.Equal(2, llm.TranslationCalls);

        var changedTarget = await batchRunner.RunAsync(batch, Options("fr"), CancellationToken.None);

        Assert.True(changedTarget.Succeeded);
        Assert.Equal(1, media.ProbeCalls);
        Assert.Equal(1, media.ChunkCalls);
        Assert.Equal(1, asr.Calls);
        Assert.Equal(1, llm.BoundaryCalls);
        Assert.Equal(1, llm.CorrectionCalls);
        Assert.Equal(3, llm.TranslationCalls);
        var manifest = await workspace.LoadJobAsync(batch.Jobs[0].JobId, CancellationToken.None);
        Assert.NotNull(manifest);
        Assert.All(manifest.Stages.Values, stage => Assert.Equal(StageStatus.Ready, stage.Status));
    }

    [Fact]
    public async Task Cleaning_one_batch_does_not_remove_another_batch_for_the_same_input()
    {
        using var temporary = new TemporaryDirectory();
        var workspace = new FileJobWorkspace(temporary.Path);
        var runner = new BatchRunner(null!, workspace);
        var input = new MediaInput("/media/a.mp4", "a.mp4", "same", "/output/a.srt");
        var first = await runner.PrepareAsync([input], CancellationToken.None);
        var second = await runner.PrepareAsync([input], CancellationToken.None);

        await workspace.CleanBatchAsync(first.BatchId, CancellationToken.None);

        Assert.Null(await workspace.LoadBatchAsync(first.BatchId, CancellationToken.None));
        Assert.NotNull(await workspace.LoadBatchAsync(second.BatchId, CancellationToken.None));
        Assert.Null(await workspace.LoadJobAsync(first.Jobs[0].JobId, CancellationToken.None));
        Assert.NotNull(await workspace.LoadJobAsync(second.Jobs[0].JobId, CancellationToken.None));
    }

    [Fact]
    public async Task Subtitle_input_skips_media_and_asr_and_exports_corrected_srt()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "talk.srt");
        await File.WriteAllTextAsync(source, """
            1
            00:00:00,000 --> 00:00:01,000
            Hello

            2
            00:00:01,000 --> 00:00:02,000
            World

            """);
        var output = Path.Combine(temporary.Path, "out", "talk.captioned.srt");
        var workspace = new FileJobWorkspace(Path.Combine(temporary.Path, "workspace"));
        var media = new FakeMediaTool();
        var asr = new FakeAsrClient();
        var llm = new FakeLlmClient();
        var subtitles = new SrtSubtitlePublisher();
        var pipeline = new PipelineRunner(media, asr, llm, workspace, subtitles, subtitles);
        var runner = new BatchRunner(pipeline, workspace);
        var options = Options("zh-CN") with { TargetLanguage = null, Layout = SubtitleLayout.Source, EnableSegmentation = false };

        var batch = await runner.PrepareAsync(
            [new MediaInput(source, "talk.srt", "srt-sha", output, SourceKind.Subtitle)],
            CancellationToken.None,
            options);
        var result = await runner.RunAsync(batch, options, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, media.ProbeCalls);
        Assert.Equal(0, media.ChunkCalls);
        Assert.Equal(0, asr.Calls);
        Assert.Equal(1, llm.CorrectionCalls);
        Assert.Equal(0, llm.TranslationCalls);
        Assert.True(File.Exists(output));
        Assert.Contains("Hello!", await File.ReadAllTextAsync(output));
    }

    private static PipelineOptions Options(string targetLanguage) => new(
        new EndpointProfile("https://asr.invalid/v1", "asr", "UNUSED", EndpointCapabilities.DefaultAsr),
        new EndpointProfile(
            "https://llm.invalid/v1",
            "llm",
            "UNUSED",
            new EndpointCapabilities(false, false, true, 0, TimeSpan.Zero)),
        SourceLanguage: "en",
        TargetLanguage: targetLanguage,
        Layout: SubtitleLayout.Bilingual,
        Overwrite: true);

    private sealed class FakeMediaTool : IMediaTool
    {
        public int ProbeCalls { get; private set; }
        public int ChunkCalls { get; private set; }

        public Task<MediaInfoArtifact> ProbeAsync(string inputPath, CancellationToken cancellationToken)
        {
            ProbeCalls++;
            return Task.FromResult(new MediaInfoArtifact(2_000, 100, true));
        }

        public async Task<MediaChunksArtifact> CreateChunksAsync(
            string inputPath,
            string workingDirectory,
            MediaInfoArtifact media,
            EndpointCapabilities capabilities,
            CancellationToken cancellationToken)
        {
            ChunkCalls++;
            Directory.CreateDirectory(workingDirectory);
            var path = Path.Combine(workingDirectory, "fake.flac");
            await File.WriteAllBytesAsync(path, [1, 2, 3], cancellationToken);
            return new([new MediaChunk(path, 0, media.DurationMs, 3, 0)]);
        }

        public Task<bool> VerifyChunksAsync(MediaChunksArtifact chunks, CancellationToken cancellationToken) =>
            Task.FromResult(chunks.Chunks.All(chunk => File.Exists(chunk.Path)));
    }

    private sealed class FakeAsrClient : IAsrClient
    {
        public int Calls { get; private set; }

        public Task<TranscriptDocument> TranscribeAsync(
            MediaChunk chunk,
            EndpointProfile profile,
            string? language,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TranscriptDocument("en", chunk.DurationMs,
            [
                new TimedAnchor("w1", "Hello", 0, 900, TimingOrigin.ProviderWord),
                new TimedAnchor("w2", "world", 900, 2_000, TimingOrigin.ProviderWord)
            ]));
        }

        public Task<string?> CheckAsync(EndpointProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(profile.Model);
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public bool FailNextTranslation { get; set; }
        public int BoundaryCalls { get; private set; }
        public int CorrectionCalls { get; private set; }
        public int TranslationCalls { get; private set; }

        public Task<IReadOnlyList<string>> SelectCueBoundariesAsync(
            IReadOnlyList<TimedAnchor> anchors,
            EndpointProfile profile,
            string? language,
            int maxCueCharactersCjk,
            int maxCueWordsLatin,
            long maxCueDurationMs,
            string? referenceText,
            CancellationToken cancellationToken)
        {
            BoundaryCalls++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<IReadOnlyDictionary<string, string>> CorrectAsync(
            IReadOnlyList<SubtitleCue> cues,
            EndpointProfile profile,
            string? language,
            string? referenceText,
            CancellationToken cancellationToken)
        {
            CorrectionCalls++;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                cues.ToDictionary(cue => cue.Id, cue => cue.SourceText + "!", StringComparer.Ordinal));
        }

        public Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            IReadOnlyList<SubtitleCue> cues,
            EndpointProfile profile,
            string targetLanguage,
            string? referenceText,
            CancellationToken cancellationToken)
        {
            TranslationCalls++;
            if (FailNextTranslation)
            {
                FailNextTranslation = false;
                throw new HttpRequestException("transient translation failure");
            }

            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                cues.ToDictionary(cue => cue.Id, cue => $"{targetLanguage}:{cue.SourceText}", StringComparer.Ordinal));
        }

        public Task<string?> CheckAsync(EndpointProfile profile, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(profile.Model);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "captioner-tests-" + Guid.NewGuid().ToString("N"));
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
