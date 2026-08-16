using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Captioner.Core;
using Captioner.Desktop;
using Captioner.Infrastructure;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Captioner.Desktop.Tests.TestAppBuilder))]

namespace Captioner.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class DesktopStartupTests
{
    [AvaloniaFact]
    public async Task Main_window_constructs_and_opens()
    {
        var window = new MainWindow();
        try
        {
            window.Show();

            Assert.True(window.IsVisible);
            var newBatch = Assert.IsType<NewBatchView>(window.FindControl<NewBatchView>("NewBatchPage"));
            var queue = Assert.IsType<QueueView>(window.FindControl<QueueView>("QueuePage"));
            var targetLanguage = Assert.IsType<TextBox>(newBatch.FindControl<TextBox>("TargetLanguageBox"));
            var layout = Assert.IsType<ComboBox>(newBatch.FindControl<ComboBox>("LayoutBox"));

            targetLanguage.Text = "zh-CN";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(2, layout.SelectedIndex);
            targetLanguage.Text = string.Empty;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(0, layout.SelectedIndex);

            var queueButton = Assert.IsType<Button>(window.FindControl<Button>("QueueNavButton"));
            queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(newBatch.IsVisible);
            Assert.True(queue.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Settings_dialog_uses_the_new_english_categories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "captioner-settings-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var dialog = new SettingsDialog(new JsonConfigurationLoader(Path.Combine(directory, "config.json")));
            Assert.NotNull(dialog.FindControl<ComboBox>("AsrBackendBox"));
            Assert.NotNull(dialog.FindControl<TextBox>("LlmBaseUrlBox"));
            Assert.NotNull(dialog.FindControl<TextBox>("ReferenceBox"));
            Assert.NotNull(dialog.FindControl<TextBox>("WorkspaceDirectoryBox"));
            Assert.Equal("Settings", dialog.Title);
            dialog.Close();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Subtitle_job_with_missing_required_stages_is_not_reported_complete()
    {
        var row = new JobRow("job", "/tmp/input.srt");
        var now = DateTimeOffset.UtcNow;
        row.Apply(new JobManifest(
            1,
            "job",
            "batch",
            "/tmp/input.srt",
            "input.srt",
            "hash",
            "/tmp/output.srt",
            new Dictionary<string, StageRecord>(),
            now,
            now,
            SourceKind.Subtitle));

        Assert.Equal("Waiting", row.Status);
        Assert.Equal("Segment", row.CurrentStage);
        Assert.Equal("Not needed", row.Stages[0].StateText);
        Assert.Equal("Not needed", row.Stages[1].StateText);
        Assert.Equal("Not needed", row.Stages[2].StateText);
    }

    [AvaloniaFact]
    public void Startup_log_rotates_and_redacts_credentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "captioner-desktop-log-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "desktop-startup.log");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(path, new byte[1024 * 1024]);
            var fakeApiKey = "sk-" + "test-secret-value";
            const string fakeBearerToken = "another-secret-value";
            var exception = new InvalidOperationException(
                $"Rejected {fakeApiKey} and Bearer {fakeBearerToken}.");

            DesktopStartupDiagnostics.WriteEntry(path, "test-failure", exception);

            var log = File.ReadAllText(path);
            Assert.True(File.Exists(path + ".previous"));
            Assert.Contains("stage=test-failure", log, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", log, StringComparison.Ordinal);
            Assert.DoesNotContain(fakeApiKey, log, StringComparison.Ordinal);
            Assert.DoesNotContain(fakeBearerToken, log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
