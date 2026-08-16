using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Captioner.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        DesktopStartupDiagnostics.Record("process-start");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                DesktopStartupDiagnostics.ReportFailure("app-domain-unhandled", exception);
            }
        };
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            DesktopStartupDiagnostics.ReportFailure("dispatcher-unhandled", eventArgs.Exception);
            eventArgs.Handled = true;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown(1);
            }
            else
            {
                Environment.Exit(1);
            }
        };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            DesktopStartupDiagnostics.ReportFailure("startup-failed", exception);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
