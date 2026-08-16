using System.Text.Json;
using System.Text.Json.Serialization;
using Captioner.Core;

namespace Captioner.Infrastructure;

public sealed record CaptionerConfiguration(
    EndpointProfile Asr,
    EndpointProfile Llm,
    IReadOnlyDictionary<string, EndpointProfile> Profiles,
    string ModelDirectory,
    string? ReferenceText = null,
    string? WorkspaceDirectory = null)
{
    public EndpointProfile GetProfile(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Profiles.TryGetValue(name, out var profile)
            ? profile
            : throw new KeyNotFoundException($"Endpoint profile '{name}' is not configured.");
    }
}

/// <summary>Reads and atomically writes application configuration, including optional plaintext API keys.</summary>
public sealed class JsonConfigurationLoader
{
    private static readonly EndpointCapabilities LocalAsrCapabilities = new(
        SegmentTimestamps: true,
        WordTimestamps: false,
        JsonSchema: false,
        MaxAudioBytes: 1024L * 1024 * 1024,
        MaxAudioDuration: TimeSpan.FromMinutes(30));

    private static readonly EndpointCapabilities LlmCapabilities = new(
        SegmentTimestamps: false,
        WordTimestamps: false,
        JsonSchema: true,
        MaxAudioBytes: 0,
        MaxAudioDuration: TimeSpan.Zero);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public JsonConfigurationLoader(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captioner",
            "config.json"));
    }

    public string ConfigPath => _path;

    public string GetPath() => _path;

    public string Initialize()
    {
        if (File.Exists(_path))
        {
            return _path;
        }

        Save(CreateDefault());
        return _path;
    }

    public CaptionerConfiguration CreateDefault()
    {
        var asr = new EndpointProfile(
            "local://sherpa-onnx",
            "whisper-small",
            string.Empty,
            LocalAsrCapabilities,
            MaxConcurrency: 1,
            Backend: EndpointBackend.SherpaOnnx);
        var llm = new EndpointProfile(
            "https://api.deepseek.com",
            "deepseek-v4-flash",
            "DEEPSEEK_API_KEY",
            LlmCapabilities,
            MaxConcurrency: 4,
            Backend: EndpointBackend.OpenAiCompatible);
        return new(
            asr,
            llm,
            new Dictionary<string, EndpointProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["asr"] = asr,
                ["llm"] = llm
            },
            AsrModelCatalog.DefaultModelDirectory);
    }

    public void Save(CaptionerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var profiles = configuration.Profiles
            .Where(pair => !string.Equals(pair.Key, "asr", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(pair.Key, "llm", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => ToFileEndpoint(pair.Value), StringComparer.OrdinalIgnoreCase);
        var value = new
        {
            modelDirectory = Path.GetFullPath(configuration.ModelDirectory),
            workspaceDirectory = string.IsNullOrWhiteSpace(configuration.WorkspaceDirectory)
                ? null
                : Path.GetFullPath(configuration.WorkspaceDirectory),
            referenceText = string.IsNullOrWhiteSpace(configuration.ReferenceText)
                ? null
                : configuration.ReferenceText,
            @default = new
            {
                asr = ToFileEndpoint(configuration.Asr),
                llm = ToFileEndpoint(configuration.Llm)
            },
            profiles
        };
        WriteAtomic(_path, JsonSerializer.SerializeToUtf8Bytes(value, WriteOptions));
    }

    public CaptionerConfiguration Load() => Load(null);

    public CaptionerConfiguration Load(string? profileName)
    {
        if (!File.Exists(_path))
        {
            Initialize();
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(_path));
        var root = document.RootElement;
        var profiles = new Dictionary<string, EndpointProfile>(StringComparer.OrdinalIgnoreCase);
        var defaultElement = GetProperty(root, "default") ?? root;
        var defaultAsr = ParseEndpoint(
            GetProperty(defaultElement, "asr") ?? GetProperty(root, "asr") ?? defaultElement,
            LocalAsrCapabilities,
            "whisper-small",
            string.Empty,
            EndpointBackend.SherpaOnnx);
        var defaultLlm = ParseEndpoint(
            GetProperty(defaultElement, "llm") ?? GetProperty(root, "llm") ?? defaultElement,
            LlmCapabilities,
            "deepseek-v4-flash",
            "DEEPSEEK_API_KEY",
            EndpointBackend.OpenAiCompatible);

        if (GetProperty(root, "profiles") is { ValueKind: JsonValueKind.Object } profileObject)
        {
            foreach (var profile in profileObject.EnumerateObject())
            {
                profiles.Add(profile.Name, ParseEndpoint(
                    profile.Value,
                    EndpointCapabilities.DefaultAsr,
                    defaultAsr.Model,
                    defaultAsr.ApiKeyEnvironmentVariable,
                    EndpointBackend.OpenAiCompatible));
            }
        }

        profiles["asr"] = defaultAsr;
        profiles["llm"] = defaultLlm;
        var modelDirectory = GetString(root, "modelDirectory") ?? AsrModelCatalog.DefaultModelDirectory;

        if (!string.IsNullOrWhiteSpace(profileName))
        {
            var selected = profiles.TryGetValue(profileName, out var endpoint)
                ? endpoint
                : throw new KeyNotFoundException($"Endpoint profile '{profileName}' is not configured.");
            return new(
                selected,
                selected,
                profiles,
                Path.GetFullPath(modelDirectory),
                GetString(root, "referenceText"),
                ResolveWorkspaceDirectory(root));
        }

        var referenceText = GetString(root, "referenceText");
        return new(
            defaultAsr,
            defaultLlm,
            profiles,
            Path.GetFullPath(modelDirectory),
            referenceText,
            ResolveWorkspaceDirectory(root));
    }

    public CaptionerConfiguration LoadWithEnvironment(string? profileName = null)
    {
        var configuration = Load(profileName);
        var asr = ApplyEnvironment(configuration.Asr, "ASR");
        var llm = ApplyEnvironment(configuration.Llm, "LLM");
        var profiles = configuration.Profiles.ToDictionary(
            pair => pair.Key,
            pair => ApplyEnvironment(pair.Value, pair.Key),
            StringComparer.OrdinalIgnoreCase);
        profiles["asr"] = asr;
        profiles["llm"] = llm;
        var modelDirectory = Environment.GetEnvironmentVariable("CAPTIONER_MODEL_DIRECTORY");
        return configuration with
        {
            Asr = asr,
            Llm = llm,
            Profiles = profiles,
            ModelDirectory = string.IsNullOrWhiteSpace(modelDirectory)
                ? configuration.ModelDirectory
                : Path.GetFullPath(modelDirectory),
            ReferenceText = configuration.ReferenceText,
            WorkspaceDirectory = configuration.WorkspaceDirectory
        };
    }

    public object GetRedactedView()
    {
        var configuration = Load();
        return new
        {
            configuration.ModelDirectory,
            configuration.WorkspaceDirectory,
            configuration.ReferenceText,
            Asr = ToRedactedEndpoint(configuration.Asr),
            Llm = ToRedactedEndpoint(configuration.Llm),
            Profiles = configuration.Profiles
                .Where(pair => !string.Equals(pair.Key, "asr", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(pair.Key, "llm", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => ToRedactedEndpoint(pair.Value), StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string ResolveWorkspaceDirectory(JsonElement root) =>
        Path.GetFullPath(GetString(root, "workspaceDirectory") ?? FileJobWorkspace.DefaultRootDirectory);

    private static EndpointProfile ApplyEnvironment(EndpointProfile profile, string name)
    {
        var prefix = "CAPTIONER_" + name.ToUpperInvariant() + "_";
        var baseUrl = Environment.GetEnvironmentVariable(prefix + "BASE_URL");
        var model = Environment.GetEnvironmentVariable(prefix + "MODEL");
        var backend = ParseBackend(Environment.GetEnvironmentVariable(prefix + "BACKEND"), profile.Backend);
        var apiKeyVariable = Environment.GetEnvironmentVariable(prefix + "API_KEY_ENVIRONMENT_VARIABLE");
        var apiKey = Environment.GetEnvironmentVariable(prefix + "API_KEY");
        return profile with
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? profile.BaseUrl : baseUrl,
            Model = string.IsNullOrWhiteSpace(model) ? profile.Model : model,
            Backend = backend,
            ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(apiKeyVariable)
                ? profile.ApiKeyEnvironmentVariable
                : apiKeyVariable,
            ApiKey = string.IsNullOrWhiteSpace(profile.ApiKey) && !string.IsNullOrWhiteSpace(apiKey)
                ? apiKey
                : profile.ApiKey
        };
    }

    private static EndpointProfile ParseEndpoint(
        JsonElement element,
        EndpointCapabilities fallbackCapabilities,
        string fallbackModel,
        string fallbackApiKeyVariable,
        EndpointBackend fallbackBackend)
    {
        var backend = ParseBackend(GetString(element, "backend"), fallbackBackend);
        var baseUrl = GetString(element, "baseUrl") ??
            (backend == EndpointBackend.SherpaOnnx ? "local://sherpa-onnx" : "https://api.openai.com/v1");
        var model = GetString(element, "model") ?? fallbackModel;
        var keyVariable = GetString(element, "apiKeyEnvironmentVariable") ??
            GetString(element, "apiKeyEnv") ?? fallbackApiKeyVariable;
        var apiKey = GetString(element, "apiKey");
        var capabilities = GetProperty(element, "capabilities") is { } capabilityElement
            ? ParseCapabilities(capabilityElement, fallbackCapabilities)
            : fallbackCapabilities;
        var maxConcurrency = GetInt(element, "maxConcurrency") ?? 1;
        return new(baseUrl, model, keyVariable, capabilities, Math.Max(1, maxConcurrency), backend, apiKey);
    }

    private static EndpointBackend ParseBackend(string? value, EndpointBackend fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim().ToLowerInvariant() switch
            {
                "sherpaonnx" or "sherpa-onnx" or "local" => EndpointBackend.SherpaOnnx,
                "openaicompatible" or "openai-compatible" or "openai" => EndpointBackend.OpenAiCompatible,
                _ => throw new InvalidDataException($"Unsupported endpoint backend '{value}'.")
            };

    private static EndpointCapabilities ParseCapabilities(JsonElement element, EndpointCapabilities fallback)
    {
        var durationMs = GetLong(element, "maxAudioDurationMs");
        var durationSeconds = GetDouble(element, "maxAudioDurationSeconds");
        var durationText = GetString(element, "maxAudioDuration");
        var duration = durationMs is not null
            ? TimeSpan.FromMilliseconds(Math.Max(0, durationMs.Value))
            : durationSeconds is not null
                ? TimeSpan.FromSeconds(Math.Max(0, durationSeconds.Value))
                : TimeSpan.TryParse(durationText, System.Globalization.CultureInfo.InvariantCulture, out var parsedDuration)
                    ? parsedDuration < TimeSpan.Zero ? TimeSpan.Zero : parsedDuration
                    : fallback.MaxAudioDuration;
        return new(
            GetBool(element, "segmentTimestamps") ?? fallback.SegmentTimestamps,
            GetBool(element, "wordTimestamps") ?? fallback.WordTimestamps,
            GetBool(element, "jsonSchema") ?? fallback.JsonSchema,
            GetLong(element, "maxAudioBytes") ?? fallback.MaxAudioBytes,
            duration);
    }

    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        GetProperty(element, name) is { } value && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        GetProperty(element, name) is { } value && value.TryGetInt32(out var result) ? result : null;

    private static long? GetLong(JsonElement element, string name) =>
        GetProperty(element, name) is { } value && value.TryGetInt64(out var result) ? result : null;

    private static double? GetDouble(JsonElement element, string name) =>
        GetProperty(element, name) is { } value && value.TryGetDouble(out var result) ? result : null;

    private static bool? GetBool(JsonElement element, string name) =>
        GetProperty(element, name) is { } value && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static object ToFileEndpoint(EndpointProfile profile) => new
    {
        Backend = profile.Backend == EndpointBackend.SherpaOnnx ? "sherpa-onnx" : "openai-compatible",
        profile.BaseUrl,
        profile.Model,
        profile.ApiKey,
        ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable)
            ? null
            : profile.ApiKeyEnvironmentVariable,
        capabilities = new
        {
            profile.Capabilities.SegmentTimestamps,
            profile.Capabilities.WordTimestamps,
            profile.Capabilities.JsonSchema,
            profile.Capabilities.MaxAudioBytes,
            MaxAudioDurationSeconds = profile.Capabilities.MaxAudioDuration.TotalSeconds
        },
        profile.MaxConcurrency
    };

    private static object ToRedactedEndpoint(EndpointProfile profile) => new
    {
        profile.Backend,
        profile.BaseUrl,
        profile.Model,
        ApiKey = string.IsNullOrWhiteSpace(profile.ApiKey) ? null : "********",
        profile.ApiKeyEnvironmentVariable,
        profile.Capabilities,
        profile.MaxConcurrency
    };

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Configuration path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
}
