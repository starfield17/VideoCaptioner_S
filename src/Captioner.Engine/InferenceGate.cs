namespace Captioner.Engine;

public sealed class InferenceGate : IDisposable
{
    private readonly SemaphoreSlim _asr;
    private readonly SemaphoreSlim _llm;

    public InferenceGate(int asrConcurrency, int llmConcurrency)
    {
        _asr = new(Math.Max(1, asrConcurrency));
        _llm = new(Math.Max(1, llmConcurrency));
    }

    public async Task<T> RunAsrAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _asr.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _asr.Release();
        }
    }

    public async Task<T> RunLlmAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _llm.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _llm.Release();
        }
    }

    public void Dispose()
    {
        _asr.Dispose();
        _llm.Dispose();
    }
}
