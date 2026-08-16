using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Captioner.Desktop;

internal static partial class DesktopStartupDiagnostics
{
    private const long MaximumLogBytes = 1024 * 1024;
    private const uint ErrorDialogFlags = 0x00000010;
    private static readonly object Sync = new();
    private static int _failureShown;

    public static string LogPath => Environment.GetEnvironmentVariable("CAPTIONER_DESKTOP_LOG_PATH") is { } overridePath &&
        !string.IsNullOrWhiteSpace(overridePath)
        ? Path.GetFullPath(overridePath)
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Captioner",
            "logs",
            "desktop-startup.log");

    public static void Record(string stage) => WriteEntry(LogPath, stage, null);

    public static void ReportFailure(string stage, Exception exception)
    {
        WriteEntry(LogPath, stage, exception);
        if (Interlocked.Exchange(ref _failureShown, 1) != 0)
        {
            return;
        }

        var message = "Captioner could not open its desktop window.\n\n" +
            "Details were written to:\n" + LogPath;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _ = MessageBoxW(IntPtr.Zero, message, "Captioner startup error", ErrorDialogFlags);
                return;
            }
            catch
            {
            }
        }

        Console.Error.WriteLine(message);
    }

    internal static void WriteEntry(string path, string stage, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                var fullPath = Path.GetFullPath(path);
                var directory = Path.GetDirectoryName(fullPath) ??
                    throw new InvalidOperationException("Desktop log path has no directory.");
                Directory.CreateDirectory(directory);
                if (File.Exists(fullPath) && new FileInfo(fullPath).Length >= MaximumLogBytes)
                {
                    File.Move(fullPath, fullPath + ".previous", overwrite: true);
                }

                var entry = new StringBuilder()
                    .Append(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" stage=").Append(stage)
                    .Append(" version=").Append(Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown")
                    .Append(" os=").Append(RuntimeInformation.OSDescription)
                    .Append(" architecture=").Append(RuntimeInformation.ProcessArchitecture)
                    .AppendLine();
                if (exception is not null)
                {
                    entry.AppendLine(Redact(exception.ToString()));
                }

                File.AppendAllText(fullPath, entry.ToString());
            }
        }
        catch
        {
            // Diagnostics must never create a second startup failure.
        }
    }

    private static string Redact(string value) => SecretPattern().Replace(value, "[REDACTED]");

    [GeneratedRegex(@"(?i)\b(?:sk-[a-z0-9_-]{12,}|bearer\s+[a-z0-9._~+/-]{12,})")]
    private static partial Regex SecretPattern();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
