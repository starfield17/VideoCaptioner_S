using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Captioner.Core;
using Captioner.Engine;
using Captioner.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Captioner.Cli;

public static partial class Program
{
    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        var root = CreateRootCommand();
        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await Console.Error.WriteLineAsync(error.Message);
            }

            return ExitCodes.UsageError;
        }

        try
        {
            return await parseResult.InvokeAsync();
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Cancelled. The current batch can be resumed.");
            return ExitCodes.Cancelled;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return ClassifyException(exception);
        }
    }

    public static RootCommand CreateRootCommand()
    {
        var root = new RootCommand("Batch media-to-SRT captioning with local sherpa-onnx ASR and OpenAI-compatible LLMs.");
        root.Subcommands.Add(CreateRunCommand());
        root.Subcommands.Add(CreateResumeCommand());
        root.Subcommands.Add(CreateStatusCommand());
        root.Subcommands.Add(CreateDoctorCommand());
        root.Subcommands.Add(CreateConfigCommand());
        root.Subcommands.Add(CreateModelsCommand());
        root.Subcommands.Add(CreateCleanCommand());
        return root;
    }

    private static Command CreateRunCommand()
    {
        var inputs = new Argument<string[]>("inputs")
        {
            Description = "One or more media files or directories.",
            Arity = ArgumentArity.OneOrMore
        };
        var output = RequiredOption("--output", "Directory for mirrored SRT output.");
        var common = AddPipelineOptions(new Command("run", "Create and execute a resumable captioning batch."));
        common.Command.Arguments.Add(inputs);
        common.Command.Options.Add(output);

        common.Command.SetAction(async (result, cancellationToken) =>
        {
            var outputDirectory = Path.GetFullPath(result.GetRequiredValue(output));
            var targetLanguage = NullIfWhiteSpace(result.GetValue(common.TargetLanguage));
            var layout = ResolveLayout(result.GetValue(common.Layout), targetLanguage);
            var discovered = await DiscoverInputsAsync(
                result.GetRequiredValue(inputs),
                outputDirectory,
                targetLanguage,
                layout,
                cancellationToken);
            if (discovered.Count == 0)
            {
                await Console.Error.WriteLineAsync("No supported media files were found.");
                return ExitCodes.UsageError;
            }

            var collisions = discovered.GroupBy(item => item.OutputPath, PathComparer())
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (collisions.Length > 0)
            {
                await Console.Error.WriteLineAsync("More than one input maps to the same output path: " + collisions[0]);
                return ExitCodes.UsageError;
            }

            if (!result.GetValue(common.Overwrite))
            {
                var existing = discovered.FirstOrDefault(item => File.Exists(item.OutputPath));
                if (existing is not null)
                {
                    await Console.Error.WriteLineAsync($"Output already exists (use --overwrite to replace it): {existing.OutputPath}");
                    return ExitCodes.UsageError;
                }
            }

            using var host = BuildHost(result.GetValue(common.Config), result.GetValue(common.Workspace));
            var runner = host.Services.GetRequiredService<BatchRunner>();
            var options = LoadPipelineOptions(result, common, targetLanguage, layout);
            var batch = await runner.PrepareAsync(discovered, cancellationToken, options);
            await Console.Error.WriteLineAsync($"Batch prepared: {batch.BatchId}");
            var run = await runner.RunAsync(batch, options, cancellationToken);
            await WriteRunResultAsync(run, result.GetValue(common.Json));
            return run.Succeeded ? ExitCodes.Success : ExitCodes.PartialFailure;
        });

        return common.Command;
    }

    private static Command CreateResumeCommand()
    {
        var batchId = new Argument<string>("batch-id") { Description = "Batch identifier printed by run." };
        var common = AddPipelineOptions(new Command("resume", "Resume a batch from verified stage artifacts."));
        common.Command.Arguments.Add(batchId);
        common.Command.SetAction(async (result, cancellationToken) =>
        {
            using var host = BuildHost(result.GetValue(common.Config), result.GetValue(common.Workspace));
            var workspace = host.Services.GetRequiredService<IJobWorkspace>();
            var id = result.GetRequiredValue(batchId);
            var batch = await workspace.LoadBatchAsync(id, cancellationToken);
            if (batch is null)
            {
                await Console.Error.WriteLineAsync($"Batch not found: {id}");
                return ExitCodes.UsageError;
            }

            var options = LoadResumeOptions(result, common, batch);
            var run = await host.Services.GetRequiredService<BatchRunner>()
                .RunAsync(batch, options, cancellationToken);
            await WriteRunResultAsync(run, result.GetValue(common.Json));
            return run.Succeeded ? ExitCodes.Success : ExitCodes.PartialFailure;
        });
        return common.Command;
    }

    private static PipelineCommand AddPipelineOptions(Command command)
    {
        var options = new PipelineCommand(
            command,
            OptionalStringOption("--config", "Configuration file override."),
            OptionalStringOption("--workspace", "Workspace root override."),
            OptionalStringOption("--asr-profile", "Named ASR endpoint profile."),
            OptionalStringOption("--llm-profile", "Named LLM endpoint profile."),
            OptionalStringOption("--source-language", "Optional ASR language hint."),
            OptionalStringOption("--target-language", "Translate captions to this language."),
            OptionalStringOption("--layout", "source, target, or bilingual; inferred when omitted."),
            new Option<int>("--jobs") { Description = "Maximum files processed concurrently.", DefaultValueFactory = _ => 2 },
            new Option<int>("--max-cue-characters") { Description = "Preferred maximum cue length.", DefaultValueFactory = _ => 42 },
            new Option<long>("--max-cue-duration-ms") { Description = "Preferred maximum cue duration.", DefaultValueFactory = _ => 7_000 },
            new Option<bool>("--no-segment") { Description = "Disable LLM semantic boundary selection." },
            new Option<bool>("--no-correct") { Description = "Disable LLM source-caption correction." },
            new Option<bool>("--overwrite") { Description = "Atomically replace existing output SRT files." },
            new Option<bool>("--json") { Description = "Write machine-readable result JSON." });
        foreach (var option in options.AllOptions)
        {
            command.Options.Add(option);
        }

        return options;
    }

    private static PipelineOptions LoadPipelineOptions(
        ParseResult result,
        PipelineCommand options,
        string? targetLanguage,
        SubtitleLayout layout)
    {
        var configuration = new JsonConfigurationLoader(result.GetValue(options.Config)).LoadWithEnvironment();
        var asrName = NullIfWhiteSpace(result.GetValue(options.AsrProfile));
        var llmName = NullIfWhiteSpace(result.GetValue(options.LlmProfile));
        var asr = asrName is null ? configuration.Asr : configuration.GetProfile(asrName);
        var llm = llmName is null ? configuration.Llm : configuration.GetProfile(llmName);
        var jobs = result.GetValue(options.Jobs);
        var maxCharacters = result.GetValue(options.MaxCueCharacters);
        var maxDuration = result.GetValue(options.MaxCueDurationMs);
        if (jobs < 1 || maxCharacters < 1 || maxDuration < 1)
        {
            throw new ArgumentException("--jobs, --max-cue-characters, and --max-cue-duration-ms must be positive.");
        }

        return new(
            asr,
            llm,
            NullIfWhiteSpace(result.GetValue(options.SourceLanguage)),
            targetLanguage,
            layout,
            !result.GetValue(options.NoSegment),
            !result.GetValue(options.NoCorrect),
            maxCharacters,
            maxDuration,
            jobs,
            result.GetValue(options.Overwrite));
    }

    private static PipelineOptions LoadResumeOptions(
        ParseResult result,
        PipelineCommand options,
        BatchManifest batch)
    {
        if (batch.Options is null)
        {
            var targetLanguage = NullIfWhiteSpace(result.GetValue(options.TargetLanguage));
            return LoadPipelineOptions(result, options, targetLanguage, ResolveLayout(result.GetValue(options.Layout), targetLanguage));
        }

        var saved = batch.Options;
        var contentOverrides = new Option[]
        {
            options.SourceLanguage,
            options.TargetLanguage,
            options.Layout,
            options.NoSegment,
            options.NoCorrect,
            options.MaxCueCharacters,
            options.MaxCueDurationMs
        };
        if (contentOverrides.Any(option => WasSpecified(result, option)))
        {
            throw new ArgumentException(
                "A saved batch keeps its source/target languages, layout, and cue settings. Start a new run to change output content.");
        }

        var asr = saved.Asr;
        var llm = saved.Llm;
        if (WasSpecified(result, options.Config) || WasSpecified(result, options.AsrProfile) || WasSpecified(result, options.LlmProfile))
        {
            var configuration = new JsonConfigurationLoader(result.GetValue(options.Config)).LoadWithEnvironment();
            var asrName = NullIfWhiteSpace(result.GetValue(options.AsrProfile));
            var llmName = NullIfWhiteSpace(result.GetValue(options.LlmProfile));
            asr = asrName is null ? configuration.Asr : configuration.GetProfile(asrName);
            llm = llmName is null ? configuration.Llm : configuration.GetProfile(llmName);
        }

        return saved with
        {
            Asr = asr,
            Llm = llm,
            MaxFileConcurrency = WasSpecified(result, options.Jobs)
                ? result.GetValue(options.Jobs)
                : saved.MaxFileConcurrency,
            Overwrite = WasSpecified(result, options.Overwrite)
                ? result.GetValue(options.Overwrite)
                : saved.Overwrite
        };
    }

    private static IHost BuildHost(string? configPath, string? workspacePath)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var loader = new JsonConfigurationLoader(configPath);
        var configuration = loader.LoadWithEnvironment();
        builder.Services.AddSingleton(loader);
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton<IJobWorkspace>(new FileJobWorkspace(workspacePath));
        builder.Services.AddSingleton<FfmpegMediaTool>();
        builder.Services.AddSingleton<IMediaTool>(services => services.GetRequiredService<FfmpegMediaTool>());
        builder.Services.AddSingleton<IAsrClient>(_ => new RoutingAsrClient(
            configuration.ModelDirectory,
            new HttpClient { Timeout = TimeSpan.FromHours(2) }));
        builder.Services.AddSingleton<ILlmClient>(_ => new OpenAiLlmClient(new HttpClient { Timeout = TimeSpan.FromMinutes(20) }));
        builder.Services.AddSingleton<ISubtitlePublisher, SrtSubtitlePublisher>();
        builder.Services.AddSingleton<PipelineRunner>();
        builder.Services.AddSingleton<BatchRunner>();
        return builder.Build();
    }

    private static async Task<IReadOnlyList<MediaInput>> DiscoverInputsAsync(
        IReadOnlyList<string> inputPaths,
        string outputDirectory,
        string? targetLanguage,
        SubtitleLayout layout,
        CancellationToken cancellationToken)
    {
        var items = new List<MediaInput>();
        foreach (var inputPath in inputPaths)
        {
            var discovered = await MediaDiscovery.DiscoverAsync(inputPath, outputDirectory, cancellationToken: cancellationToken);
            foreach (var item in discovered)
            {
                var outputPath = SubtitleOutputPlanner.CreatePath(
                    item.RelativePath,
                    outputDirectory,
                    targetLanguage,
                    layout);
                items.Add(item with { OutputPath = outputPath });
            }
        }

        return items.GroupBy(item => item.Path, PathComparer()).Select(group => group.First()).ToArray();
    }

    private static SubtitleLayout ResolveLayout(string? value, string? targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return targetLanguage is null ? SubtitleLayout.Source : SubtitleLayout.Bilingual;
        }

        if (!Enum.TryParse<SubtitleLayout>(value, ignoreCase: true, out var layout))
        {
            throw new ArgumentException("--layout must be source, target, or bilingual.");
        }

        if (layout != SubtitleLayout.Source && targetLanguage is null)
        {
            throw new ArgumentException("--target-language is required for target or bilingual layout.");
        }

        return layout;
    }

    private static async Task WriteRunResultAsync(BatchRunResult result, bool json)
    {
        if (json)
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, OutputJson));
            return;
        }

        await Console.Out.WriteLineAsync($"Batch {result.BatchId}: {(result.Succeeded ? "completed" : "completed with failures")}");
        foreach (var job in result.Jobs)
        {
            await Console.Out.WriteLineAsync(job.Succeeded
                ? $"OK   {job.OutputPath}"
                : $"FAIL {job.JobId}: {job.Error}");
        }
    }

    private static string CurrentState(JobManifest manifest)
    {
        foreach (var stageName in StageNames.Ordered)
        {
            if (!manifest.Stages.TryGetValue(stageName, out var stage))
            {
                return "missing:" + stageName;
            }

            if (stage.Status != StageStatus.Ready)
            {
                return stageName + ":" + stage.Status.ToString().ToLowerInvariant();
            }
        }

        return "completed";
    }

    private static void CheckSecret(EndpointProfile profile, string name, ICollection<DoctorCheck> checks)
    {
        if (profile.Backend == EndpointBackend.SherpaOnnx)
        {
            checks.Add(new(name, true, "not required for local ASR"));
            return;
        }

        var present = !string.IsNullOrWhiteSpace(profile.ApiKey) ||
            (!string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable) &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(profile.ApiKeyEnvironmentVariable)));
        checks.Add(new(name, present, present ? "configured" : "not configured"));
    }

    private static async Task CheckEndpointAsync(
        string name,
        Func<CancellationToken, Task<string?>> operation,
        ICollection<DoctorCheck> checks,
        CancellationToken cancellationToken)
    {
        try
        {
            var model = await operation(cancellationToken);
            checks.Add(new(name, true, model ?? "reachable"));
        }
        catch (Exception exception)
        {
            checks.Add(new(name, false, exception.Message));
        }
    }

    private static int ClassifyException(Exception exception) => exception switch
    {
        ArgumentException or FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => ExitCodes.UsageError,
        InvalidOperationException when exception.Message.Contains("installed", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("environment variable", StringComparison.OrdinalIgnoreCase) => ExitCodes.DependencyMissing,
        _ => ExitCodes.GeneralError
    };

    private static Option<string> RequiredOption(string name, string description) => new(name)
    {
        Description = description,
        Required = true
    };

    private static Option<string?> OptionalStringOption(string name, string description) => new(name)
    {
        Description = description
    };

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool WasSpecified(ParseResult result, Option option) => result.GetResult(option) is { Implicit: false };

    private sealed record DoctorCheck(string Name, bool Ok, string Detail);

    private sealed record StatusJob(
        string JobId,
        string InputPath,
        string State,
        string? OutputPath,
        IReadOnlyDictionary<string, StageRecord>? Stages);

    private sealed record PipelineCommand(
        Command Command,
        Option<string?> Config,
        Option<string?> Workspace,
        Option<string?> AsrProfile,
        Option<string?> LlmProfile,
        Option<string?> SourceLanguage,
        Option<string?> TargetLanguage,
        Option<string?> Layout,
        Option<int> Jobs,
        Option<int> MaxCueCharacters,
        Option<long> MaxCueDurationMs,
        Option<bool> NoSegment,
        Option<bool> NoCorrect,
        Option<bool> Overwrite,
        Option<bool> Json)
    {
        public IEnumerable<Option> AllOptions =>
        [Config, Workspace, AsrProfile, LlmProfile, SourceLanguage, TargetLanguage, Layout, Jobs,
            MaxCueCharacters, MaxCueDurationMs, NoSegment, NoCorrect, Overwrite, Json];
    }
}
