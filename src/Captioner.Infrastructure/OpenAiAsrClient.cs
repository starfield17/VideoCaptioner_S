using System.Globalization;
using System.Text.Json;
using Captioner.Core;
using Captioner.Engine;

namespace Captioner.Infrastructure;

/// <summary>OpenAI-compatible audio transcription adapter.</summary>
public sealed class OpenAiAsrClient : IAsrClient
{
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _retryBaseDelay;

    public OpenAiAsrClient(HttpClient? httpClient = null, TimeSpan? retryBaseDelay = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(250);
    }

    public async Task<TranscriptDocument> TranscribeAsync(
        MediaChunk chunk,
        EndpointProfile profile,
        string? language,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(profile);
        if (!File.Exists(chunk.Path))
        {
            throw new FileNotFoundException("The normalized audio chunk was not found.", chunk.Path);
        }

        var key = OpenAiHttp.GetApiKey(profile);
        using var response = await OpenAiHttp.SendWithRetryAsync(
            _httpClient,
            () => CreateTranscriptionRequest(chunk, profile, language, key),
            _retryBaseDelay,
            cancellationToken);

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return ParseTranscript(json.RootElement, chunk);
    }

    public async Task<string?> CheckAsync(EndpointProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var key = OpenAiHttp.GetApiKey(profile);
        using var response = await OpenAiHttp.SendWithRetryAsync(
            _httpClient,
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    OpenAiHttp.Endpoint(profile.BaseUrl, "models/" + Uri.EscapeDataString(profile.Model)));
                OpenAiHttp.SetAuthorization(request, key);
                return request;
            },
            _retryBaseDelay,
            cancellationToken);

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return json.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : profile.Model;
    }

    private static HttpRequestMessage CreateTranscriptionRequest(
        MediaChunk chunk,
        EndpointProfile profile,
        string? language,
        string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            OpenAiHttp.Endpoint(profile.BaseUrl, "audio/transcriptions"));
        OpenAiHttp.SetAuthorization(request, key);

        var content = new MultipartFormDataContent();
        var stream = File.OpenRead(chunk.Path);
        var file = new StreamContent(stream);
        var mediaType = string.Equals(Path.GetExtension(chunk.Path), ".wav", StringComparison.OrdinalIgnoreCase)
            ? "audio/wav"
            : "audio/flac";
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        content.Add(file, "file", Path.GetFileName(chunk.Path));
        content.Add(new StringContent(profile.Model), "model");
        content.Add(new StringContent("verbose_json"), "response_format");

        if (!string.IsNullOrWhiteSpace(language))
        {
            content.Add(new StringContent(language), "language");
        }

        if (profile.Capabilities.SegmentTimestamps)
        {
            content.Add(new StringContent("segment"), "timestamp_granularities[]");
        }

        if (profile.Capabilities.WordTimestamps)
        {
            content.Add(new StringContent("word"), "timestamp_granularities[]");
        }

        request.Content = content;
        return request;
    }

    private static TranscriptDocument ParseTranscript(JsonElement root, MediaChunk chunk)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The ASR response must be a JSON object.");
        }

        var language = GetOptionalString(root, "language");
        var anchors = new List<TimedAnchor>();

        if (root.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var word in words.EnumerateArray())
            {
                if (word.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Every ASR word must be a JSON object.");
                }

                var text = GetRequiredString(word, "word");
                var start = GetMilliseconds(word, "start");
                var end = GetMilliseconds(word, "end");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    anchors.Add(new($"w{index++:D6}", text, start, end, TimingOrigin.ProviderWord));
                }
            }
        }

        if (anchors.Count == 0 && root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Every ASR segment must be a JSON object.");
                }

                var text = GetRequiredString(segment, "text");
                var start = GetMilliseconds(segment, "start");
                var end = GetMilliseconds(segment, "end");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    anchors.Add(new($"s{index++:D6}", text, start, end, TimingOrigin.ProviderSegment));
                }
            }
        }

        if (anchors.Count == 0 && root.TryGetProperty("text", out var fullText) &&
            fullText.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(fullText.GetString()))
        {
            throw new InvalidDataException("The ASR response contains text but no timestamped segments or words.");
        }

        var validation = TimelineValidator.ValidateAnchors(anchors, chunk.DurationMs);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(
                "The ASR response contains invalid timestamps: " +
                string.Join("; ", validation.Issues.Select(issue => issue.Message)));
        }

        return new(language, chunk.DurationMs, anchors);
    }

    private static string GetRequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"The ASR response is missing string property '{property}'.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static string? GetOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetMilliseconds(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            throw new InvalidDataException($"The ASR response is missing timestamp property '{property}'.");
        }

        double seconds = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var numeric) => numeric,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new InvalidDataException($"ASR timestamp '{property}' must be numeric.")
        };

        if (!double.IsFinite(seconds) || seconds < 0 || seconds > long.MaxValue / 1000d)
        {
            throw new InvalidDataException($"ASR timestamp '{property}' is outside the supported range.");
        }

        return checked((long)Math.Round(seconds * 1000d, MidpointRounding.AwayFromZero));
    }
}
