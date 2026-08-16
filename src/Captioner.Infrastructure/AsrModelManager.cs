using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Captioner.Infrastructure;

public sealed record ModelDownloadProgress(
    string ModelId,
    string Phase,
    long BytesCompleted,
    long BytesTotal,
    string? CurrentFile = null);

public sealed record InstalledAsrModel(
    AsrModelDefinition Definition,
    string Directory,
    bool Installed);

public sealed class AsrModelManager : IDisposable
{
    private static readonly JsonSerializerOptions MarkerJson = new() { WriteIndented = true };
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public AsrModelManager(string? rootDirectory = null, HttpClient? httpClient = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? AsrModelCatalog.DefaultModelDirectory);
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = httpClient is null;
    }

    public string RootDirectory { get; }

    public IReadOnlyList<InstalledAsrModel> List() => AsrModelCatalog.All
        .Select(model => new InstalledAsrModel(model, GetModelDirectory(model.Id), IsInstalled(model.Id)))
        .ToArray();

    public string GetModelDirectory(string modelId) => Path.Combine(RootDirectory, AsrModelCatalog.Get(modelId).Id);

    public bool IsInstalled(string modelId)
    {
        var model = AsrModelCatalog.Get(modelId);
        var directory = GetModelDirectory(model.Id);
        if (!File.Exists(Path.Combine(directory, "installed.json")))
        {
            return false;
        }

        return RequiredPaths(model).All(path => File.Exists(Path.Combine(directory, path)) || Directory.Exists(Path.Combine(directory, path)));
    }

    public async Task PullAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var model = AsrModelCatalog.Get(modelId);
        if (IsInstalled(model.Id))
        {
            progress?.Report(new(model.Id, "installed", model.DownloadSizeBytes, model.DownloadSizeBytes));
            return;
        }

        Directory.CreateDirectory(RootDirectory);
        var downloadDirectory = Path.Combine(RootDirectory, ".downloads", model.Id);
        var stagingDirectory = Path.Combine(RootDirectory, ".installing-" + model.Id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloadDirectory);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var total = model.DownloadSizeBytes;
            var completed = 0L;
            if (model.Archive is { } archive)
            {
                var archivePath = Path.Combine(downloadDirectory, "model.tar.bz2.part");
                await DownloadAndVerifyAsync(model.Id, archive.DownloadUri, archivePath, archive.SizeBytes, archive.Sha256,
                    completed, total, progress, cancellationToken);
                completed += archive.SizeBytes;
                progress?.Report(new(model.Id, "extracting", completed, total, Path.GetFileName(archivePath)));
                ExtractArchiveSafely(archivePath, stagingDirectory, archive.RootDirectory, cancellationToken);
            }

            foreach (var file in model.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cachedPath = Path.Combine(downloadDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar) + ".part");
                Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
                await DownloadAndVerifyAsync(model.Id, file.DownloadUri, cachedPath, file.SizeBytes, file.Sha256,
                    completed, total, progress, cancellationToken);
                completed += file.SizeBytes;
                var destination = SafeDestination(stagingDirectory, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(cachedPath, destination, overwrite: true);
            }

            foreach (var required in RequiredPaths(model))
            {
                var path = SafeDestination(stagingDirectory, required);
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    throw new InvalidDataException($"Downloaded model '{model.Id}' is missing '{required}'.");
                }
            }

            var marker = JsonSerializer.SerializeToUtf8Bytes(new
            {
                model.Id,
                model.DisplayName,
                model.Family,
                model.License,
                model.Revision,
                InstalledAt = DateTimeOffset.UtcNow
            }, MarkerJson);
            await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "installed.json"), marker, cancellationToken);

            var finalDirectory = GetModelDirectory(model.Id);
            if (Directory.Exists(finalDirectory))
            {
                Directory.Delete(finalDirectory, recursive: true);
            }

            Directory.Move(stagingDirectory, finalDirectory);
            if (Directory.Exists(downloadDirectory))
            {
                Directory.Delete(downloadDirectory, recursive: true);
            }

            progress?.Report(new(model.Id, "installed", total, total));
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            throw;
        }
    }

    public void Remove(string modelId)
    {
        var model = AsrModelCatalog.Get(modelId);
        var directory = GetModelDirectory(model.Id);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        var downloadDirectory = Path.Combine(RootDirectory, ".downloads", model.Id);
        if (Directory.Exists(downloadDirectory))
        {
            Directory.Delete(downloadDirectory, recursive: true);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task DownloadAndVerifyAsync(
        string modelId,
        Uri uri,
        string path,
        long expectedSize,
        string expectedSha256,
        long alreadyCompleted,
        long total,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (existing > expectedSize)
        {
            File.Delete(path);
            existing = 0;
        }

        if (existing < expectedSize)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (existing > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existing, null);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append)
            {
                existing = 0;
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                path,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true);
            var buffer = new byte[1024 * 1024];
            var current = existing;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                current += read;
                progress?.Report(new(modelId, "downloading", alreadyCompleted + current, total, uri.Segments[^1]));
            }
        }

        var actualSize = new FileInfo(path).Length;
        if (actualSize != expectedSize)
        {
            throw new InvalidDataException(
                $"Download size mismatch for '{uri}': expected {expectedSize}, received {actualSize}.");
        }

        progress?.Report(new(modelId, "verifying", alreadyCompleted + actualSize, total, uri.Segments[^1]));
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException($"SHA-256 verification failed for '{uri}'.");
        }
    }

    private static void ExtractArchiveSafely(
        string archivePath,
        string destinationDirectory,
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        var extractDirectory = Path.Combine(destinationDirectory, ".archive");
        Directory.CreateDirectory(extractDirectory);
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (entry.Key ?? string.Empty).Replace('\\', '/');
            if (key.StartsWith("/", StringComparison.Ordinal) || key.Split('/').Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Unsafe archive entry '{entry.Key}'.");
            }

            entry.WriteToDirectory(extractDirectory, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
        }

        var extractedRoot = SafeDestination(extractDirectory, rootDirectory);
        if (!Directory.Exists(extractedRoot))
        {
            throw new InvalidDataException($"Model archive does not contain expected directory '{rootDirectory}'.");
        }

        foreach (var source in Directory.EnumerateFileSystemEntries(extractedRoot))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
            if (Directory.Exists(source))
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination);
            }
        }

        Directory.Delete(extractDirectory, recursive: true);
    }

    private static IReadOnlyList<string> RequiredPaths(AsrModelDefinition model) => model.Family switch
    {
        AsrModelFamily.Whisper =>
        [
            $"{model.ModelStem}-encoder.int8.onnx",
            $"{model.ModelStem}-decoder.int8.onnx",
            $"{model.ModelStem}-tokens.txt",
            "silero_vad.onnx"
        ],
        AsrModelFamily.Qwen3Asr =>
        [
            "conv_frontend.onnx",
            "encoder.int8.onnx",
            "decoder.int8.onnx",
            "tokenizer",
            "silero_vad.onnx"
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(model))
    };

    private static string SafeDestination(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Path '{relativePath}' escapes the model directory.");
        }

        return destination;
    }
}
