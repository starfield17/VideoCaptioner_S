using System.Security.Cryptography;
using System.Text.Json;
using Captioner.Core;
using Captioner.Engine;

namespace Captioner.Infrastructure;

/// <summary>Durable local JSON workspace for resumable batch and stage artifacts.</summary>
public sealed class FileJobWorkspace : IJobWorkspace
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _rootDirectory;

    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Captioner");

    public FileJobWorkspace(string? rootDirectory = null)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory ?? DefaultRootDirectory);
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public string GetJobDirectory(string jobId)
    {
        ValidateIdentifier(jobId, nameof(jobId));
        return Path.Combine(_rootDirectory, "jobs", jobId);
    }

    public Task SaveBatchAsync(BatchManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateIdentifier(manifest.BatchId, nameof(manifest.BatchId));
        return WriteJsonAsync(Path.Combine(_rootDirectory, "batches", manifest.BatchId + ".json"), manifest, cancellationToken);
    }

    public Task<BatchManifest?> LoadBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        ValidateIdentifier(batchId, nameof(batchId));
        return ReadJsonAsync<BatchManifest>(Path.Combine(_rootDirectory, "batches", batchId + ".json"), cancellationToken);
    }

    public Task SaveJobAsync(JobManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateIdentifier(manifest.JobId, nameof(manifest.JobId));
        return WriteJsonAsync(Path.Combine(GetJobDirectory(manifest.JobId), "job.json"), manifest, cancellationToken);
    }

    public Task<JobManifest?> LoadJobAsync(string jobId, CancellationToken cancellationToken)
    {
        ValidateIdentifier(jobId, nameof(jobId));
        return ReadJsonAsync<JobManifest>(Path.Combine(GetJobDirectory(jobId), "job.json"), cancellationToken);
    }

    public async Task<ArtifactReference> WriteArtifactAsync<T>(
        string jobId,
        string name,
        T value,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(jobId, nameof(jobId));
        var relativePath = Path.Combine("artifacts", ValidateRelativeName(name));
        var fullPath = ResolveJobRelativePath(jobId, relativePath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await WriteBytesAsync(fullPath, bytes, cancellationToken);
        return new(relativePath.Replace(Path.DirectorySeparatorChar, '/'), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public async Task<T> ReadArtifactAsync<T>(
        string jobId,
        ArtifactReference artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateIdentifier(jobId, nameof(jobId));
        var fullPath = ResolveJobRelativePath(jobId, artifact.RelativePath);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        return value is null
            ? throw new InvalidDataException($"Artifact '{artifact.RelativePath}' contained JSON null.")
            : value;
    }

    public async Task<bool> VerifyArtifactAsync(
        string jobId,
        ArtifactReference artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateIdentifier(jobId, nameof(jobId));
        if (string.IsNullOrWhiteSpace(artifact.Sha256))
        {
            return false;
        }

        var fullPath = ResolveJobRelativePath(jobId, artifact.RelativePath);
        if (!File.Exists(fullPath))
        {
            return false;
        }

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                hash,
                Convert.FromHexString(artifact.Sha256));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListBatchIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(_rootDirectory, "batches");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return await Task.Run(() => Directory.EnumerateFiles(directory, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray(), cancellationToken);
    }

    public async Task CleanBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        ValidateIdentifier(batchId, nameof(batchId));
        var manifest = await LoadBatchAsync(batchId, cancellationToken);
        if (manifest is not null)
        {
            foreach (var job in manifest.Jobs)
            {
                ValidateIdentifier(job.JobId, nameof(job.JobId));
                var jobDirectory = GetJobDirectory(job.JobId);
                if (Directory.Exists(jobDirectory))
                {
                    Directory.Delete(jobDirectory, recursive: true);
                }
            }
        }

        var batchPath = Path.Combine(_rootDirectory, "batches", batchId + ".json");
        if (File.Exists(batchPath))
        {
            File.Delete(batchPath);
        }
    }

    private async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await WriteBytesAsync(path, bytes, cancellationToken);
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Artifact path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string ResolveJobRelativePath(string jobId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Artifact path must be a non-empty relative path.", nameof(relativePath));
        }

        var jobDirectory = Path.GetFullPath(GetJobDirectory(jobId));
        var fullPath = Path.GetFullPath(Path.Combine(jobDirectory, relativePath));
        if (!fullPath.StartsWith(jobDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(fullPath, jobDirectory, StringComparison.Ordinal))
        {
            throw new ArgumentException("Artifact path escapes the job directory.", nameof(relativePath));
        }

        return fullPath;
    }

    private static string ValidateRelativeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) ||
            name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
        {
            throw new ArgumentException("Artifact name must be a safe relative file name.", nameof(name));
        }

        return name;
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value is "." or ".." || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Identifier must be a safe directory name.", parameterName);
        }
    }
}
