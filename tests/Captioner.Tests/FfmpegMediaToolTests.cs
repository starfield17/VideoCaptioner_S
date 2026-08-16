using Captioner.Infrastructure;

namespace Captioner.Tests;

public sealed class FfmpegMediaToolTests
{
    [Fact]
    public async Task Probe_missing_input_throws_file_not_found()
    {
        var tool = new FfmpegMediaTool();
        var missing = Path.Combine(Path.GetTempPath(), "captioner-missing-" + Guid.NewGuid().ToString("N") + ".wav");

        await Assert.ThrowsAsync<FileNotFoundException>(() => tool.ProbeAsync(missing, CancellationToken.None));
    }

    [Fact]
    public async Task Check_with_missing_binaries_explains_the_failure()
    {
        var tool = new FfmpegMediaTool(
            Path.Combine(Path.GetTempPath(), "no-ffmpeg-" + Guid.NewGuid().ToString("N")),
            Path.Combine(Path.GetTempPath(), "no-ffprobe-" + Guid.NewGuid().ToString("N")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.CheckAsync(CancellationToken.None));
        Assert.Contains("ffmpeg", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_reads_duration_when_ffmpeg_is_available()
    {
        if (!HasOnPath("ffmpeg") || !HasOnPath("ffprobe"))
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "captioner-ffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var wav = Path.Combine(directory, "tone.wav");
        try
        {
            WriteSilentWav(wav, milliseconds: 250);
            var info = await new FfmpegMediaTool().ProbeAsync(wav, CancellationToken.None);
            Assert.True(info.HasAudio);
            Assert.InRange(info.DurationMs, 200, 400);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool HasOnPath(string name)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => extensions.Select(extension => Path.Combine(directory, name + extension)))
            .Any(File.Exists);
    }

    private static void WriteSilentWav(string path, int milliseconds)
    {
        const int sampleRate = 16_000;
        var samples = sampleRate * milliseconds / 1000;
        var dataBytes = samples * 2;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
    }
}
