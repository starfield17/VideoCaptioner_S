using System.Text.Json.Serialization;

namespace Captioner.Core;

public enum EndpointBackend
{
    OpenAiCompatible,
    SherpaOnnx
}

public sealed record EndpointCapabilities(
    bool SegmentTimestamps,
    bool WordTimestamps,
    bool JsonSchema,
    long MaxAudioBytes,
    TimeSpan MaxAudioDuration)
{
    public static EndpointCapabilities DefaultAsr { get; } = new(
        SegmentTimestamps: true,
        WordTimestamps: true,
        JsonSchema: false,
        MaxAudioBytes: 24 * 1024 * 1024,
        MaxAudioDuration: TimeSpan.FromMinutes(10));
}

public sealed record EndpointProfile(
    string BaseUrl,
    string Model,
    string ApiKeyEnvironmentVariable,
    EndpointCapabilities Capabilities,
    int MaxConcurrency = 1,
    EndpointBackend Backend = EndpointBackend.OpenAiCompatible,
    [property: JsonIgnore] string? ApiKey = null)
{
    public bool HasConfiguredApiKey => !string.IsNullOrWhiteSpace(ApiKey) ||
        !string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable);
}
