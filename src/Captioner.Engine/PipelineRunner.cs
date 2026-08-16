using Captioner.Core;

namespace Captioner.Engine;

public sealed class PipelineRunner(
    IMediaTool mediaTool,
    IAsrClient asrClient,
    ILlmClient llmClient,
    IJobWorkspace workspace,
    ISubtitlePublisher subtitlePublisher)
{
    private const int ProbeVersion = 1;
    private const int ChunkVersion = 2;
    private const int TranscribeVersion = 2;
    private const int SegmentVersion = 1;
    private const int CorrectVersion = 1;
    private const int TranslateVersion = 1;
    private const int ExportVersion = 1;

    public async Task<ExportArtifact> RunAsync(
        JobManifest manifest,
        PipelineOptions options,
        InferenceGate gate,
        CancellationToken cancellationToken)
    {
        var state = new JobExecutionState(manifest, workspace);

        var probeFingerprint = StageFingerprint.Create(
            StageNames.Probe,
            ProbeVersion,
            manifest.InputSha256,
            new { manifest.InputPath });
        var (media, probeArtifact) = await state.RunStageAsync(
            StageNames.Probe,
            probeFingerprint,
            ct => mediaTool.ProbeAsync(manifest.InputPath, ct),
            cancellationToken);

        if (!media.HasAudio)
        {
            throw new PipelineBlockedException("Input media has no audio stream.");
        }

        var chunksFingerprint = StageFingerprint.Create(
            StageNames.Chunks,
            ChunkVersion,
            probeArtifact.Sha256,
            new
            {
                options.Asr.Capabilities.MaxAudioBytes,
                MaxAudioDurationMs = (long)options.Asr.Capabilities.MaxAudioDuration.TotalMilliseconds
            });
        var (chunks, chunksArtifact) = await state.RunStageAsync(
            StageNames.Chunks,
            chunksFingerprint,
            ct => mediaTool.CreateChunksAsync(
                manifest.InputPath,
                workspace.GetJobDirectory(manifest.JobId),
                media,
                options.Asr.Capabilities,
                ct),
            cancellationToken,
            (artifact, ct) => mediaTool.VerifyChunksAsync(artifact, ct));

        if (!options.Asr.Capabilities.SegmentTimestamps && !options.Asr.Capabilities.WordTimestamps)
        {
            throw new PipelineBlockedException("ASR endpoint is not configured to return usable timestamps.");
        }

        var transcribeFingerprint = StageFingerprint.Create(
            StageNames.Transcribe,
            TranscribeVersion,
            chunksArtifact.Sha256,
            new
            {
                options.Asr.Backend,
                options.Asr.BaseUrl,
                options.Asr.Model,
                options.SourceLanguage,
                options.Asr.Capabilities.SegmentTimestamps,
                options.Asr.Capabilities.WordTimestamps
            });
        var (transcript, transcriptArtifact) = await state.RunStageAsync(
            StageNames.Transcribe,
            transcribeFingerprint,
            ct => TranscribeChunksAsync(chunks, media, options, gate, ct),
            cancellationToken);

        var segmentFingerprint = StageFingerprint.Create(
            StageNames.Segment,
            SegmentVersion,
            transcriptArtifact.Sha256,
            new
            {
                options.EnableSegmentation,
                options.MaxCueCharacters,
                options.MaxCueDurationMs,
                LlmBaseUrl = options.EnableSegmentation ? options.Llm.BaseUrl : null,
                LlmModel = options.EnableSegmentation ? options.Llm.Model : null,
                PromptVersion = 2
            });
        var (segmented, segmentedArtifact) = await state.RunStageAsync(
            StageNames.Segment,
            segmentFingerprint,
            ct => SegmentAsync(transcript, options, gate, ct),
            cancellationToken);

        var correctFingerprint = StageFingerprint.Create(
            StageNames.Correct,
            CorrectVersion,
            segmentedArtifact.Sha256,
            new
            {
                options.EnableCorrection,
                LlmBaseUrl = options.EnableCorrection ? options.Llm.BaseUrl : null,
                LlmModel = options.EnableCorrection ? options.Llm.Model : null,
                PromptVersion = 2
            });
        var (corrected, correctedArtifact) = await state.RunStageAsync(
            StageNames.Correct,
            correctFingerprint,
            ct => CorrectAsync(segmented, options, gate, ct),
            cancellationToken);

        var translateFingerprint = StageFingerprint.Create(
            StageNames.Translate,
            TranslateVersion,
            correctedArtifact.Sha256,
            new
            {
                options.TargetLanguage,
                LlmBaseUrl = options.TargetLanguage is not null ? options.Llm.BaseUrl : null,
                LlmModel = options.TargetLanguage is not null ? options.Llm.Model : null,
                PromptVersion = 2
            });
        var (translated, translatedArtifact) = await state.RunStageAsync(
            StageNames.Translate,
            translateFingerprint,
            ct => TranslateAsync(corrected, options, gate, ct),
            cancellationToken);

        var exportFingerprint = StageFingerprint.Create(
            StageNames.Export,
            ExportVersion,
            translatedArtifact.Sha256,
            new { options.Layout, manifest.OutputPath });
        var (exported, _) = await state.RunStageAsync(
            StageNames.Export,
            exportFingerprint,
            ct => subtitlePublisher.PublishAsync(
                translated,
                manifest.OutputPath,
                options.Layout,
                options.Overwrite,
                ct),
            cancellationToken,
            (artifact, ct) => subtitlePublisher.VerifyAsync(artifact, ct));

        return exported;
    }

    private async Task<TranscriptDocument> TranscribeChunksAsync(
        MediaChunksArtifact chunks,
        MediaInfoArtifact media,
        PipelineOptions options,
        InferenceGate gate,
        CancellationToken cancellationToken)
    {
        var anchors = new List<TimedAnchor>();
        string? detectedLanguage = options.SourceLanguage;

        foreach (var chunk in chunks.Chunks.OrderBy(chunk => chunk.Index))
        {
            var response = await gate.RunAsrAsync(
                () => asrClient.TranscribeAsync(chunk, options.Asr, options.SourceLanguage, cancellationToken),
                cancellationToken);
            detectedLanguage ??= response.Language;

            anchors.AddRange(response.Anchors.Select(anchor => new TimedAnchor(
                $"p{chunk.Index:D4}.{anchor.Id}",
                anchor.Text,
                anchor.StartMs + chunk.OffsetMs,
                anchor.EndMs + chunk.OffsetMs,
                anchor.Origin)));
        }

        var ordered = anchors.OrderBy(anchor => anchor.StartMs).ToArray();
        var validation = TimelineValidator.ValidateAnchors(ordered, media.DurationMs);
        if (!validation.IsValid)
        {
            throw new PipelineBlockedException(
                "ASR timeline is invalid: " + string.Join("; ", validation.Issues.Select(issue => issue.Message)));
        }

        return new(detectedLanguage, media.DurationMs, ordered);
    }

    private async Task<SubtitleDocument> SegmentAsync(
        TranscriptDocument transcript,
        PipelineOptions options,
        InferenceGate gate,
        CancellationToken cancellationToken)
    {
        var anchors = AnchorEstimator.EnsureSplittableAnchors(transcript.Anchors);
        IReadOnlyList<string> boundaries = [];

        if (options.EnableSegmentation && anchors.Count > 0)
        {
            boundaries = await gate.RunLlmAsync(
                () => llmClient.SelectCueBoundariesAsync(
                    anchors,
                    options.Llm,
                    transcript.Language,
                    options.MaxCueCharacters,
                    options.MaxCueDurationMs,
                    cancellationToken),
                cancellationToken);
        }

        var cues = CueSegmenter.BuildCues(
            anchors,
            boundaries,
            options.MaxCueCharacters,
            options.MaxCueDurationMs);
        EnsureValidTimeline(cues, transcript.MediaDurationMs, "segmentation");
        return new(transcript.Language, null, transcript.MediaDurationMs, cues);
    }

    private async Task<SubtitleDocument> CorrectAsync(
        SubtitleDocument document,
        PipelineOptions options,
        InferenceGate gate,
        CancellationToken cancellationToken)
    {
        if (!options.EnableCorrection || document.Cues.Count == 0)
        {
            return document;
        }

        var values = await gate.RunLlmAsync(
            () => llmClient.CorrectAsync(
                document.Cues,
                options.Llm,
                document.SourceLanguage,
                cancellationToken),
            cancellationToken);
        EnsureExactCueMapping(document.Cues, values, "correction");

        var cues = document.Cues.Select(cue => cue with
        {
            SourceText = values[cue.Id].Trim()
        }).ToArray();
        EnsureValidTimeline(cues, document.MediaDurationMs, "correction");
        return document with { Cues = cues };
    }

    private async Task<SubtitleDocument> TranslateAsync(
        SubtitleDocument document,
        PipelineOptions options,
        InferenceGate gate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.TargetLanguage) || document.Cues.Count == 0)
        {
            return document;
        }

        var values = await gate.RunLlmAsync(
            () => llmClient.TranslateAsync(
                document.Cues,
                options.Llm,
                options.TargetLanguage,
                cancellationToken),
            cancellationToken);
        EnsureExactCueMapping(document.Cues, values, "translation");

        return document with
        {
            TargetLanguage = options.TargetLanguage,
            Cues = document.Cues.Select(cue => cue with
            {
                TranslatedText = values[cue.Id].Trim()
            }).ToArray()
        };
    }

    private static void EnsureExactCueMapping(
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlyDictionary<string, string> values,
        string stage)
    {
        var expected = cues.Select(cue => cue.Id).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(values.Keys) || values.Any(pair => string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw new PipelineBlockedException($"LLM {stage} response did not contain exactly one non-empty value per cue ID.");
        }
    }

    private static void EnsureValidTimeline(IReadOnlyList<SubtitleCue> cues, long durationMs, string stage)
    {
        var validation = TimelineValidator.ValidateCues(cues, durationMs);
        if (!validation.IsValid)
        {
            throw new PipelineBlockedException(
                $"Timeline failed after {stage}: " + string.Join("; ", validation.Issues.Select(issue => issue.Message)));
        }
    }

    private sealed class JobExecutionState(JobManifest manifest, IJobWorkspace workspace)
    {
        private JobManifest _manifest = manifest;

        public async Task<(T Value, ArtifactReference Artifact)> RunStageAsync<T>(
            string stageName,
            string fingerprint,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken,
            Func<T, CancellationToken, Task<bool>>? reuseValidator = null)
        {
            if (_manifest.Stages.TryGetValue(stageName, out var existing) &&
                existing.Status == StageStatus.Ready &&
                existing.Fingerprint == fingerprint &&
                existing.Artifact is not null &&
                await workspace.VerifyArtifactAsync(_manifest.JobId, existing.Artifact, cancellationToken))
            {
                var cached = await workspace.ReadArtifactAsync<T>(
                    _manifest.JobId,
                    existing.Artifact,
                    cancellationToken);
                if (reuseValidator is null || await reuseValidator(cached, cancellationToken))
                {
                    return (cached, existing.Artifact);
                }
            }

            if (existing is { Status: StageStatus.Ready })
            {
                await UpdateStageAsync(existing with
                {
                    Status = StageStatus.Invalidated,
                    Diagnostic = "Stage fingerprint or artifact verification changed.",
                    CompletedAt = null
                }, cancellationToken);
            }

            await UpdateStageAsync(new(
                stageName,
                StageStatus.Running,
                fingerprint,
                StartedAt: DateTimeOffset.UtcNow), cancellationToken);

            try
            {
                var value = await operation(cancellationToken);
                var artifact = await workspace.WriteArtifactAsync(
                    _manifest.JobId,
                    $"{stageName}.json",
                    value,
                    cancellationToken);
                await UpdateStageAsync(new(
                    stageName,
                    StageStatus.Ready,
                    fingerprint,
                    artifact,
                    StartedAt: DateTimeOffset.UtcNow,
                    CompletedAt: DateTimeOffset.UtcNow), cancellationToken);
                return (value, artifact);
            }
            catch (OperationCanceledException)
            {
                await UpdateStageAsync(new(
                    stageName,
                    StageStatus.Pending,
                    fingerprint,
                    Diagnostic: "Cancelled before stage commit."), CancellationToken.None);
                throw;
            }
            catch (PipelineBlockedException exception)
            {
                await UpdateStageAsync(new(
                    stageName,
                    StageStatus.Blocked,
                    fingerprint,
                    ErrorCategory: "blocked",
                    Diagnostic: exception.Message), CancellationToken.None);
                throw;
            }
            catch (Exception exception) when (IsNonRetryable(exception))
            {
                await UpdateStageAsync(new(
                    stageName,
                    StageStatus.Blocked,
                    fingerprint,
                    ErrorCategory: exception.GetType().Name,
                    Diagnostic: exception.Message), CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                await UpdateStageAsync(new(
                    stageName,
                    StageStatus.RetryableFailed,
                    fingerprint,
                    ErrorCategory: exception.GetType().Name,
                    Diagnostic: exception.Message), CancellationToken.None);
                throw;
            }
        }

        private static bool IsNonRetryable(Exception exception)
        {
            if (exception is ArgumentException or InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
            {
                return true;
            }

            if (exception is InvalidOperationException invalidOperation &&
                (invalidOperation.Message.Contains("environment variable", StringComparison.OrdinalIgnoreCase) ||
                 invalidOperation.Message.Contains("installed", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (exception is HttpRequestException { StatusCode: { } status })
            {
                var code = (int)status;
                return code < 500 && code is not 408 and not 409 and not 429;
            }

            return false;
        }

        private async Task UpdateStageAsync(StageRecord record, CancellationToken cancellationToken)
        {
            var stages = new Dictionary<string, StageRecord>(_manifest.Stages, StringComparer.Ordinal)
            {
                [record.Name] = record
            };
            _manifest = _manifest with
            {
                Stages = stages,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await workspace.SaveJobAsync(_manifest, cancellationToken);
        }
    }
}
