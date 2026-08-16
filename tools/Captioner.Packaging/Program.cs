using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Captioner.Packaging;

internal static class Program
{
    private const int DownloadAttemptLimit = 4;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("Usage: prepare-ffmpeg <target> | package <rid> <version> | checksums <directory>");
            }

            var root = FindRepositoryRoot();
            switch (args[0])
            {
                case "prepare-ffmpeg":
                    await PrepareFfmpegAsync(root, args[1]);
                    break;
                case "package" when args.Length == 3:
                    await PackageAsync(root, args[1], args[2]);
                    break;
                case "checksums":
                    await WriteChecksumsAsync(Path.GetFullPath(args[1]));
                    break;
                default:
                    throw new ArgumentException("Unknown packaging command or invalid arguments.");
            }

            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or HttpRequestException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task PrepareFfmpegAsync(string root, string targetName)
    {
        var manifestPath = Path.Combine(root, "packaging", "ffmpeg", "manifest.json");
        var manifest = JsonSerializer.Deserialize<FfmpegManifest>(await File.ReadAllTextAsync(manifestPath), JsonOptions) ??
            throw new InvalidOperationException("FFmpeg manifest is empty.");
        if (manifest.SchemaVersion != 1 || !manifest.Targets.TryGetValue(targetName, out var target))
        {
            throw new ArgumentException($"Unknown FFmpeg target: {targetName}");
        }

        var destination = Path.Combine(root, "artifacts", "ffmpeg", targetName);
        var downloadDirectory = Path.Combine(root, "artifacts", "downloads", targetName);
        RecreateDirectory(destination);
        Directory.CreateDirectory(downloadDirectory);
        var binDirectory = Directory.CreateDirectory(Path.Combine(destination, "bin")).FullName;
        var expectedTools = target.Platform == "windows"
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ffmpeg.exe"] = "ffmpeg.exe", ["ffprobe.exe"] = "ffprobe.exe" }
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ffmpeg"] = "ffmpeg", ["ffprobe"] = "ffprobe" };

        foreach (var sourceArchive in target.Archives)
        {
            var fileName = Path.GetFileName(new Uri(sourceArchive.Url).LocalPath);
            var archivePath = Path.Combine(downloadDirectory, fileName);
            await DownloadVerifiedAsync(sourceArchive.Url, sourceArchive.Sha256, archivePath);
            await using var archiveStream = File.OpenRead(archivePath);
            using var reader = ReaderFactory.OpenReader(archiveStream);
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory)
                {
                    continue;
                }

                var entryName = Path.GetFileName(reader.Entry.Key ?? string.Empty);
                if (expectedTools.TryGetValue(entryName, out var outputName))
                {
                    reader.WriteEntryToFile(Path.Combine(binDirectory, outputName), new ExtractionOptions
                    {
                        Overwrite = true
                    });
                }
            }
        }

        foreach (var tool in expectedTools.Values)
        {
            var path = Path.Combine(binDirectory, tool);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"FFmpeg archive did not contain {tool}.");
            }

            MakeExecutable(path);
        }

        var licenseDirectory = Directory.CreateDirectory(Path.Combine(destination, "licenses")).FullName;
        foreach (var license in manifest.Licenses)
        {
            await DownloadVerifiedAsync(license.Url, license.Sha256, Path.Combine(licenseDirectory, license.Name));
        }

        await File.WriteAllTextAsync(Path.Combine(destination, "source.json"), JsonSerializer.Serialize(new
        {
            manifest.FfmpegVersion,
            target.Provider,
            target.SourceVersion,
            target.BuildRecipe,
            target.FfmpegSource,
            Target = targetName
        }, JsonOptions));
        await File.WriteAllTextAsync(
            Path.Combine(destination, "notice.txt"),
            "Bundled FFmpeg is GPLv3 software. Source and build provenance are recorded in source.json.\n");
        var suffix = target.Platform == "windows" ? ".exe" : string.Empty;
        await RunCaptureAsync(Path.Combine(binDirectory, "ffmpeg" + suffix), ["-hide_banner", "-version"]);
        await RunCaptureAsync(Path.Combine(binDirectory, "ffprobe" + suffix), ["-hide_banner", "-version"]);
        Console.WriteLine(destination);
    }

    private static async Task PackageAsync(string root, string rid, string version)
    {
        if (!Version.TryParse(version, out _))
        {
            throw new ArgumentException("Version must be numeric, for example 1.0.0.", nameof(version));
        }

        var target = TargetForRid(rid);
        var ffmpegDirectory = Path.Combine(root, "artifacts", "ffmpeg", target);
        if (!Directory.Exists(Path.Combine(ffmpegDirectory, "bin")))
        {
            throw new InvalidOperationException($"Prepare FFmpeg target {target} before packaging.");
        }

        var stageRoot = Path.Combine(root, "artifacts", "stage", rid);
        var appRoot = Path.Combine(stageRoot, "captioner");
        var publishRoot = Path.Combine(root, "artifacts", "publish", rid);
        RecreateDirectory(stageRoot);
        RecreateDirectory(publishRoot);
        var desktopPublish = Path.Combine(publishRoot, "desktop");
        var cliPublish = Path.Combine(publishRoot, "cli");
        await PublishAsync(root, Path.Combine(root, "src", "Captioner.Desktop", "Captioner.Desktop.csproj"), desktopPublish, rid, version);
        await PublishAsync(root, Path.Combine(root, "src", "Captioner.Cli", "Captioner.Cli.csproj"), cliPublish, rid, version);
        CopyDirectory(desktopPublish, appRoot);
        CopyDirectory(cliPublish, appRoot);
        CopyReleaseContent(root, ffmpegDirectory, appRoot);

        var platform = target.Split('-', 2)[0];
        var releaseDirectory = Directory.CreateDirectory(Path.Combine(root, "artifacts", "release")).FullName;
        if (platform == "macos")
        {
            var bundle = Path.Combine(stageRoot, "Captioner.app");
            var macosDirectory = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS")).FullName;
            CopyDirectory(appRoot, macosDirectory);
            await File.WriteAllTextAsync(Path.Combine(bundle, "Contents", "Info.plist"), InfoPlist(version));
            var archive = Path.Combine(releaseDirectory, $"captioner-v{version}-{target}.zip");
            File.Delete(archive);
            ZipFile.CreateFromDirectory(bundle, archive, CompressionLevel.SmallestSize, includeBaseDirectory: true);
            Console.WriteLine(bundle);
            Console.WriteLine(archive);
        }
        else
        {
            var archive = Path.Combine(releaseDirectory, $"captioner-v{version}-{target}.zip");
            File.Delete(archive);
            ZipFile.CreateFromDirectory(appRoot, archive, CompressionLevel.SmallestSize, includeBaseDirectory: true);
            Console.WriteLine(appRoot);
            Console.WriteLine(archive);
        }
    }

    private static async Task PublishAsync(string root, string project, string output, string rid, string version)
    {
        await RunCheckedAsync("dotnet",
        [
            "publish", project,
            "-c", "Release",
            "-r", rid,
            "--self-contained", "true",
            "-p:PublishSingleFile=false",
            $"-p:Version={version}",
            "-o", output
        ], root);
    }

    private static void CopyReleaseContent(string root, string ffmpegDirectory, string appRoot)
    {
        var tools = Directory.CreateDirectory(Path.Combine(appRoot, "tools")).FullName;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ffmpegDirectory, "bin")))
        {
            var destination = Path.Combine(tools, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
            MakeExecutable(destination);
        }

        var notices = Path.Combine(appRoot, "third-party", "ffmpeg");
        Directory.CreateDirectory(notices);
        File.Copy(Path.Combine(ffmpegDirectory, "notice.txt"), Path.Combine(notices, "NOTICE.txt"), overwrite: true);
        File.Copy(Path.Combine(ffmpegDirectory, "source.json"), Path.Combine(notices, "SOURCE.json"), overwrite: true);
        CopyDirectory(Path.Combine(ffmpegDirectory, "licenses"), Path.Combine(notices, "licenses"));
        File.Copy(Path.Combine(root, "README.md"), Path.Combine(appRoot, "README.md"), overwrite: true);
        File.Copy(Path.Combine(root, "LICENSE"), Path.Combine(appRoot, "LICENSE"), overwrite: true);
        File.Copy(Path.Combine(root, "THIRD-PARTY-NOTICES.md"), Path.Combine(appRoot, "THIRD-PARTY-NOTICES.md"), overwrite: true);
    }

    private static async Task DownloadVerifiedAsync(string url, string expectedHash, string path)
    {
        if (File.Exists(path) && string.Equals(await Sha256Async(path), expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".download";
        Exception? lastError = null;
        for (var attempt = 1; attempt <= DownloadAttemptLimit; attempt++)
        {
            try
            {
                File.Delete(temporaryPath);
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode} while downloading {url}.", null, response.StatusCode);
                }

                await using (var input = await response.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, true))
                {
                    await input.CopyToAsync(output);
                }

                if (!string.Equals(await Sha256Async(temporaryPath), expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"SHA-256 mismatch for {Path.GetFileName(path)}.");
                }

                File.Move(temporaryPath, path, overwrite: true);
                return;
            }
            catch (HttpRequestException exception) when (IsTransient(exception.StatusCode))
            {
                lastError = exception;
            }
            catch (OperationCanceledException exception)
            {
                lastError = exception;
            }

            if (attempt < DownloadAttemptLimit)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)));
            }
        }

        File.Delete(temporaryPath);
        throw new HttpRequestException($"Download failed after {DownloadAttemptLimit} attempts: {url}", lastError);
    }

    private static bool IsTransient(HttpStatusCode? statusCode) => statusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static async Task WriteChecksumsAsync(string directory)
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory)
            .Where(path => Path.GetFileName(path) != "SHA256SUMS.txt")
            .Order(StringComparer.Ordinal))
        {
            lines.Add(await Sha256Async(file) + "  " + Path.GetFileName(file));
        }

        await File.WriteAllLinesAsync(Path.Combine(directory, "SHA256SUMS.txt"), lines);
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static async Task RunCheckedAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        using var process = StartProcess(fileName, arguments, workingDirectory, redirect: false);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}.");
        }
    }

    private static async Task<string> RunCaptureAsync(string fileName, IReadOnlyList<string> arguments)
    {
        using var process = StartProcess(fileName, arguments, null, redirect: true);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout + await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} verification failed: {output[^Math.Min(output.Length, 2_000)..]}");
        }

        return output;
    }

    private static Process StartProcess(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, bool redirect)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Captioner.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static string TargetForRid(string rid) => rid switch
    {
        "win-x64" => "windows-x86_64",
        "win-arm64" => "windows-arm64",
        "linux-x64" => "linux-x86_64",
        "linux-arm64" => "linux-arm64",
        "osx-x64" => "macos-x86_64",
        "osx-arm64" => "macos-arm64",
        _ => throw new ArgumentException($"Unsupported RID: {rid}")
    };

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static string InfoPlist(string version) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>CFBundleName</key><string>Captioner</string>
          <key>CFBundleDisplayName</key><string>Captioner</string>
          <key>CFBundleIdentifier</key><string>dev.starfield17.captioner</string>
          <key>CFBundleExecutable</key><string>captioner-desktop</string>
          <key>CFBundleVersion</key><string>{{version}}</string>
          <key>CFBundleShortVersionString</key><string>{{version}}</string>
          <key>NSHighResolutionCapable</key><true/>
        </dict></plist>
        """;

    private sealed record FfmpegManifest(
        [property: JsonPropertyName("schema_version")] int SchemaVersion,
        [property: JsonPropertyName("ffmpeg_version")] string FfmpegVersion,
        IReadOnlyList<LicenseEntry> Licenses,
        IReadOnlyDictionary<string, TargetEntry> Targets);

    private sealed record LicenseEntry(string Name, string Url, string Sha256);

    private sealed record TargetEntry(
        string Platform,
        string Architecture,
        string Provider,
        [property: JsonPropertyName("source_version")] string SourceVersion,
        [property: JsonPropertyName("build_recipe")] string BuildRecipe,
        [property: JsonPropertyName("ffmpeg_source")] string FfmpegSource,
        IReadOnlyList<ArchiveEntry> Archives);

    private sealed record ArchiveEntry(string Url, string Sha256, string Format);
}
