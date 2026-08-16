using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Captioner.Core;
using Captioner.Infrastructure;

namespace Captioner.Desktop;

/// <summary>Modal editor for durable endpoint, reference, and storage settings.</summary>
public sealed partial class SettingsDialog : Window
{
    private static readonly Regex SecretPattern = new(@"(?:sk-[A-Za-z0-9_-]{8,}|Bearer\s+[A-Za-z0-9._~+/=-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly JsonConfigurationLoader _loader;
    private readonly CaptionerConfiguration _initialConfiguration;
    private CancellationTokenSource? _checkCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private bool _closed;
    private bool _saved;
    private bool _asrKeyVisible;
    private bool _llmKeyVisible;

    public SettingsDialog(JsonConfigurationLoader configurationLoader)
    {
        ArgumentNullException.ThrowIfNull(configurationLoader);
        _loader = configurationLoader;
        _initialConfiguration = File.Exists(configurationLoader.ConfigPath)
            ? configurationLoader.Load()
            : configurationLoader.CreateDefault();

        InitializeComponent();
        DataContext = this;
        ConfigPathBox.Text = _loader.ConfigPath;
        LoadDraft(_initialConfiguration);

        // Do not wire selection/text events before InitializeComponent and LoadDraft.
        AsrBackendBox.SelectionChanged += AsrBackend_Changed;
        AsrModelBox.SelectionChanged += AsrModel_Changed;
        DownloadModelButton.Click += DownloadModel_Click;
        CheckAsrButton.Click += CheckAsr_Click;
        CheckLlmButton.Click += CheckLlm_Click;
        AsrApiKeyVisibilityButton.Click += ToggleAsrKey_Click;
        LlmApiKeyVisibilityButton.Click += ToggleLlmKey_Click;
        SaveButton.Click += Save_Click;
        CancelButton.Click += Cancel_Click;
        Opened += (_, _) => RefreshAsrModelStatus();
        Closed += (_, _) => DisposeOperationTokens();
    }

    public SettingsDialog() : this(new JsonConfigurationLoader())
    {
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _closed = true;
        DisposeOperationTokens();
        base.OnClosing(e);
    }

    /// <summary>Atomically persists the current draft and closes the dialog with a true result.</summary>
    internal void SaveDraft()
    {
        if (_saved)
        {
            return;
        }

        var current = File.Exists(_loader.ConfigPath) ? _loader.Load() : _initialConfiguration;
        var asr = BuildAsrProfile(current.Asr);
        var llm = BuildLlmProfile(current.Llm);
        var profiles = current.Profiles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        profiles["asr"] = asr;
        profiles["llm"] = llm;

        var modelDirectory = RequiredPath(ModelDirectoryBox.Text, "model directory");
        var workspaceDirectory = RequiredPath(WorkspaceDirectoryBox.Text, "workspace directory");
        var configuration = current with
        {
            Asr = asr,
            Llm = llm,
            Profiles = profiles,
            ModelDirectory = Path.GetFullPath(modelDirectory),
            WorkspaceDirectory = Path.GetFullPath(workspaceDirectory),
            ReferenceText = NullIfWhiteSpace(ReferenceBox.Text)
        };

        // JsonConfigurationLoader performs a write-to-temp followed by an atomic replace.
        _loader.Save(configuration);
        _saved = true;
        Close(true);
    }

    private void LoadDraft(CaptionerConfiguration configuration)
    {
        AsrBackendBox.SelectedIndex = configuration.Asr.Backend == EndpointBackend.SherpaOnnx ? 0 : 1;
        AsrBaseUrlBox.Text = configuration.Asr.BaseUrl;
        AsrRemoteModelBox.Text = configuration.Asr.Model;
        AsrApiKeyBox.Text = configuration.Asr.ApiKey;
        AsrConcurrencyBox.Value = Math.Clamp(configuration.Asr.MaxConcurrency, 1, 16);

        AsrModelBox.ItemsSource = AsrModelCatalog.All.Select(model => model.Id).ToArray();
        AsrModelBox.Text = configuration.Asr.Model;
        AsrModelBox.SelectedItem = configuration.Asr.Model;

        LlmBaseUrlBox.Text = configuration.Llm.BaseUrl;
        LlmModelBox.Text = configuration.Llm.Model;
        LlmApiKeyBox.Text = configuration.Llm.ApiKey;
        LlmConcurrencyBox.Value = Math.Clamp(configuration.Llm.MaxConcurrency, 1, 32);
        ReferenceBox.Text = configuration.ReferenceText;
        ModelDirectoryBox.Text = configuration.ModelDirectory;
        WorkspaceDirectoryBox.Text = configuration.WorkspaceDirectory ?? FileJobWorkspace.DefaultRootDirectory;

        ApplyAsrBackendVisibility();
        _asrKeyVisible = false;
        _llmKeyVisible = false;
    }

    private EndpointProfile BuildAsrProfile(EndpointProfile existing)
    {
        var remote = AsrBackendBox.SelectedIndex != 0;
        var model = remote ? Required(AsrRemoteModelBox.Text, "ASR model") : Required(AsrModelBox.Text, "local ASR model");
        var baseUrl = remote
            ? Required(AsrBaseUrlBox.Text, "ASR base URL")
            : "local://sherpa-onnx";
        var capabilities = remote
            ? EndpointCapabilities.DefaultAsr
            : new EndpointCapabilities(true, false, false, 1024L * 1024 * 1024, TimeSpan.FromMinutes(30));
        return existing with
        {
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = NullIfWhiteSpace(AsrApiKeyBox.Text),
            Capabilities = capabilities,
            MaxConcurrency = NumericValue(AsrConcurrencyBox, existing.MaxConcurrency),
            Backend = remote ? EndpointBackend.OpenAiCompatible : EndpointBackend.SherpaOnnx
        };
    }

    private EndpointProfile BuildLlmProfile(EndpointProfile existing) => existing with
    {
        BaseUrl = Required(LlmBaseUrlBox.Text, "LLM base URL"),
        Model = Required(LlmModelBox.Text, "LLM model"),
        ApiKey = NullIfWhiteSpace(LlmApiKeyBox.Text),
        MaxConcurrency = NumericValue(LlmConcurrencyBox, existing.MaxConcurrency),
        Backend = EndpointBackend.OpenAiCompatible
    };

    private async void CheckAsr_Click(object? sender, RoutedEventArgs e)
    {
        if (_checkCancellation is not null)
        {
            _checkCancellation.Cancel();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _checkCancellation = cancellation;
        CheckAsrButton.Content = "Cancel check";
        CheckAsrButton.IsEnabled = true;
        SetStatus(AsrCheckStatusText, "Checking speech endpoint…");
        try
        {
            var profile = BuildAsrProfile(_initialConfiguration.Asr);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            using var client = new RoutingAsrClient(RequiredPath(ModelDirectoryBox.Text, "model directory"), http);
            var result = await Task.Run(async () => await client.CheckAsync(profile, cancellation.Token), cancellation.Token);
            if (!_closed)
            {
                SetStatus(AsrCheckStatusText, $"Ready: {Sanitize(result ?? profile.Model)}", failure: false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed)
            {
                SetStatus(AsrCheckStatusText, "Check cancelled.");
            }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                SetStatus(AsrCheckStatusText, "Check failed: " + Sanitize(exception.Message), failure: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_checkCancellation, cancellation))
            {
                _checkCancellation = null;
            }

            cancellation.Dispose();
            if (!_closed)
            {
                CheckAsrButton.Content = "Check connection";
            }
        }
    }

    private async void CheckLlm_Click(object? sender, RoutedEventArgs e)
    {
        if (_checkCancellation is not null)
        {
            _checkCancellation.Cancel();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _checkCancellation = cancellation;
        CheckLlmButton.Content = "Cancel check";
        SetStatus(LlmCheckStatusText, "Checking language model endpoint…");
        try
        {
            var profile = BuildLlmProfile(_initialConfiguration.Llm);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var client = new OpenAiLlmClient(http);
            var result = await client.CheckAsync(profile, cancellation.Token);
            if (!_closed)
            {
                SetStatus(LlmCheckStatusText, $"Ready: {Sanitize(result ?? profile.Model)}", failure: false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed)
            {
                SetStatus(LlmCheckStatusText, "Check cancelled.");
            }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                SetStatus(LlmCheckStatusText, "Check failed: " + Sanitize(exception.Message), failure: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_checkCancellation, cancellation))
            {
                _checkCancellation = null;
            }

            cancellation.Dispose();
            if (!_closed)
            {
                CheckLlmButton.Content = "Check connection";
            }
        }
    }

    private async void DownloadModel_Click(object? sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null)
        {
            _downloadCancellation.Cancel();
            return;
        }

        var modelId = NullIfWhiteSpace(AsrModelBox.Text);
        if (modelId is null || !IsKnownModel(modelId))
        {
            SetStatus(AsrModelStatusText, "Choose a catalog model before downloading.", failure: true);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        DownloadModelButton.Content = "Cancel";
        ModelProgressBar.IsVisible = true;
        ModelProgressBar.Value = 0;
        try
        {
            using var manager = new AsrModelManager(RequiredPath(ModelDirectoryBox.Text, "model directory"));
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                if (_closed)
                {
                    return;
                }

                ModelProgressBar.Value = value.BytesTotal <= 0 ? 100 : value.BytesCompleted * 100d / value.BytesTotal;
                AsrModelStatusText.Text = value.Phase switch
                {
                    "downloading" => "Downloading model…",
                    "verifying" => "Verifying model…",
                    "extracting" => "Extracting model…",
                    "installed" => "Installed",
                    _ => value.Phase
                };
                AsrModelDetailText.Text = $"{ModelProgressBar.Value:0}% · {value.CurrentFile ?? value.ModelId}";
            });
            await manager.PullAsync(modelId, progress, cancellation.Token);
            if (!_closed)
            {
                SetStatus(AsrModelStatusText, "Installed and ready for local ASR.");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed)
            {
                SetStatus(AsrModelStatusText, "Download cancelled. Partial downloads can be resumed.");
            }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                SetStatus(AsrModelStatusText, "Download failed: " + Sanitize(exception.Message), failure: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_downloadCancellation, cancellation))
            {
                _downloadCancellation = null;
            }

            cancellation.Dispose();
            if (!_closed)
            {
                DownloadModelButton.Content = "Download";
                ModelProgressBar.IsVisible = false;
                RefreshAsrModelStatus();
            }
        }
    }

