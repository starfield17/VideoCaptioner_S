using Captioner.Cli;
using Captioner.Core;
using Captioner.Infrastructure;

namespace Captioner.Tests;

public sealed class CliExitCodeTests
{
    [Fact]
    public async Task Help_exits_successfully()
    {
        Assert.Equal(0, await Program.RunAsync(["--help"]));
    }

    [Fact]
    public async Task Missing_output_is_a_usage_error()
    {
        Assert.Equal(2, await Program.RunAsync(["run", "missing.mp4"]));
    }

    [Fact]
    public async Task Unknown_layout_is_a_usage_error()
    {
        var directory = Path.Combine(Path.GetTempPath(), "captioner-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "sample.srt");
            await File.WriteAllTextAsync(input, "1\n00:00:00,000 --> 00:00:01,000\nHello\n");
            var code = await Program.RunAsync([
                "run", input, "--output", directory, "--layout", "sideways", "--workspace", directory, "--config",
                Path.Combine(directory, "config.json")
            ]);
            Assert.True(code is 1 or 2, "Unknown --layout must not succeed.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Status_uses_workspace_saved_in_configuration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "captioner-cli-workspace-" + Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(directory, "config.json");
        var workspacePath = Path.Combine(directory, "recovery");
        try
        {
            var loader = new JsonConfigurationLoader(configPath);
            loader.Save(loader.CreateDefault() with { WorkspaceDirectory = workspacePath });
            var workspace = new FileJobWorkspace(workspacePath);
            await workspace.SaveBatchAsync(
                new BatchManifest(1, "saved-batch", [], DateTimeOffset.UnixEpoch),
                CancellationToken.None);

            Assert.Equal(0, await Program.RunAsync(["status", "saved-batch", "--config", configPath]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
