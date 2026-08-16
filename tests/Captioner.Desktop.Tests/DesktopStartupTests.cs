using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Captioner.Desktop;
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
            var backend = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("AsrBackendBox"));
            var model = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("AsrModelBox"));
            var targetLanguage = Assert.IsType<TextBox>(window.FindControl<TextBox>("TargetLanguageBox"));
            var layout = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("LayoutBox"));

            backend.SelectedIndex = 1;
            backend.SelectedIndex = 0;
            model.SelectedItem = "whisper-base";
            targetLanguage.Text = "zh-CN";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(2, layout.SelectedIndex);
            targetLanguage.Text = string.Empty;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(0, layout.SelectedIndex);
        }
        finally
        {
            window.Close();
        }
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
