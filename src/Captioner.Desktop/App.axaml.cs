using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Captioner.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        DesktopStartupDiagnostics.Record("framework-initializing");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow();
            DesktopStartupDiagnostics.Record("window-created");
        }

        base.OnFrameworkInitializationCompleted();
        DesktopStartupDiagnostics.Record("framework-ready");
    }
}
