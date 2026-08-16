using Captioner.Core;
using Captioner.Engine;

namespace Captioner.Infrastructure;

public sealed class RoutingAsrClient : IAsrClient, IDisposable
{
    private readonly OpenAiAsrClient _remote;
    private readonly SherpaOnnxAsrClient _local;

    public RoutingAsrClient(string modelDirectory, HttpClient? remoteHttpClient = null)
    {
        _remote = new OpenAiAsrClient(remoteHttpClient);
        _local = new SherpaOnnxAsrClient(modelDirectory);
    }

    public Task<TranscriptDocument> TranscribeAsync(
        MediaChunk chunk,
        EndpointProfile profile,
        string? language,
        CancellationToken cancellationToken) => profile.Backend switch
        {
            EndpointBackend.SherpaOnnx => _local.TranscribeAsync(chunk, profile, language, cancellationToken),
            EndpointBackend.OpenAiCompatible => _remote.TranscribeAsync(chunk, profile, language, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), $"Unsupported ASR backend '{profile.Backend}'.")
        };

    public Task<string?> CheckAsync(EndpointProfile profile, CancellationToken cancellationToken) => profile.Backend switch
    {
        EndpointBackend.SherpaOnnx => _local.CheckAsync(profile, cancellationToken),
        EndpointBackend.OpenAiCompatible => _remote.CheckAsync(profile, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), $"Unsupported ASR backend '{profile.Backend}'.")
    };

    public void Dispose() => _local.Dispose();
}
