using Captioner.Core;

namespace Captioner.Desktop;

public sealed record InputPathItem(string FullPath, string DisplayName, string Kind, string Detail)
{
    public static InputPathItem Create(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            return new(
                fullPath,
                new DirectoryInfo(fullPath).Name,
                "Folder",
                fullPath);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Input path was not found.", fullPath);
        }

        if (!Captioner.Infrastructure.MediaDiscovery.IsSupported(fullPath))
        {
            throw new ArgumentException(
                $"Unsupported media or subtitle extension: {Path.GetExtension(fullPath)}",
                nameof(path));
        }

        return new(
            fullPath,
            Path.GetFileName(fullPath),
            Captioner.Infrastructure.MediaDiscovery.IsSubtitle(fullPath) ? "Subtitle" : "Media",
            fullPath);
    }
}

internal sealed record NewBatchRequest(
    IReadOnlyList<string> Inputs,
    string OutputDirectory,
    string? SourceLanguage,
    string? TargetLanguage,
    SubtitleLayout Layout,
    bool EnableSegmentation,
    bool EnableCorrection,
    int MaxFileConcurrency,
    int LlmConcurrency,
    bool Overwrite,
    string? BatchReferenceText,
    int MaxCueCharactersCjk,
    int MaxCueWordsLatin,
    long MaxCueDurationMs)
{
    public string? ComposeReference(string? defaultReference)
    {
        var global = Normalize(defaultReference);
        var batch = Normalize(BatchReferenceText);
        if (global is null)
        {
            return batch;
        }

        if (batch is null)
        {
            return global;
        }

        return "[Default glossary]\n" + global + "\n\n[Batch reference]\n" + batch;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
