using System.Globalization;
using System.Security.Cryptography;
using Captioner.Core;
using Captioner.Engine;

namespace Captioner.Infrastructure;

/// <summary>Validating, atomically published SRT sidecar writer.</summary>
public sealed class SrtSubtitlePublisher : ISubtitlePublisher
{
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
            var text = Render(document, layout);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096, leaveOpen: true))
            {
                await writer.WriteAsync(text.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            var parsed = await ParseAsync(temporaryPath, cancellationToken);
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
            var parsed = await ParseAsync(artifact.OutputPath, cancellationToken);
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

    private static string Render(SubtitleDocument document, SubtitleLayout layout)
    {
        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < document.Cues.Count; index++)
        {
            var cue = document.Cues[index];
            builder.Append(index + 1).Append('\n');
            builder.Append(FormatTime(cue.StartMs)).Append(" --> ").Append(FormatTime(cue.EndMs)).Append('\n');
            var source = cue.SourceText.Trim();
            var target = cue.TranslatedText?.Trim();
            switch (layout)
            {
                case SubtitleLayout.Source:
                    builder.Append(source);
                    break;
                case SubtitleLayout.Target:
                    builder.Append(target);
                    break;
                case SubtitleLayout.Bilingual:
                    builder.Append(source);
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        builder.Append('\n').Append(target);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown subtitle layout.");
            }

            builder.Append("\n\n");
        }

        return builder.ToString();
    }

    private static async Task<IReadOnlyList<SubtitleCue>> ParseAsync(string path, CancellationToken cancellationToken)
    {
        var input = await File.ReadAllTextAsync(path, cancellationToken);
        var lines = input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var cues = new List<SubtitleCue>();
        var lineIndex = 0;
        while (lineIndex < lines.Length)
        {
            while (lineIndex < lines.Length && string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                lineIndex++;
            }

            if (lineIndex >= lines.Length)
            {
                break;
            }

            if (!int.TryParse(lines[lineIndex].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number != cues.Count + 1)
            {
                throw new InvalidDataException("SRT cue numbers must be contiguous.");
            }

            lineIndex++;
            if (lineIndex >= lines.Length)
            {
                throw new InvalidDataException("SRT cue is missing its timing line.");
            }

            var timing = lines[lineIndex].Split(" --> ", StringSplitOptions.None);
            if (timing.Length != 2 || !TryParseTime(timing[0], out var start) || !TryParseTime(timing[1], out var end))
            {
                throw new InvalidDataException("SRT cue has an invalid timing line.");
            }

            lineIndex++;
            var textLines = new List<string>();
            while (lineIndex < lines.Length && !string.IsNullOrWhiteSpace(lines[lineIndex]))
            {
                textLines.Add(lines[lineIndex]);
                lineIndex++;
            }

            var text = string.Join('\n', textLines).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException("SRT cue text cannot be empty.");
            }

            cues.Add(new($"c{number:D6}", start, end, text));
        }

        return cues;
    }

    private static string FormatTime(long milliseconds)
    {
        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        var value = TimeSpan.FromMilliseconds(milliseconds);
        var hours = (long)value.TotalHours;
        return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }

    private static bool TryParseTime(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Trim().Split([':', ','], StringSplitOptions.None);
        if (parts.Length != 4 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var fraction) ||
            hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or > 59 || fraction is < 0 or > 999)
        {
            return false;
        }

        milliseconds = checked(((hours * 60L + minutes) * 60 + seconds) * 1000 + fraction);
        return true;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
