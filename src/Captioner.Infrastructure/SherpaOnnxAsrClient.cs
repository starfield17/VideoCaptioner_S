using System.Collections.Concurrent;
using Captioner.Core;
using Captioner.Engine;
using SherpaOnnx;

namespace Captioner.Infrastructure;

/// <summary>In-process CPU ASR through sherpa-onnx, with Silero VAD providing segment timestamps.</summary>
public sealed class SherpaOnnxAsrClient : IAsrClient, IDisposable
{
    private readonly AsrModelManager _models;
    private readonly ConcurrentDictionary<string, Lazy<ModelRuntime>> _runtimes = new(StringComparer.Ordinal);
    private bool _disposed;

    public SherpaOnnxAsrClient(string? modelDirectory = null)
    {
        _models = new AsrModelManager(modelDirectory);
    }

    public async Task<TranscriptDocument> TranscribeAsync(
        MediaChunk chunk,
        EndpointProfile profile,
        string? language,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(profile);
        if (!File.Exists(chunk.Path))
        {
            throw new FileNotFoundException("The normalized audio chunk was not found.", chunk.Path);
        }

        var model = AsrModelCatalog.Get(profile.Model);
        if (!_models.IsInstalled(model.Id))
        {
            throw new InvalidOperationException(
                $"Local ASR model '{model.Id}' is not installed. Run 'captioner models pull {model.Id}' first.");
        }

        var runtime = GetRuntime(model, language);
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Decode(chunk, model, runtime, language, cancellationToken), CancellationToken.None);
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    public Task<string?> CheckAsync(EndpointProfile profile, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var model = AsrModelCatalog.Get(profile.Model);
        if (!_models.IsInstalled(model.Id))
        {
            throw new InvalidOperationException(
                $"Local ASR model '{model.Id}' is not installed. Run 'captioner models pull {model.Id}' first.");
        }

        _ = GetRuntime(model, language: null);
        return Task.FromResult<string?>(model.Id);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var runtime in _runtimes.Values.Where(value => value.IsValueCreated).Select(value => value.Value))
        {
            runtime.Dispose();
        }

        _models.Dispose();
    }

    private ModelRuntime GetRuntime(AsrModelDefinition model, string? language)
    {
        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? string.Empty : language.Trim().ToLowerInvariant();
        var key = model.Id + "|" + normalizedLanguage;
        return _runtimes.GetOrAdd(key, _ => new(() => CreateRuntime(model, normalizedLanguage),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private ModelRuntime CreateRuntime(AsrModelDefinition model, string language)
    {
        var directory = _models.GetModelDirectory(model.Id);
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        if (model.Family == AsrModelFamily.Whisper)
        {
            config.ModelConfig.Tokens = Path.Combine(directory, model.ModelStem + "-tokens.txt");
            config.ModelConfig.Whisper.Encoder = Path.Combine(directory, model.ModelStem + "-encoder.int8.onnx");
            config.ModelConfig.Whisper.Decoder = Path.Combine(directory, model.ModelStem + "-decoder.int8.onnx");
            config.ModelConfig.Whisper.Language = language;
            config.ModelConfig.Whisper.Task = "transcribe";
            config.ModelConfig.Whisper.EnableTokenTimestamps = 0;
            config.ModelConfig.Whisper.EnableSegmentTimestamps = 0;
        }
        else
        {
            config.ModelConfig.Tokens = string.Empty;
            config.ModelConfig.Qwen3Asr.ConvFrontend = Path.Combine(directory, "conv_frontend.onnx");
            config.ModelConfig.Qwen3Asr.Encoder = Path.Combine(directory, "encoder.int8.onnx");
            config.ModelConfig.Qwen3Asr.Decoder = Path.Combine(directory, "decoder.int8.onnx");
            config.ModelConfig.Qwen3Asr.Tokenizer = Path.Combine(directory, "tokenizer");
            config.ModelConfig.Qwen3Asr.Hotwords = string.Empty;
        }

        return new(new OfflineRecognizer(config), Path.Combine(directory, "silero_vad.onnx"));
    }

    private static TranscriptDocument Decode(
        MediaChunk chunk,
        AsrModelDefinition model,
        ModelRuntime runtime,
        string? language,
        CancellationToken cancellationToken)
    {
        var reader = PcmWaveData.Read(chunk.Path);
        if (reader.SampleRate != 16_000)
        {
            throw new InvalidDataException($"Local ASR requires 16 kHz PCM WAV; received {reader.SampleRate} Hz.");
        }

        var vadConfig = new VadModelConfig();
        vadConfig.SileroVad.Model = runtime.VadModelPath;
        vadConfig.SileroVad.Threshold = 0.35F;
        vadConfig.SileroVad.MinSilenceDuration = 0.45F;
        vadConfig.SileroVad.MinSpeechDuration = 0.25F;
        vadConfig.SileroVad.MaxSpeechDuration = model.Family == AsrModelFamily.Whisper ? 20.0F : 12.0F;
        vadConfig.SileroVad.WindowSize = 512;
        vadConfig.Debug = 0;

        using var vad = new VoiceActivityDetector(vadConfig, 60);
        var anchors = new List<TimedAnchor>();
        var windowSize = vadConfig.SileroVad.WindowSize;
        var offset = 0;
        while (offset + windowSize <= reader.Samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = new float[windowSize];
            Array.Copy(reader.Samples, offset, window, 0, windowSize);
            vad.AcceptWaveform(window);
            DecodeReadySegments(vad, runtime.Recognizer, reader.SampleRate, chunk.DurationMs, anchors, cancellationToken);
            offset += windowSize;
        }

        if (offset < reader.Samples.Length)
        {
            var tail = new float[reader.Samples.Length - offset];
            Array.Copy(reader.Samples, offset, tail, 0, tail.Length);
            vad.AcceptWaveform(tail);
        }

        vad.Flush();
        DecodeReadySegments(vad, runtime.Recognizer, reader.SampleRate, chunk.DurationMs, anchors, cancellationToken);

        var validation = TimelineValidator.ValidateAnchors(anchors, chunk.DurationMs);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                "Local ASR produced an invalid timeline: " +
                string.Join("; ", validation.Issues.Select(issue => issue.Message)));
        }

        return new(language, chunk.DurationMs, anchors);
    }

    private static void DecodeReadySegments(
        VoiceActivityDetector vad,
        OfflineRecognizer recognizer,
        int sampleRate,
        long mediaDurationMs,
        ICollection<TimedAnchor> anchors,
        CancellationToken cancellationToken)
    {
        while (!vad.IsEmpty())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = vad.Front();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, segment.Samples);
            recognizer.Decode(stream);
            var text = stream.Result.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var startMs = checked((long)Math.Round(segment.Start * 1000d / sampleRate, MidpointRounding.AwayFromZero));
                var durationMs = checked((long)Math.Round(segment.Samples.Length * 1000d / sampleRate, MidpointRounding.AwayFromZero));
                if (startMs < mediaDurationMs)
                {
                    anchors.Add(new(
                        $"s{anchors.Count:D6}",
                        text,
                        startMs,
                        Math.Min(mediaDurationMs, checked(startMs + Math.Max(1, durationMs))),
                        TimingOrigin.ProviderSegment));
                }
            }

            vad.Pop();
        }
    }

    private sealed class ModelRuntime(OfflineRecognizer recognizer, string vadModelPath) : IDisposable
    {
        public OfflineRecognizer Recognizer { get; } = recognizer;
        public string VadModelPath { get; } = vadModelPath;
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void Dispose()
        {
            Gate.Dispose();
            Recognizer.Dispose();
        }
    }
}
