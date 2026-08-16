using System.Security.Cryptography;
using Captioner.Core;
using Captioner.Engine;

namespace Captioner.Infrastructure;

/// <summary>Validating, atomically published SRT sidecar writer and importer.</summary>
public sealed class SrtSubtitlePublisher : ISubtitlePublisher, ISubtitleImporter
{
    public async Task<SubtitleDocument> ImportAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Subtitle file was not found.", fullPath);
        }

        var parsed = SrtDocument.Parse(await File.ReadAllTextAsync(fullPath, cancellationToken));
        if (parsed.Count == 0)
        {
            throw new InvalidDataException("The subtitle file does not contain any cues.");
        }

        var duration = parsed[^1].EndMs;
        var validation = TimelineValidator.ValidateCues(parsed, duration);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                "Imported subtitle timeline is invalid: " +
                string.Join("; ", validation.Issues.Select(issue => issue.Message)));
        }

        return new(null, null, duration, parsed);
    }

    public async Task<ExportArtifact> PublishAsync(
        SubtitleDocument document,
        string outputPath,
        SubtitleLayout layout,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ValidateDocument(document, layout);

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Output path has no directory.");
        Directory.CreateDirectory(directory);
        if (!overwrite && File.Exists(fullPath))
        {
            throw new PipelineBlockedException($"Output file already exists: {fullPath}");
        }

        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var text = SrtDocument.Render(document, layout);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096, leaveOpen: true))
            {
                await writer.WriteAsync(text.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            var parsed = SrtDocument.Parse(await File.ReadAllTextAsync(temporaryPath, cancellationToken));
            if (parsed.Count != document.Cues.Count || !TimelineValidator.ValidateCues(parsed, document.MediaDurationMs).IsValid)
            {
                throw new InvalidDataException("The staged SRT failed independent parser validation.");
            }

            File.Move(temporaryPath, fullPath, overwrite);
            var hash = await HashFileAsync(fullPath, cancellationToken);
            return new(fullPath, hash, document.Cues.Count);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<bool> VerifyAsync(ExportArtifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!File.Exists(artifact.OutputPath))
        {
            return false;
        }

        try
        {
            var parsed = SrtDocument.Parse(await File.ReadAllTextAsync(artifact.OutputPath, cancellationToken));
            if (parsed.Count != artifact.CueCount || !TimelineValidator.ValidateCues(parsed, long.MaxValue).IsValid)
            {
                return false;
            }

            var hash = await HashFileAsync(artifact.OutputPath, cancellationToken);
            return string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void ValidateDocument(SubtitleDocument document, SubtitleLayout layout)
    {
        var timeline = TimelineValidator.ValidateCues(document.Cues, document.MediaDurationMs);
        if (!timeline.IsValid)
        {
            throw new InvalidDataException(
                "Subtitle timeline is invalid: " + string.Join("; ", timeline.Issues.Select(issue => issue.Message)));
        }

        if ((layout == SubtitleLayout.Target ||
             layout == SubtitleLayout.Bilingual && !string.IsNullOrWhiteSpace(document.TargetLanguage)) &&
            document.Cues.Any(cue => string.IsNullOrWhiteSpace(cue.TranslatedText)))
        {
            throw new InvalidDataException("Translated SRT output requires translated text for every cue.");
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
