using System.CommandLine;
using System.Text.Json;
using Captioner.Infrastructure;

namespace Captioner.Cli;

public static partial class Program
{
    private static Command CreateStatusCommand()
    {
        var command = new Command("status", "Show saved batch and stage state.");
        var batchId = new Argument<string?>("batch-id")
        {
            Description = "Optional batch identifier. Omit to list batches.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var workspaceOption = OptionalStringOption("--workspace", "Workspace root override.");
        var json = new Option<bool>("--json") { Description = "Write machine-readable JSON." };
        command.Arguments.Add(batchId);
        command.Options.Add(workspaceOption);
        command.Options.Add(json);
        command.SetAction(async (result, cancellationToken) =>
        {
            var workspace = new FileJobWorkspace(result.GetValue(workspaceOption));
            var id = NullIfWhiteSpace(result.GetValue(batchId));
            if (id is null)
            {
                var ids = await workspace.ListBatchIdsAsync(cancellationToken);
                if (result.GetValue(json))
                {
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { batches = ids }, OutputJson));
                }
                else if (ids.Count == 0)
                {
                    await Console.Out.WriteLineAsync("No saved batches.");
                }
                else
                {
                    foreach (var value in ids)
                    {
                        await Console.Out.WriteLineAsync(value);
                    }
                }

                return ExitCodes.Success;
            }

            var batch = await workspace.LoadBatchAsync(id, cancellationToken);
            if (batch is null)
            {
                await Console.Error.WriteLineAsync($"Batch not found: {id}");
                return ExitCodes.UsageError;
            }

            var jobs = new List<StatusJob>();
            foreach (var job in batch.Jobs)
            {
                var manifest = await workspace.LoadJobAsync(job.JobId, cancellationToken);
                jobs.Add(new(
                    job.JobId,
                    job.InputPath,
                    manifest is null ? "missing" : CurrentState(manifest),
                    manifest?.OutputPath,
                    manifest?.Stages));
            }

            if (result.GetValue(json))
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { batch.BatchId, batch.CreatedAt, jobs }, OutputJson));
            }
            else
            {
                await Console.Out.WriteLineAsync($"Batch {batch.BatchId} ({batch.CreatedAt:u})");
                foreach (var job in jobs)
                {
                    await Console.Out.WriteLineAsync($"{job.State,-24} {job.InputPath}");
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateDoctorCommand()
    {
        var command = new Command("doctor", "Check local dependencies, configuration, and optionally endpoints.");
        var config = OptionalStringOption("--config", "Configuration file override.");
        var checkApi = new Option<bool>("--check-api") { Description = "Also contact the configured ASR and LLM endpoints." };
        var json = new Option<bool>("--json") { Description = "Write machine-readable JSON." };
        command.Options.Add(config);
        command.Options.Add(checkApi);
        command.Options.Add(json);
        command.SetAction(async (result, cancellationToken) =>
        {
            var checks = new List<DoctorCheck>();
            var ffmpeg = new FfmpegMediaTool();
            try
            {
                await ffmpeg.CheckAsync(cancellationToken);
                checks.Add(new("ffmpeg", true, "ffmpeg and ffprobe are available"));
            }
            catch (Exception exception)
            {
                checks.Add(new("ffmpeg", false, exception.Message));
            }

            CaptionerConfiguration? configuration = null;
            try
            {
                var loader = new JsonConfigurationLoader(result.GetValue(config));
                if (!File.Exists(loader.ConfigPath))
                {
                    throw new FileNotFoundException("Configuration is missing. Run 'captioner config init' first.", loader.ConfigPath);
                }

                configuration = loader.LoadWithEnvironment();
                checks.Add(new("config", true, loader.ConfigPath));
                CheckSecret(configuration.Asr, "asr-secret", checks);
                CheckSecret(configuration.Llm, "llm-secret", checks);
                if (configuration.Asr.Backend == Captioner.Core.EndpointBackend.SherpaOnnx)
                {
                    using var models = new AsrModelManager(configuration.ModelDirectory);
                    var installed = models.IsInstalled(configuration.Asr.Model);
                    checks.Add(new("asr-model", installed, installed
                        ? $"{configuration.Asr.Model} is installed"
                        : $"{configuration.Asr.Model} is missing; run 'captioner models pull {configuration.Asr.Model}'"));
                }
            }
            catch (Exception exception)
            {
                checks.Add(new("config", false, exception.Message));
            }

            if (result.GetValue(checkApi) && configuration is not null)
            {
                using var asr = new RoutingAsrClient(configuration.ModelDirectory);
                await CheckEndpointAsync("asr-api", ct => asr.CheckAsync(configuration.Asr, ct), checks, cancellationToken);
                await CheckEndpointAsync("llm-api", ct => new OpenAiLlmClient().CheckAsync(configuration.Llm, ct), checks, cancellationToken);
            }

            if (result.GetValue(json))
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { checks }, OutputJson));
            }
            else
            {
                foreach (var check in checks)
                {
                    await Console.Out.WriteLineAsync($"{(check.Ok ? "OK" : "FAIL"),-4} {check.Name,-12} {check.Detail}");
                }
            }

            return checks.All(check => check.Ok) ? ExitCodes.Success : ExitCodes.DependencyMissing;
        });
        return command;
    }

    private static Command CreateConfigCommand()
    {
        var command = new Command("config", "Initialize or inspect endpoint configuration.");
        command.Subcommands.Add(CreateConfigLeaf("init", "Create the default configuration if absent.", loader =>
            new { path = loader.Initialize(), ready = true }));
        command.Subcommands.Add(CreateConfigLeaf("path", "Print the configuration path.", loader =>
            new { path = loader.ConfigPath }));
        command.Subcommands.Add(CreateConfigLeaf("show", "Show configuration with API keys redacted.", loader =>
            new { path = loader.ConfigPath, configuration = loader.GetRedactedView() }));
        command.Subcommands.Add(CreateConfigSetLlmCommand());
        return command;
    }

    private static Command CreateConfigSetLlmCommand()
    {
        var command = new Command("set-llm", "Save OpenAI-compatible LLM settings, including an optional plaintext API key.");
        var config = OptionalStringOption("--config", "Configuration file override.");
        var baseUrl = RequiredOption("--base-url", "LLM API base URL.");
        var model = RequiredOption("--model", "LLM model name.");
        var apiKeyStdin = new Option<bool>("--api-key-stdin") { Description = "Read the plaintext API key from one line on standard input." };
        command.Options.Add(config);
        command.Options.Add(baseUrl);
        command.Options.Add(model);
        command.Options.Add(apiKeyStdin);
        command.SetAction(async (result, cancellationToken) =>
        {
            var loader = new JsonConfigurationLoader(result.GetValue(config));
            var configuration = loader.Load();
            var apiKey = configuration.Llm.ApiKey;
            if (result.GetValue(apiKeyStdin))
            {
                apiKey = NullIfWhiteSpace(await Console.In.ReadLineAsync(cancellationToken)) ??
                    throw new ArgumentException("Standard input did not contain an API key.");
            }

            var llm = configuration.Llm with
            {
                BaseUrl = result.GetRequiredValue(baseUrl),
                Model = result.GetRequiredValue(model),
                ApiKey = apiKey
            };
            var profiles = configuration.Profiles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            profiles["llm"] = llm;
            loader.Save(configuration with { Llm = llm, Profiles = profiles });
            await Console.Out.WriteLineAsync("LLM settings saved. The API key is configured and will not be displayed.");
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateConfigLeaf(string name, string description, Func<JsonConfigurationLoader, object> operation)
    {
        var command = new Command(name, description);
        var config = OptionalStringOption("--config", "Configuration file override.");
        command.Options.Add(config);
        command.SetAction(result =>
        {
            var value = operation(new JsonConfigurationLoader(result.GetValue(config)));
            Console.Out.WriteLine(JsonSerializer.Serialize(value, OutputJson));
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateCleanCommand()
    {
        var command = new Command("clean", "Delete one saved batch and its job-local artifacts.");
        var batchId = new Argument<string>("batch-id");
        var workspace = OptionalStringOption("--workspace", "Workspace root override.");
        command.Arguments.Add(batchId);
        command.Options.Add(workspace);
        command.SetAction(async (result, cancellationToken) =>
        {
            var store = new FileJobWorkspace(result.GetValue(workspace));
            var id = result.GetRequiredValue(batchId);
            if (await store.LoadBatchAsync(id, cancellationToken) is null)
            {
                await Console.Error.WriteLineAsync($"Batch not found: {id}");
                return ExitCodes.UsageError;
            }

            await store.CleanBatchAsync(id, cancellationToken);
            await Console.Out.WriteLineAsync($"Deleted batch {id} and its resumable artifacts.");
            return ExitCodes.Success;
        });
        return command;
    }
}
