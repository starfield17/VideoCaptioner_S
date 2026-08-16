using System.Net;
using System.Text;
using System.Text.Json;
using Captioner.Core;
using Captioner.Engine;
using Captioner.Infrastructure;

namespace Captioner.Tests;

public sealed class OpenAiAdapterContractTests
{
    [Fact]
    public async Task Asr_adapter_sends_timestamped_verbose_request_and_parses_word_timing()
    {
        var path = Path.Combine(Path.GetTempPath(), "captioner-asr-" + Guid.NewGuid().ToString("N") + ".flac");
        var keyName = "CAPTIONER_TEST_KEY_" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(path, [1, 2, 3], CancellationToken.None);
        Environment.SetEnvironmentVariable(keyName, "test-secret");
        string? requestBody = null;
        try
        {
            var handler = new DelegateHandler(async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://asr.example/v1/audio/transcriptions", request.RequestUri?.ToString());
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-secret", request.Headers.Authorization?.Parameter);
                requestBody = await request.Content!.ReadAsStringAsync(CancellationToken.None);
                return JsonResponse("""
                    {"language":"en","words":[
                      {"word":"Hello","start":0.0,"end":0.4},
                      {"word":"world","start":0.4,"end":1.0}
                    ]}
                    """);
            });
            var client = new OpenAiAsrClient(new HttpClient(handler), TimeSpan.Zero);
            var profile = new EndpointProfile(
                "https://asr.example/v1",
                "whisper-test",
                keyName,
                EndpointCapabilities.DefaultAsr);

            var transcript = await client.TranscribeAsync(
                new MediaChunk(path, 0, 1_000, 3, 0),
                profile,
                "en",
                CancellationToken.None);

            Assert.Equal("en", transcript.Language);
            Assert.Equal(2, transcript.Anchors.Count);
            Assert.Equal((0L, 400L), (transcript.Anchors[0].StartMs, transcript.Anchors[0].EndMs));
            Assert.Equal(TimingOrigin.ProviderWord, transcript.Anchors[0].Origin);
            Assert.Contains("whisper-test", requestBody, StringComparison.Ordinal);
            Assert.Contains("verbose_json", requestBody, StringComparison.Ordinal);
            Assert.Contains("word", requestBody, StringComparison.Ordinal);
            Assert.Contains("segment", requestBody, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Llm_adapter_windows_large_cue_sets_and_preserves_exact_ids()
    {
        var keyName = "CAPTIONER_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(keyName, "test-secret");
        var callCount = 0;
        try
        {
            var handler = new DelegateHandler(async request =>
            {
                callCount++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(CancellationToken.None));
                Assert.Equal("llm-test", body.RootElement.GetProperty("model").GetString());
                Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
                var userJson = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                using var user = JsonDocument.Parse(userJson);
                var values = user.RootElement.GetProperty("cues").EnumerateArray().ToDictionary(
                    cue => cue.GetProperty("id").GetString()!,
                    cue => "fixed:" + cue.GetProperty("text").GetString(),
                    StringComparer.Ordinal);
                var content = JsonSerializer.Serialize(values);
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content } } }
                }));
            });
            var client = new OpenAiLlmClient(new HttpClient(handler), TimeSpan.Zero);
            var profile = new EndpointProfile(
                "https://llm.example/v1",
                "llm-test",
                keyName,
                new EndpointCapabilities(false, false, true, 0, TimeSpan.Zero));
            var cues = Enumerable.Range(1, 81).Select(index =>
                new SubtitleCue($"c{index:D6}", index * 1_000, index * 1_000 + 900, "text " + index)).ToArray();

            var corrected = await client.CorrectAsync(cues, profile, "en", CancellationToken.None);

            Assert.Equal(2, callCount);
            Assert.Equal(cues.Select(cue => cue.Id), corrected.Keys);
            Assert.Equal("fixed:text 81", corrected["c000081"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    [Fact]
    public async Task Llm_adapter_rejects_missing_or_renamed_cue_ids()
    {
        var keyName = "CAPTIONER_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(keyName, "test-secret");
        try
        {
            var response = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = "{\"wrong-id\":\"text\"}" } } }
            });
            var client = new OpenAiLlmClient(
                new HttpClient(new DelegateHandler(_ => Task.FromResult(JsonResponse(response)))),
                TimeSpan.Zero);
            var profile = new EndpointProfile(
                "https://llm.example/v1",
                "llm-test",
                keyName,
                new EndpointCapabilities(false, false, true, 0, TimeSpan.Zero));

            await Assert.ThrowsAsync<InvalidDataException>(() => client.TranslateAsync(
                [new SubtitleCue("c000001", 0, 1_000, "hello")],
                profile,
                "zh-CN",
                CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
