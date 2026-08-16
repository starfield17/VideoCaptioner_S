using System.Security.Cryptography;
using System.Text;
using Captioner.Core;

namespace Captioner.Engine;

public sealed class BatchRunner(PipelineRunner pipeline, IJobWorkspace workspace)
{
    public async Task<BatchManifest> PrepareAsync(
        IReadOnlyList<MediaInput> inputs,
        CancellationToken cancellationToken,
        PipelineOptions? options = null)
    {
        var batchId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var batchJobs = new List<BatchJob>(inputs.Count);
        var jobIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var input in inputs)
        {
            // A job directory is owned by one batch. Keeping that ownership in the
            // identifier prevents a later run of the same media from overwriting the
            // manifest and artifacts needed to resume an earlier batch.
            var jobId = CreateJobId(batchId, input);
            if (!jobIds.Add(jobId))
            {
                throw new ArgumentException($"Duplicate media identity in batch: {input.RelativePath}", nameof(inputs));
            }
            var stages = StageNames.Ordered.ToDictionary(
                name => name,
                name => new StageRecord(name, StageStatus.Pending, string.Empty),
                StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            var manifest = new JobManifest(
                1,
                jobId,
                batchId,
                input.Path,
                input.RelativePath,
                input.Sha256,
                input.OutputPath,
                stages,
                now,
                now,
                input.Kind);
            await workspace.SaveJobAsync(manifest, cancellationToken);
            batchJobs.Add(new(jobId, input.Path, input.RelativePath));
        }

        var batch = new BatchManifest(1, batchId, batchJobs, DateTimeOffset.UtcNow, options);
        await workspace.SaveBatchAsync(batch, cancellationToken);
        return batch;
    }

    public async Task<BatchRunResult> RunAsync(
        BatchManifest batch,
        PipelineOptions options,
        CancellationToken cancellationToken)
    {
        using var fileGate = new SemaphoreSlim(Math.Max(1, options.MaxFileConcurrency));
        using var inferenceGate = new InferenceGate(
            options.Asr.MaxConcurrency,
            options.Llm.MaxConcurrency);

        var tasks = batch.Jobs.Select(async batchJob =>
        {
            await fileGate.WaitAsync(cancellationToken);
            try
            {
                var manifest = await workspace.LoadJobAsync(batchJob.JobId, cancellationToken);
                if (manifest is null)
                {
                    return new JobRunResult(batchJob.JobId, false, null, "Job manifest not found.");
                }

                try
                {
                    var result = await pipeline.RunAsync(manifest, options, inferenceGate, cancellationToken);
                    return new JobRunResult(batchJob.JobId, true, result.OutputPath, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return new JobRunResult(batchJob.JobId, false, null, exception.Message);
                }
            }
            finally
            {
                fileGate.Release();
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        return new(batch.BatchId, results);
    }

    private static string CreateJobId(string batchId, MediaInput input)
    {
        var value = Encoding.UTF8.GetBytes(input.Sha256 + "\n" + input.RelativePath.Replace('\\', '/'));
        return batchId + "-" + Convert.ToHexStringLower(SHA256.HashData(value))[..20];
    }
}
