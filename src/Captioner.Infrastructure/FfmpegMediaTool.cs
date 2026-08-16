using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Captioner.Core;
using Captioner.Engine;

namespace Captioner.Infrastructure;

/// <summary>FFprobe/FFmpeg media adapter. It only reads the input and writes job-local audio files.</summary>
public sealed class FfmpegMediaTool : IMediaTool
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly TimeSpan _processTimeout;

    public FfmpegMediaTool(
        string ffmpegPath = "ffmpeg",
        string ffprobePath = "ffprobe",
        TimeSpan? processTimeout = null)
    {
        _ffmpegPath = ResolveExecutable(string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath, "ffmpeg");
        _ffprobePath = ResolveExecutable(string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath, "ffprobe");
        _processTimeout = processTimeout ?? TimeSpan.FromHours(2);
    }

    /// <summary>Checks both executables without requiring a media input.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var ffmpeg = await RunProcessAsync(_ffmpegPath, ["-version"], cancellationToken);
        if (ffmpeg.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg check failed: {Trim(ffmpeg.StandardError)}");
        }

        var ffprobe = await RunProcessAsync(_ffprobePath, ["-version"], cancellationToken);
        if (ffprobe.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe check failed: {Trim(ffprobe.StandardError)}");
        }
    }

    public async Task<MediaInfoArtifact> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input media was not found.", inputPath);
        }

        var result = await RunProcessAsync(
            _ffprobePath,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", "--", inputPath],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException($"ffprobe failed for '{inputPath}': {Trim(result.StandardError)}");
        }

        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object ||
            !format.TryGetProperty("duration", out var durationValue))
        {
            throw new InvalidDataException("ffprobe did not return media duration.");
        }

        var durationSeconds = ParseDouble(durationValue, "format.duration");
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0 || durationSeconds > long.MaxValue / 1000d)
        {
            throw new InvalidDataException("ffprobe returned an invalid media duration.");
        }

        var hasAudio = root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array &&
            streams.EnumerateArray().Any(stream =>
                stream.ValueKind == JsonValueKind.Object &&
                stream.TryGetProperty("codec_type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "audio", StringComparison.OrdinalIgnoreCase));

        return new(
            checked((long)Math.Round(durationSeconds * 1000d, MidpointRounding.AwayFromZero)),
            new FileInfo(inputPath).Length,
            hasAudio);
    }

    public async Task<MediaChunksArtifact> CreateChunksAsync(
        string inputPath,
        string workingDirectory,
        MediaInfoArtifact media,
        EndpointCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input media was not found.", inputPath);
        }

        Directory.CreateDirectory(workingDirectory);
        var normalizedPath = Path.Combine(workingDirectory, "normalized.wav");
        await RunFfmpegAsync(
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", inputPath, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", normalizedPath],
            cancellationToken);

        var normalizedSize = new FileInfo(normalizedPath).Length;
        var maxDurationMs = capabilities.MaxAudioDuration > TimeSpan.Zero
            ? checked((long)Math.Floor(capabilities.MaxAudioDuration.TotalMilliseconds))
            : long.MaxValue;
        var maxBytes = capabilities.MaxAudioBytes > 0 ? capabilities.MaxAudioBytes : long.MaxValue;
        var fits = media.DurationMs <= maxDurationMs && normalizedSize <= maxBytes;
        if (fits)
        {
            return new([new MediaChunk(normalizedPath, 0, media.DurationMs, normalizedSize, 0)]);
        }

        var initialDuration = Math.Min(media.DurationMs, maxDurationMs);
        if (initialDuration <= 0)
        {
            throw new InvalidDataException("The ASR endpoint does not allow a positive audio duration.");
        }

        // Compression varies by source, so use the whole-file ratio only as a starting
        // point, then verify each generated chunk against the endpoint byte limit.
        if (normalizedSize > maxBytes && media.DurationMs > 0 && maxBytes < long.MaxValue)
        {
            initialDuration = Math.Min(initialDuration, Math.Max(1, media.DurationMs * maxBytes / normalizedSize));
        }

        var chunks = new List<MediaChunk>();
        var offset = 0L;
        var index = 0;
        while (offset < media.DurationMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = Math.Min(initialDuration, media.DurationMs - offset);
            var outputPath = Path.Combine(workingDirectory, $"chunk-{index:D4}.wav");
            while (true)
            {
                await RunFfmpegAsync(
                    ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", FormatSeconds(offset), "-i", normalizedPath, "-t", FormatSeconds(duration), "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", outputPath],
                    cancellationToken);
                var size = new FileInfo(outputPath).Length;
                if (size <= maxBytes || duration <= 1_000)
                {
                    if (size > maxBytes)
                    {
                        throw new InvalidDataException(
                            $"The ASR byte limit ({maxBytes}) is smaller than a one-second normalized audio chunk.");
                    }

                    chunks.Add(new(outputPath, offset, duration, size, index));
                    break;
                }

                duration = Math.Max(1_000, duration / 2);
            }

            offset = checked(offset + duration);
            index++;
        }

        if (chunks.Count == 0)
        {
            throw new InvalidDataException("FFmpeg did not produce any audio chunks.");
        }

        return new(chunks);
    }

    public Task<bool> VerifyChunksAsync(MediaChunksArtifact chunks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(chunks.Chunks.Count > 0 && chunks.Chunks.All(chunk =>
            File.Exists(chunk.Path) && new FileInfo(chunk.Path).Length == chunk.SizeBytes));
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(_ffmpegPath, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException($"ffmpeg failed: {Trim(result.StandardError)}");
        }
    }

    private static string ResolveExecutable(string configuredPath, string executableName)
    {
        if (!string.Equals(configuredPath, executableName, StringComparison.OrdinalIgnoreCase))
        {
            return configuredPath;
        }

        var fileName = OperatingSystem.IsWindows() ? executableName + ".exe" : executableName;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", fileName),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        };
        return candidates.FirstOrDefault(File.Exists) ?? configuredPath;
    }

    private async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Unable to start '{executable}'.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"Unable to start '{executable}'. Is it installed and on PATH?", exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_processTimeout);
        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(standardOutput, standardError);
            return new(process.ExitCode, standardOutput.Result, standardError.Result);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process may have exited between HasExited and Kill.
            }

            throw;
        }
    }

    private static string FormatSeconds(long milliseconds) =>
        (milliseconds / 1000d).ToString("0.###", CultureInfo.InvariantCulture);

    private static double ParseDouble(JsonElement element, string name)
    {
        var value = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text) => text,
            _ => double.NaN
        };
        if (double.IsNaN(value))
        {
            throw new InvalidDataException($"ffprobe field '{name}' must be numeric.");
        }

        return value;
    }

    private static string Trim(string value)
    {
        var normalized = value.Trim();
        return normalized.Length <= 1000 ? normalized : normalized[..1000] + "…";
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