    private void AsrBackend_Changed(object? sender, SelectionChangedEventArgs e) => ApplyAsrBackendVisibility();

    private void AsrModel_Changed(object? sender, SelectionChangedEventArgs e) => RefreshAsrModelStatus();

    private void ApplyAsrBackendVisibility()
    {
        var local = AsrBackendBox.SelectedIndex == 0;
        AsrLocalPanel.IsVisible = local;
        AsrRemotePanel.IsVisible = !local;
        CheckAsrButton.Content = "Check connection";
        if (local)
        {
            AsrModelStatusText.IsVisible = true;
            RefreshAsrModelStatus();
        }
    }

    private void RefreshAsrModelStatus()
    {
        if (_closed || AsrBackendBox.SelectedIndex != 0)
        {
            return;
        }

        var id = NullIfWhiteSpace(AsrModelBox.Text);
        if (id is null || !IsKnownModel(id))
        {
            AsrModelStatusText.Text = "Custom model or no model selected";
            AsrModelDetailText.Text = "Choose a catalog model to enable managed downloads.";
            DownloadModelButton.IsEnabled = false;
            return;
        }

        var model = AsrModelCatalog.Get(id);
        using var manager = new AsrModelManager(RequiredPath(ModelDirectoryBox.Text, "model directory"));
        var installed = manager.IsInstalled(model.Id);
        AsrModelStatusText.Text = installed ? "Installed · ready for local ASR" : "Not installed";
        AsrModelDetailText.Text = $"{FormatBytes(model.DownloadSizeBytes)} · {model.License} · revision {model.Revision[..Math.Min(10, model.Revision.Length)]}";
        DownloadModelButton.Content = installed ? "Installed" : "Download";
        DownloadModelButton.IsEnabled = !installed && _downloadCancellation is null;
    }

