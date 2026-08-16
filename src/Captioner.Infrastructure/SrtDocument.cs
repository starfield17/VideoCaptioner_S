using System.Globalization;
using Captioner.Core;

namespace Captioner.Infrastructure;

/// <summary>Shared SRT parse/render used by import and atomic publication.</summary>
public static class SrtDocument
{
    public static string Render(SubtitleDocument document, SubtitleLayout layout)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < document.Cues.Count; index++)
        {
            var cue = document.Cues[index];
            builder.Append(index + 1).Append('\n');
            builder.Append(FormatTime(cue.StartMs)).Append(" --> ").Append(FormatTime(cue.EndMs)).Append('\n');
            var source = FirstLine(cue.SourceText);
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

    public static IReadOnlyList<SubtitleCue> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
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

            if (!int.TryParse(lines[lineIndex].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                number != cues.Count + 1)
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

            var source = textLines.Count == 0 ? string.Empty : FirstLine(textLines[0]);
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new InvalidDataException("SRT cue text cannot be empty.");
            }

            cues.Add(new($"c{number:D6}", start, end, source));
        }

        return cues;
    }

    public static string FormatTime(long milliseconds)
    {
        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        }

        var value = TimeSpan.FromMilliseconds(milliseconds);
        var hours = (long)value.TotalHours;
        return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }

    public static bool TryParseTime(string value, out long milliseconds)
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

    public static string FirstLine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        var separator = trimmed.IndexOf('\n');
        return separator < 0 ? trimmed : trimmed[..separator].Trim();
    }
}
