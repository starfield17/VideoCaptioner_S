using Captioner.Core;

namespace Captioner.Infrastructure;

/// <summary>Presentation-independent naming for mirrored sidecar subtitle output.</summary>
public static class SubtitleOutputPlanner
{
    public static string CreatePath(
        string relativeInputPath,
        string outputDirectory,
        string? targetLanguage,
        SubtitleLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeInputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var nativeRelativePath = relativeInputPath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(nativeRelativePath) ||
            nativeRelativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part is "." or ".."))
        {
            throw new ArgumentException("Input path must be a safe relative path.", nameof(relativeInputPath));
        }

        var relativeDirectory = Path.GetDirectoryName(nativeRelativePath);
        var stem = Path.GetFileNameWithoutExtension(nativeRelativePath);
        var language = string.IsNullOrWhiteSpace(targetLanguage)
            ? string.Empty
            : "." + SanitizeFileToken(targetLanguage);
        var layoutSuffix = layout == SubtitleLayout.Source
            ? string.Empty
            : "." + layout.ToString().ToLowerInvariant();
        var fileName = stem + ".captioned" + language + layoutSuffix + ".srt";
        var root = Path.GetFullPath(outputDirectory);
        var output = Path.GetFullPath(Path.Combine(root, relativeDirectory ?? string.Empty, fileName));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!output.StartsWith(rootPrefix, comparison))
        {
            throw new ArgumentException("Output path escapes the output directory.", nameof(relativeInputPath));
        }

        return output;
    }

    private static string SanitizeFileToken(string value)
    {
        var normalized = new string(value.Trim().Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "translated" : normalized;
    }
}
