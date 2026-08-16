using System.Text.Json;
using Captioner.Core;
using Captioner.Infrastructure;

namespace Captioner.Tests;

public sealed class ConfigurationAndNamingTests
{
    [Fact]
    public void Initialized_configuration_uses_local_asr_and_deepseek_defaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "captioner-config-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var loader = new JsonConfigurationLoader(path);
            loader.Initialize();

            var text = File.ReadAllText(path);
            using var json = JsonDocument.Parse(text);
            var configuration = loader.Load();

            Assert.Equal(TimeSpan.FromMinutes(30), configuration.Asr.Capabilities.MaxAudioDuration);
            Assert.Equal(EndpointBackend.SherpaOnnx, configuration.Asr.Backend);
            Assert.Equal("whisper-small", configuration.Asr.Model);
            Assert.Equal("https://api.deepseek.com", configuration.Llm.BaseUrl);
            Assert.Equal("deepseek-v4-flash", configuration.Llm.Model);
            Assert.Contains("DEEPSEEK_API_KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-", text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Plaintext_api_key_round_trips_but_redacted_view_and_manifest_exclude_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), "captioner-secret-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        const string secret = "test-secret-value-that-must-not-leak";
        try
        {
            var loader = new JsonConfigurationLoader(path);
            var configuration = loader.CreateDefault();
            var llm = configuration.Llm with { ApiKey = secret };
            var profiles = configuration.Profiles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            profiles["llm"] = llm;
            loader.Save(configuration with { Llm = llm, Profiles = profiles });

            Assert.Equal(secret, loader.Load().Llm.ApiKey);
            Assert.Contains(secret, File.ReadAllText(path), StringComparison.Ordinal);
            var redacted = JsonSerializer.Serialize(loader.GetRedactedView());
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
            Assert.Contains("********", redacted, StringComparison.Ordinal);

            var manifest = new BatchManifest(
                1,
                "batch",
                [],
                DateTimeOffset.UnixEpoch,
                new PipelineOptions(configuration.Asr, llm));
            var manifestJson = JsonSerializer.Serialize(manifest);
            Assert.DoesNotContain(secret, manifestJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"ApiKey\":", manifestJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Model_catalog_is_pinned_and_contains_requested_families()
    {
        Assert.Contains(AsrModelCatalog.All, model => model.Id == "qwen3-asr-0.6b-int8");
        Assert.Contains(AsrModelCatalog.All, model => model.Id == "whisper-small");
        Assert.Contains(AsrModelCatalog.All, model => model.Id == "whisper-large-v3");
        Assert.Equal(AsrModelCatalog.All.Count, AsrModelCatalog.All.Select(model => model.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(AsrModelCatalog.All, model =>
        {
            Assert.True(model.DownloadSizeBytes > 0);
            Assert.All(model.Files, file => Assert.Matches("^[0-9a-f]{64}$", file.Sha256));
            if (model.Archive is { } archive)
            {
                Assert.Matches("^[0-9a-f]{64}$", archive.Sha256);
            }
        });
    }

    [Fact]
    public void Desktop_user_interface_contains_no_cjk_characters()
    {
        var root = FindRepositoryRoot();
        var desktop = Path.Combine(root, "src", "Captioner.Desktop");
        var files = Directory.EnumerateFiles(desktop, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".cs" or ".axaml");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch("[\\u3400-\\u4DBF\\u4E00-\\u9FFF]", text);
        }
    }

    [Theory]
    [InlineData("lesson.mp4", null, SubtitleLayout.Source, "lesson.captioned.srt")]
    [InlineData("unit/lesson.mp4", "zh-CN", SubtitleLayout.Bilingual, "unit/lesson.captioned.zh-CN.bilingual.srt")]
    [InlineData("unit/lesson.mp4", "../中文", SubtitleLayout.Target, "unit/lesson.captioned.---中文.target.srt")]
    public void Output_planner_mirrors_directories_and_sanitizes_language_tokens(
        string relativeInput,
        string? targetLanguage,
        SubtitleLayout layout,
        string expectedRelative)
    {
        var root = Path.Combine(Path.GetTempPath(), "captioner-output-tests");

        var path = SubtitleOutputPlanner.CreatePath(relativeInput, root, targetLanguage, layout);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, expectedRelative.Replace('/', Path.DirectorySeparatorChar))),
            path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Captioner.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
