using System.CommandLine;
using System.Text.Json;
using Captioner.Infrastructure;

namespace Captioner.Cli;

public static partial class Program
{
    private static Command CreateModelsCommand()
    {
        var command = new Command("models", "List, download, and remove local sherpa-onnx speech models.");
        command.Subcommands.Add(CreateModelsListCommand());
        command.Subcommands.Add(CreateModelsPullCommand());
        command.Subcommands.Add(CreateModelsRemoveCommand());
        command.Subcommands.Add(CreateModelsPathCommand());
        return command;
    }

    private static Command CreateModelsListCommand()
    {
        var command = new Command("list", "List the pinned local ASR model catalog and installation state.");
        var config = OptionalStringOption("--config", "Configuration file override.");
        var json = new Option<bool>("--json") { Description = "Write machine-readable JSON." };
        command.Options.Add(config);
        command.Options.Add(json);
        command.SetAction(result =>
        {
            var configuration = new JsonConfigurationLoader(result.GetValue(config)).LoadWithEnvironment();
            using var manager = new AsrModelManager(configuration.ModelDirectory);
            var models = manager.List().Select(item => new
            {
                item.Definition.Id,
                item.Definition.DisplayName,
                Family = item.Definition.Family.ToString(),
                item.Definition.License,
                item.Definition.Revision,
                SizeBytes = item.Definition.DownloadSizeBytes,
                item.Installed,
                item.Directory
            }).ToArray();
            if (result.GetValue(json))
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { models }, OutputJson));
            }
            else
            {
                foreach (var model in models)
                {
                    Console.Out.WriteLine(
                        $"{(model.Installed ? "installed" : "missing"),-10} {model.Id,-24} {FormatBytes(model.SizeBytes),9}  {model.License,-10} {model.DisplayName}");
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateModelsPullCommand()
    {
        var command = new Command("pull", "Explicitly download and verify one local ASR model.");
        var modelId = new Argument<string>("model-id");
        var config = OptionalStringOption("--config", "Configuration file override.");
        command.Arguments.Add(modelId);
        command.Options.Add(config);
        command.SetAction(async (result, cancellationToken) =>
        {
            var configuration = new JsonConfigurationLoader(result.GetValue(config)).LoadWithEnvironment();
            using var manager = new AsrModelManager(configuration.ModelDirectory);
            var lastPercent = -1;
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                var percent = value.BytesTotal == 0 ? 100 : (int)(value.BytesCompleted * 100 / value.BytesTotal);
                if (percent != lastPercent || value.Phase is "installed" or "extracting" or "verifying")
                {
                    lastPercent = percent;
                    Console.Error.WriteLine($"{value.Phase,-11} {percent,3}% {value.CurrentFile ?? value.ModelId}");
                }
            });
            var id = result.GetRequiredValue(modelId);
            await manager.PullAsync(id, progress, cancellationToken);
            await Console.Out.WriteLineAsync($"Installed {id} in {manager.GetModelDirectory(id)}");
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateModelsRemoveCommand()
    {
        var command = new Command("remove", "Remove one installed model and any partial download for it.");
        var modelId = new Argument<string>("model-id");
        var config = OptionalStringOption("--config", "Configuration file override.");
        command.Arguments.Add(modelId);
        command.Options.Add(config);
        command.SetAction(result =>
        {
            var configuration = new JsonConfigurationLoader(result.GetValue(config)).LoadWithEnvironment();
            using var manager = new AsrModelManager(configuration.ModelDirectory);
            var id = result.GetRequiredValue(modelId);
            manager.Remove(id);
            Console.Out.WriteLine($"Removed local model {id}.");
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateModelsPathCommand()
    {
        var command = new Command("path", "Print the configured local model directory.");
        var config = OptionalStringOption("--config", "Configuration file override.");
        command.Options.Add(config);
        command.SetAction(result =>
        {
            Console.Out.WriteLine(new JsonConfigurationLoader(result.GetValue(config)).LoadWithEnvironment().ModelDirectory);
            return ExitCodes.Success;
        });
        return command;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }
}
