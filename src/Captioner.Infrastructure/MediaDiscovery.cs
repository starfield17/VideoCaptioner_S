using System.Security.Cryptography;
using Captioner.Core;
using Captioner.Engine;

namespace Captioner.Infrastructure;

/// <summary>Discovers supported media and creates deterministic mirrored SRT output plans.</summary>
public static class MediaDiscovery
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".avi", ".flac", ".m2ts", ".m4a", ".m4v", ".mkv", ".mov", ".mp3", ".mp4",
        ".ogg", ".opus", ".srt", ".ts", ".wav", ".webm", ".wma"
    };

    public static IReadOnlySet<string> SupportedExtensions => Extensions;

    public static IReadOnlyList<MediaInput> Discover(
        string inputPath,
        string outputDirectory,
        string outputExtension = ".srt") =>
        DiscoverAsync(inputPath, outputDirectory, outputExtension).GetAwaiter().GetResult();

    public static async Task<IReadOnlyList<MediaInput>> DiscoverAsync(
        string inputPath,
        string outputDirectory,
        string outputExtension = ".srt",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (string.IsNullOrWhiteSpace(outputExtension) || !outputExtension.StartsWith(".", StringComparison.Ordinal) ||
            outputExtension.Contains(Path.DirectorySeparatorChar) || outputExtension.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Output extension must begin with '.'.", nameof(outputExtension));
        }

        var fullInput = Path.GetFullPath(inputPath);
        var fullOutput = Path.GetFullPath(outputDirectory);
        IReadOnlyList<(string Path, string RelativePath)> files;
        if (File.Exists(fullInput))
        {
            if (!IsSupported(fullInput))
            {
                throw new ArgumentException($"Unsupported media or subtitle extension: {Path.GetExtension(fullInput)}", nameof(inputPath));
            }

            files = [(fullInput, Path.GetFileName(fullInput))];
        }
        else if (Directory.Exists(fullInput))
        {
            files = Directory.EnumerateFiles(fullInput, "*", SearchOption.AllDirectories)
                .Where(IsSupported)
                .Select(path => (path, Path.GetRelativePath(fullInput, path)))
                .OrderBy(item => item.Item2, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            throw new FileNotFoundException("Input path was not found.", inputPath);
        }

        var results = new List<MediaInput>(files.Count);
        foreach (var (path, relativePath) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRelative = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            var relativeOutput = Path.ChangeExtension(normalizedRelative, outputExtension);
            var outputPath = Path.GetFullPath(Path.Combine(fullOutput, relativeOutput));
            var hash = await MediaHasher.ComputeSha256Async(path, cancellationToken);
            results.Add(new(path, normalizedRelative, hash, outputPath, KindOf(path)));
        }

        return results;
    }

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));

    public static bool IsSubtitle(string path) =>
        string.Equals(Path.GetExtension(path), ".srt", StringComparison.OrdinalIgnoreCase);

    private static SourceKind KindOf(string path) =>
        IsSubtitle(path) ? SourceKind.Subtitle : SourceKind.Media;
}

/// <summary>Full-file SHA-256 hashing for input media and deterministic job identity.</summary>
public static class MediaHasher
{
    public static string ComputeSha256(string path) =>
        ComputeSha256Async(path).GetAwaiter().GetResult();

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
