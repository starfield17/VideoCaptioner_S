using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Captioner.Core;
using Captioner.Engine;
using Captioner.Infrastructure;

namespace Captioner.Desktop;

public sealed partial class MainWindow : Window
{
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _modelCancellation;
    private bool _targetWasEmpty = true;
    private bool _isApplyingSettings;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        OutputPathBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Captioner");
        ConfigPathBox.Text = new JsonConfigurationLoader().ConfigPath;
        WorkspacePathBox.Text = new FileJobWorkspace().RootDirectory;
        AsrModelBox.ItemsSource = AsrModelCatalog.All.Select(model => model.Id).ToArray();
        TargetLanguageBox.TextChanged += TargetLanguage_Changed;
        AsrBackendBox.SelectionChanged += AsrBackend_Changed;
        AsrModelBox.SelectionChanged += AsrModel_Changed;
        Opened += OnOpened;
    }

    public ObservableCollection<JobRow> Jobs { get; } = [];

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _runCancellation?.Cancel();
        _modelCancellation?.Cancel();
        base.OnClosing(e);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        DesktopStartupDiagnostics.Record("window-opened");
        try
        {
            LoadSettings();
            var ids = await CreateWorkspace().ListBatchIdsAsync(CancellationToken.None);
            BatchIdBox.Text = ids.LastOrDefault();
            RefreshModelStatus();
        }
        catch (Exception exception)
        {
            SetStatus("Configuration or workspace is unavailable: " + exception.Message, failure: true);
        }
    }

    private async void BrowseFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose media files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Media files")
                {
                    Patterns = MediaDiscovery.SupportedExtensions.Select(extension => "*" + extension).ToArray()
                }
            ]
        });
        var paths = files.Select(file => file.TryGetLocalPath()).Where(path => path is not null).ToArray();
        if (paths.Length > 0)
        {
            InputPathBox.Text = string.Join("; ", paths);
        }
    }

    private async void BrowseInputFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a media folder",
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            InputPathBox.Text = path;
        }
    }

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the SRT output folder",
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            OutputPathBox.Text = path;
        }
    }

    private void TargetLanguage_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        var isEmpty = string.IsNullOrWhiteSpace(TargetLanguageBox.Text);
        if (_targetWasEmpty && !isEmpty)
        {
            LayoutBox.SelectedIndex = 2;
        }
        else if (!_targetWasEmpty && isEmpty)
        {
            LayoutBox.SelectedIndex = 0;
        }

        _targetWasEmpty = isEmpty;
    }

    private void AsrBackend_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        var local = AsrBackendBox.SelectedIndex == 0;
        if (local)
        {
            AsrBaseUrlBox.Text = "local://sherpa-onnx";
            if (!AsrModelCatalog.All.Any(model => string.Equals(model.Id, SelectedModelId(), StringComparison.OrdinalIgnoreCase)))
            {
                AsrModelBox.SelectedItem = "whisper-small";
            }
        }
        else if (string.IsNullOrWhiteSpace(AsrBaseUrlBox.Text) || AsrBaseUrlBox.Text.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            AsrBaseUrlBox.Text = "https://api.openai.com/v1";
        }

        RefreshModelStatus();
    }

    private void AsrModel_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isApplyingSettings)
        {
            RefreshModelStatus();
        }
    }

    private void InitializeConfig_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var loader = new JsonConfigurationLoader(NullIfWhiteSpace(ConfigPathBox.Text));
            ConfigPathBox.Text = loader.Initialize();
            LoadSettings();
            SetStatus("Configuration is ready. API keys can be stored as plaintext in this file.");
        }
        catch (Exception exception)
        {
            SetStatus("Could not initialize configuration: " + exception.Message, failure: true);
        }
    }

    private void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettingsCore();
            SetStatus("Inference and storage settings saved.");
            RefreshModelStatus();
        }
        catch (Exception exception)
        {
            SetStatus("Could not save settings: " + exception.Message, failure: true);
        }
    }

    private async void DownloadModel_Click(object? sender, RoutedEventArgs e)
    {
        if (_modelCancellation is not null)
        {
            _modelCancellation.Cancel();
            return;
        }

        try
        {
            var modelId = SelectedModelId() ?? throw new ArgumentException("Choose a local speech model first.");
            _ = AsrModelCatalog.Get(modelId);
            SaveSettingsCore();
            _modelCancellation = new CancellationTokenSource();
            DownloadModelButton.Content = "Cancel";
            ModelProgressBar.IsVisible = true;
            ModelProgressBar.Value = 0;
            var configuration = new JsonConfigurationLoader(NullIfWhiteSpace(ConfigPathBox.Text)).LoadWithEnvironment();
            using var manager = new AsrModelManager(configuration.ModelDirectory);
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                ModelProgressBar.Value = value.BytesTotal == 0 ? 100 : value.BytesCompleted * 100d / value.BytesTotal;
                ModelStatusText.Text = value.Phase switch
                {
                    "downloading" => "Downloading model",
                    "verifying" => "Verifying SHA-256",
                    "extracting" => "Extracting model",
                    "installed" => "Model installed",
                    _ => value.Phase
                };
                ModelDetailText.Text = $"{ModelProgressBar.Value:0}% · {value.CurrentFile ?? value.ModelId}";
            });
            await manager.PullAsync(modelId, progress, _modelCancellation.Token);
            SetStatus($"Local speech model '{modelId}' is ready.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Model download paused. Run Download again to resume.");
        }
        catch (Exception exception)
        {
            SetStatus("Model download failed: " + exception.Message, failure: true);
        }
        finally
        {
            _modelCancellation?.Dispose();
            _modelCancellation = null;
            DownloadModelButton.Content = "Download";
            ModelProgressBar.IsVisible = false;
            RefreshModelStatus();
        }
    }

    private async void Run_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(resume: false);

    private async void Resume_Click(object? sender, RoutedEventArgs e) => await ExecuteAsync(resume: true);

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        SetStatus("Stopping. Completed stages remain available for resume.");
    }

    private async Task ExecuteAsync(bool resume)
    {
        if (_runCancellation is not null)
        {
            return;
        }

        _runCancellation = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            SaveSettingsCore();
            var cancellationToken = _runCancellation.Token;
            using var runtime = CreateRuntime();
            var targetLanguage = NullIfWhiteSpace(TargetLanguageBox.Text);
            var layout = ResolveLayout(targetLanguage);
            var options = CreateOptions(runtime.Configuration, targetLanguage, layout);
            BatchManifest batch;
            if (resume)
            {
                var batchId = NullIfWhiteSpace(BatchIdBox.Text) ??
                    throw new ArgumentException("Enter the batch ID to resume.");
                batch = await runtime.Workspace.LoadBatchAsync(batchId, cancellationToken) ??
                    throw new ArgumentException("Batch not found: " + batchId);
                if (batch.Options is { } saved)
                {
                    options = saved with
                    {
                        Asr = runtime.Configuration.Asr,
                        Llm = runtime.Configuration.Llm
                    };
                    ApplyOptions(options);
                }
            }
            else
            {
                var inputs = ParseInputPaths();
                var outputDirectory = NullIfWhiteSpace(OutputPathBox.Text) ??
                    throw new ArgumentException("Choose an SRT output folder.");
                var media = await DiscoverAsync(inputs, outputDirectory, targetLanguage, layout, cancellationToken);
                if (media.Count == 0)
                {
                    throw new ArgumentException("No supported media files were found.");
                }

                EnsureDistinctOutputs(media);
                if (!options.Overwrite && media.FirstOrDefault(item => File.Exists(item.OutputPath)) is { } existing)
                {
                    throw new IOException("Output already exists. Enable Replace SRT to continue: " + existing.OutputPath);
                }

                batch = await runtime.Runner.PrepareAsync(media, cancellationToken, options);
                BatchIdBox.Text = batch.BatchId;
            }

            PopulateJobs(batch);
            SetStatus($"Batch {batch.BatchId} is running. Keep this ID to resume later.", batch.Jobs.Count);
            var runTask = runtime.Runner.RunAsync(batch, options, cancellationToken);
            while (!runTask.IsCompleted)
            {
                await RefreshJobsAsync(runtime.Workspace, batch, CancellationToken.None);
                await Task.WhenAny(runTask, Task.Delay(350, CancellationToken.None));
            }

            var result = await runTask;
            await RefreshJobsAsync(runtime.Workspace, batch, CancellationToken.None);
            var failed = result.Jobs.Count(job => !job.Succeeded);
            SetStatus(failed == 0
                ? $"Batch {batch.BatchId} completed with {result.Jobs.Count} SRT files."
                : $"Batch completed with {failed} failed jobs. Fix the issue and resume.",
                batch.Jobs.Count,
                failed > 0);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped. Enter the batch ID and choose Resume to continue.");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, failure: true);
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            SetBusy(false);
        }
    }

    private Runtime CreateRuntime()
    {
        var configuration = new JsonConfigurationLoader(NullIfWhiteSpace(ConfigPathBox.Text)).LoadWithEnvironment();
        var workspace = CreateWorkspace();
        var asrHttp = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        var llmHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        var asr = new RoutingAsrClient(configuration.ModelDirectory, asrHttp);
        var pipeline = new PipelineRunner(
            new FfmpegMediaTool(),
            asr,
            new OpenAiLlmClient(llmHttp),
            workspace,
            new SrtSubtitlePublisher());
        return new(configuration, workspace, new BatchRunner(pipeline, workspace), asr, asrHttp, llmHttp);
    }

    private void LoadSettings()
    {
        _isApplyingSettings = true;
        try
        {
            var configuration = new JsonConfigurationLoader(NullIfWhiteSpace(ConfigPathBox.Text)).Load();
            AsrBackendBox.SelectedIndex = configuration.Asr.Backend == EndpointBackend.SherpaOnnx ? 0 : 1;
            AsrBaseUrlBox.Text = configuration.Asr.BaseUrl;
            AsrApiKeyBox.Text = configuration.Asr.ApiKey;
            AsrModelBox.SelectedItem = configuration.Asr.Model;
            AsrModelBox.Text = configuration.Asr.Model;
            LlmBaseUrlBox.Text = configuration.Llm.BaseUrl;
            LlmModelBox.Text = configuration.Llm.Model;
            LlmApiKeyBox.Text = configuration.Llm.ApiKey;
            ModelDirectoryBox.Text = configuration.ModelDirectory;
            _targetWasEmpty = string.IsNullOrWhiteSpace(TargetLanguageBox.Text);
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void SaveSettingsCore()
    {
        var loader = new JsonConfigurationLoader(NullIfWhiteSpace(ConfigPathBox.Text));
        var configuration = loader.Load();
        var backend = AsrBackendBox.SelectedIndex == 0 ? EndpointBackend.SherpaOnnx : EndpointBackend.OpenAiCompatible;
        var model = SelectedModelId() ?? throw new ArgumentException("Choose an ASR model.");
        var asr = configuration.Asr with
        {
            Backend = backend,
            BaseUrl = NullIfWhiteSpace(AsrBaseUrlBox.Text) ??
                (backend == EndpointBackend.SherpaOnnx ? "local://sherpa-onnx" : throw new ArgumentException("Enter an ASR base URL.")),
            Model = model,
            ApiKey = NullIfWhiteSpace(AsrApiKeyBox.Text),
            Capabilities = backend == EndpointBackend.SherpaOnnx
                ? new(true, false, false, 1024L * 1024 * 1024, TimeSpan.FromMinutes(30))
                : EndpointCapabilities.DefaultAsr
        };
        var llm = configuration.Llm with
        {
            Backend = EndpointBackend.OpenAiCompatible,
            BaseUrl = NullIfWhiteSpace(LlmBaseUrlBox.Text) ?? throw new ArgumentException("Enter an LLM base URL."),
            Model = NullIfWhiteSpace(LlmModelBox.Text) ?? throw new ArgumentException("Enter an LLM model."),
            ApiKey = NullIfWhiteSpace(LlmApiKeyBox.Text)
        };
        var profiles = configuration.Profiles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        profiles["asr"] = asr;
        profiles["llm"] = llm;
        loader.Save(new(
            asr,
            llm,
            profiles,
            Path.GetFullPath(NullIfWhiteSpace(ModelDirectoryBox.Text) ?? AsrModelCatalog.DefaultModelDirectory)));
    }

    private void RefreshModelStatus()
    {
        if (AsrBackendBox.SelectedIndex != 0)
        {
            ModelStatusText.Text = "Remote ASR endpoint";
            ModelDetailText.Text = "Local model downloads are disabled for this backend.";
            DownloadModelButton.IsEnabled = false;
            return;
        }

        var modelId = SelectedModelId();
        AsrModelDefinition? model = null;
        try
        {
            model = modelId is null ? null : AsrModelCatalog.Get(modelId);
        }
        catch (KeyNotFoundException)
        {
        }

        if (model is null)
        {
            ModelStatusText.Text = "Choose a catalog model";
            ModelDetailText.Text = "Local ASR uses explicitly downloaded sherpa-onnx models.";
            DownloadModelButton.IsEnabled = false;
            return;
        }

        var directory = NullIfWhiteSpace(ModelDirectoryBox.Text) ?? AsrModelCatalog.DefaultModelDirectory;
        using var manager = new AsrModelManager(directory);
        var installed = manager.IsInstalled(model.Id);
        ModelStatusText.Text = installed ? "Installed · ready for local ASR" : "Not installed";
        ModelDetailText.Text = $"{FormatBytes(model.DownloadSizeBytes)} · {model.License} · revision {model.Revision[..Math.Min(10, model.Revision.Length)]}";
        DownloadModelButton.Content = installed ? "Installed" : "Download";
        DownloadModelButton.IsEnabled = !installed && _modelCancellation is null;
    }

    private string? SelectedModelId() => NullIfWhiteSpace(AsrModelBox.Text) ?? AsrModelBox.SelectedItem as string;

    private FileJobWorkspace CreateWorkspace() => new(NullIfWhiteSpace(WorkspacePathBox.Text));

    private PipelineOptions CreateOptions(
        CaptionerConfiguration configuration,
        string? targetLanguage,
        SubtitleLayout layout) => new(
            configuration.Asr,
            configuration.Llm,
            NullIfWhiteSpace(SourceLanguageBox.Text),
            targetLanguage,
            layout,
            SegmentBox.IsChecked == true,
            CorrectBox.IsChecked == true,
            MaxFileConcurrency: Math.Max(1, decimal.ToInt32(JobsBox.Value ?? 2)),
            Overwrite: OverwriteBox.IsChecked == true);

    private async Task<IReadOnlyList<MediaInput>> DiscoverAsync(
        IReadOnlyList<string> inputPaths,
        string outputDirectory,
        string? targetLanguage,
        SubtitleLayout layout,
        CancellationToken cancellationToken)
    {
        var result = new List<MediaInput>();
        foreach (var inputPath in inputPaths)
        {
            var discovered = await MediaDiscovery.DiscoverAsync(inputPath, outputDirectory, cancellationToken: cancellationToken);
            result.AddRange(discovered.Select(item => item with
            {
                OutputPath = SubtitleOutputPlanner.CreatePath(item.RelativePath, outputDirectory, targetLanguage, layout)
            }));
        }

        return result.GroupBy(item => item.Path, PathComparer()).Select(group => group.First()).ToArray();
    }

    private static void EnsureDistinctOutputs(IReadOnlyList<MediaInput> media)
    {
        var collision = media.GroupBy(item => item.OutputPath, PathComparer()).FirstOrDefault(group => group.Count() > 1);
        if (collision is not null)
        {
            throw new ArgumentException("More than one input maps to the same output path: " + collision.Key);
        }
    }

    private IReadOnlyList<string> ParseInputPaths()
    {
        var paths = (InputPathBox.Text ?? string.Empty)
            .Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(PathComparer())
            .ToArray();
        return paths.Length > 0 ? paths : throw new ArgumentException("Choose at least one media file or folder.");
    }

    private SubtitleLayout ResolveLayout(string? targetLanguage)
    {
        if (targetLanguage is null)
        {
            return SubtitleLayout.Source;
        }

        return LayoutBox.SelectedIndex switch
        {
            0 => SubtitleLayout.Source,
            1 => SubtitleLayout.Target,
            _ => SubtitleLayout.Bilingual
        };
    }

    private void ApplyOptions(PipelineOptions options)
    {
        SourceLanguageBox.Text = options.SourceLanguage;
        TargetLanguageBox.Text = options.TargetLanguage;
        LayoutBox.SelectedIndex = options.Layout switch
        {
            SubtitleLayout.Source => 0,
            SubtitleLayout.Target => 1,
            _ => 2
        };
        JobsBox.Value = options.MaxFileConcurrency;
        SegmentBox.IsChecked = options.EnableSegmentation;
        CorrectBox.IsChecked = options.EnableCorrection;
        OverwriteBox.IsChecked = options.Overwrite;
    }

    private void PopulateJobs(BatchManifest batch)
    {
        Jobs.Clear();
        foreach (var job in batch.Jobs)
        {
            Jobs.Add(new JobRow(job.JobId, job.InputPath));
        }

        BatchCountText.Text = $"{Jobs.Count} tasks";
    }

    private async Task RefreshJobsAsync(IJobWorkspace workspace, BatchManifest batch, CancellationToken cancellationToken)
    {
        foreach (var batchJob in batch.Jobs)
        {
            var manifest = await workspace.LoadJobAsync(batchJob.JobId, cancellationToken);
            var row = Jobs.FirstOrDefault(item => item.JobId == batchJob.JobId);
            if (manifest is not null && row is not null)
            {
                row.Apply(manifest);
            }
        }
    }

    private void SetBusy(bool busy)
    {
        RunButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void SetStatus(string message, int? count = null, bool failure = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(Color.Parse(failure ? "#B53A51" : "#1E3154"));
        if (count is not null)
        {
            BatchCountText.Text = $"{count} tasks";
        }
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

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record Runtime(
        CaptionerConfiguration Configuration,
        FileJobWorkspace Workspace,
        BatchRunner Runner,
        RoutingAsrClient Asr,
        HttpClient AsrHttp,
        HttpClient LlmHttp) : IDisposable
    {
        public void Dispose()
        {
            Asr.Dispose();
            AsrHttp.Dispose();
            LlmHttp.Dispose();
        }
    }
}