    private void ToggleAsrKey_Click(object? sender, RoutedEventArgs e)
    {
        _asrKeyVisible = !_asrKeyVisible;
        AsrApiKeyBox.PasswordChar = _asrKeyVisible ? '\0' : '●';
        AsrApiKeyVisibilityButton.Content = _asrKeyVisible ? "Hide" : "Show";
    }

    private void ToggleLlmKey_Click(object? sender, RoutedEventArgs e)
    {
        _llmKeyVisible = !_llmKeyVisible;
        LlmApiKeyBox.PasswordChar = _llmKeyVisible ? '\0' : '●';
        LlmApiKeyVisibilityButton.Content = _llmKeyVisible ? "Hide" : "Show";
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveDraft();
        }
        catch (Exception exception)
        {
            SetStatus(StatusText, "Could not save settings: " + Sanitize(exception.Message), failure: true);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void DisposeOperationTokens()
    {
        _checkCancellation?.Cancel();
        _downloadCancellation?.Cancel();
    }

    private void SetStatus(TextBlock target, string message, bool failure = false)
    {
        if (_closed)
        {
            return;
        }

        target.Text = Sanitize(message);
        var resourceKey = failure ? "DangerBrush" : "MutedBrush";
        target.Foreground = Application.Current?.TryGetResource(resourceKey, out var brush) == true && brush is IBrush resolved
            ? resolved
            : new SolidColorBrush(failure ? Color.Parse("#C94B5F") : Color.Parse("#68758B"));
    }

    private static int NumericValue(NumericUpDown control, int fallback) =>
        Math.Clamp(decimal.ToInt32(control.Value ?? fallback), 1, 32);

    private static string Required(string? value, string name) =>
        NullIfWhiteSpace(value) ?? throw new ArgumentException($"Enter a {name}.");

    private static string RequiredPath(string? value, string name) => Path.GetFullPath(Required(value, name));

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool IsKnownModel(string id) => AsrModelCatalog.All.Any(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

    private string Sanitize(string? value)
    {
        var message = value ?? string.Empty;
        foreach (var secret in new[] { AsrApiKeyBox?.Text, LlmApiKeyBox?.Text })
        {
            if (!string.IsNullOrWhiteSpace(secret))
            {
                message = message.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
        }

        return SecretPattern.Replace(message, "[REDACTED]");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        }

        return $"{bytes / (1024d * 1024):0} MB";
    }
}
