using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Captioner.Core;
using Captioner.Engine;
using Captioner.Infrastructure;

namespace Captioner.Desktop;

/// <summary>Coordinates the desktop workbench without owning pipeline rules.</summary>
public sealed partial class MainWindow : Window
{
    private readonly JsonConfigurationLoader _configurationLoader = new();
    private CancellationTokenSource? _runCancellation;
    private string? _activeBatchId;
    private string? _loadedQueueBatchId;
    private bool _suppressQueueSelection;

    public MainWindow()
    {
        InitializeComponent();
        NewBatchPage.StartRequested += NewBatchPage_StartRequested;
        NewBatchPage.SettingsRequested += SettingsRequested;
        QueuePage.PropertyChanged += QueuePage_PropertyChanged;
        QueuePage.ResumeRequested += QueuePage_ResumeRequested;
        QueuePage.StopRequested += QueuePage_StopRequested;
        QueuePage.RevealOutputRequested += QueuePage_RevealOutputRequested;
        QueuePage.CopyDiagnosticRequested += QueuePage_CopyDiagnosticRequested;
        QueuePage.CleanRequested += QueuePage_CleanRequested;
        Opened += OnOpened;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _runCancellation?.Cancel();
        base.OnClosing(e);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        DesktopStartupDiagnostics.Record("window-opened");
        try
        {
            LoadConfiguration();
            await RefreshRecentBatchesAsync();
        }
        catch (Exception exception)
        {
            NewBatchPage.ShowValidation("Configuration or workspace is unavailable: " + exception.Message);
        }
    }

    private void NewBatchNav_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowNewBatch();

    private void QueueNav_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowQueue();

    private async void Settings_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await ShowSettingsAsync();

    private void Stop_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => StopActiveBatch();

    private async void SettingsRequested(object? sender, EventArgs e) => await ShowSettingsAsync();

    private async Task ShowSettingsAsync()
    {
        try
        {
            var dialog = new SettingsDialog(_configurationLoader);
            if (await dialog.ShowDialog<bool>(this))
            {
                LoadConfiguration();
                await RefreshRecentBatchesAsync();
                NewBatchPage.ShowValidation("Settings saved.", failure: false);
            }
        }
        catch (Exception exception)
        {
            NewBatchPage.ShowValidation("Could not open settings: " + exception.Message);
            ShowNewBatch();
        }
    }

    private void LoadConfiguration()
    {
        var configuration = _configurationLoader.Load();
        NewBatchPage.LoadConfiguration(configuration);
    }

    private async void NewBatchPage_StartRequested(object? sender, EventArgs e)
    {
        try
        {
            await ExecuteNewBatchAsync();
        }
        catch (Exception exception)
        {
            NewBatchPage.ShowValidation(exception.Message);
        }
    }

    private async Task ExecuteNewBatchAsync()
    {
        if (_runCancellation is not null)
        {
            return;
        }

        var request = NewBatchPage.CreateRequest();
        var configuration = _configurationLoader.LoadWithEnvironment();
        var llm = configuration.Llm with { MaxConcurrency = request.LlmConcurrency };
        configuration = configuration with { Llm = llm };
        var options = new PipelineOptions(
            configuration.Asr,
            configuration.Llm,
            request.SourceLanguage,
            request.TargetLanguage,
            request.Layout,
            request.EnableSegmentation,
            request.EnableCorrection,
            MaxCueDurationMs: request.MaxCueDurationMs,
            MaxFileConcurrency: request.MaxFileConcurrency,
            Overwrite: request.Overwrite,
            ReferenceText: request.ComposeReference(configuration.ReferenceText),
            MaxCueCharactersCjk: request.MaxCueCharactersCjk,
            MaxCueWordsLatin: request.MaxCueWordsLatin);

        _runCancellation = new CancellationTokenSource();
        SetRunning(true, "Preparing batch");
        try
        {
            var cancellationToken = _runCancellation.Token;
            using var runtime = CreateRuntime(configuration);
            NewBatchPage.ShowValidation("Validating files and inference settings…", failure: false);
            var inputs = await DiscoverAsync(request, cancellationToken);
            if (inputs.Count == 0)
            {
                throw new ArgumentException("No supported media or SRT files were found.");
            }

            EnsureDistinctOutputs(inputs);
            if (!options.Overwrite && inputs.FirstOrDefault(input => File.Exists(input.OutputPath)) is { } collision)
            {
                throw new IOException("Output already exists. Enable Replace existing SRT outputs to continue: " + collision.OutputPath);
            }

            await CheckRequiredEndpointsAsync(
                runtime,
                inputs.Any(input => input.Kind == SourceKind.Media),
                options,
                cancellationToken);
            var batch = await runtime.Runner.PrepareAsync(inputs, cancellationToken, options);
            _activeBatchId = batch.BatchId;
            SetRunning(true, "Batch running");
            await RefreshRecentBatchesAsync(batch.BatchId);
            await PopulateQueueAsync(runtime.Workspace, batch, cancellationToken);
            ShowQueue();
            await RunPreparedBatchAsync(runtime, batch, options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            QueuePage.SetStatus("Stopped. Completed stages are saved and can be resumed.");
            NewBatchPage.ShowValidation("Stopped. Open Queue and choose Resume batch to continue.", failure: false);
        }
        finally
        {
            _activeBatchId = null;
            _runCancellation.Dispose();
            _runCancellation = null;
            SetRunning(false);
            await RefreshRecentBatchesAsync(_loadedQueueBatchId);
        }
    }

    private async Task RunPreparedBatchAsync(
        Runtime runtime,
        BatchManifest batch,
        PipelineOptions options,
        CancellationToken cancellationToken)
    {
        var runTask = runtime.Runner.RunAsync(batch, options, cancellationToken);
        while (!runTask.IsCompleted)
        {
            await RefreshQueueJobsAsync(runtime.Workspace, batch, CancellationToken.None);
            await Task.WhenAny(runTask, Task.Delay(350, CancellationToken.None));
        }

        var result = await runTask;
        await RefreshQueueJobsAsync(runtime.Workspace, batch, CancellationToken.None);
        var failed = result.Jobs.Count(job => !job.Succeeded);
        QueuePage.SetStatus(failed == 0
            ? $"Batch complete. {result.Jobs.Count} SRT files are ready."
            : $"{failed} jobs need attention. Fix the issue, then resume this batch.");
    }

    private async Task CheckRequiredEndpointsAsync(
        Runtime runtime,
        bool needsAsr,
        PipelineOptions options,
        CancellationToken cancellationToken)
    {
        if (needsAsr)
        {
            await runtime.Asr.CheckAsync(options.Asr, cancellationToken);
        }

        if (options.EnableSegmentation || options.EnableCorrection || options.TargetLanguage is not null)
        {
            await runtime.Llm.CheckAsync(options.Llm, cancellationToken);
        }
    }

    private async void QueuePage_ResumeRequested(object? sender, BatchActionEventArgs e)
    {
        if (e.BatchId is null || _runCancellation is not null)
        {
            return;
        }

        try
        {
            await ResumeBatchAsync(e.BatchId);
        }
        catch (Exception exception)
        {
            QueuePage.SetStatus("Could not resume the batch: " + exception.Message);
        }
    }

    private async Task ResumeBatchAsync(string batchId)
    {
        var configuration = _configurationLoader.LoadWithEnvironment();
        var workspace = CreateWorkspace(configuration);
        var batch = await workspace.LoadBatchAsync(batchId, CancellationToken.None) ??
            throw new ArgumentException("Batch not found: " + batchId);
        var saved = batch.Options ?? new PipelineOptions(configuration.Asr, configuration.Llm);
        var options = saved with
        {
            Asr = configuration.Asr,
            Llm = configuration.Llm with { MaxConcurrency = Math.Max(1, configuration.Llm.MaxConcurrency) }
        };

        _runCancellation = new CancellationTokenSource();
        _activeBatchId = batch.BatchId;
        SetRunning(true, "Resuming batch");
        try
        {
            using var runtime = CreateRuntime(configuration);
            await PopulateQueueAsync(runtime.Workspace, batch, _runCancellation.Token);
            await CheckRequiredEndpointsAsync(
                runtime,
                QueuePage.Jobs.Any(row => row.SourceKind == SourceKind.Media),
                options,
                _runCancellation.Token);
            ShowQueue();
            await RunPreparedBatchAsync(runtime, batch, options, _runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            QueuePage.SetStatus("Stopped. Completed stages are saved and can be resumed.");
        }
        catch (Exception exception)
        {
            QueuePage.SetStatus("Resume failed: " + exception.Message);
        }
        finally
        {
            _activeBatchId = null;
            _runCancellation.Dispose();
            _runCancellation = null;
            SetRunning(false);
            await RefreshRecentBatchesAsync(batch.BatchId);
        }
    }

    private void QueuePage_StopRequested(object? sender, BatchActionEventArgs e)
    {
        if (e.BatchId == _activeBatchId)
        {
            StopActiveBatch();
        }
        else
        {
            QueuePage.SetStatus("This batch is not currently running.");
        }
    }

    private void StopActiveBatch()
    {
        _runCancellation?.Cancel();
        if (_runCancellation is not null)
        {
            SetRunning(true, "Stopping safely");
            QueuePage.SetStatus("Stopping after the current operation. Completed stages remain resumable.");
        }
    }

    private async void QueuePage_RevealOutputRequested(object? sender, BatchActionEventArgs e)
    {
        try
        {
            if (e.OutputPath is not { } outputPath)
            {
                return;
            }

            var directory = Path.GetDirectoryName(outputPath);
            if (directory is null || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("The output directory does not exist yet.");
            }

            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directory));
        }
        catch (Exception exception)
        {
            QueuePage.SetStatus("Could not reveal output: " + exception.Message);
        }
    }

    private async void QueuePage_CopyDiagnosticRequested(object? sender, BatchActionEventArgs e)
    {
        try
        {
            if (e.Diagnostic is { } diagnostic && Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(diagnostic);
                QueuePage.SetStatus("Diagnostic copied to the clipboard.");
            }
        }
        catch (Exception exception)
        {
            QueuePage.SetStatus("Could not copy the diagnostic: " + exception.Message);
        }
    }

    private async void QueuePage_CleanRequested(object? sender, BatchActionEventArgs e)
    {
        if (e.BatchId is not { } batchId)
        {
            return;
        }

        if (batchId == _activeBatchId)
        {
            QueuePage.SetStatus("Stop the active batch before cleaning its recovery data.");
            return;
        }

        var dialog = new CleanBatchDialog(batchId);
        if (!await dialog.ShowDialog<bool>(this))
        {
            return;
        }

        try
        {
            var workspace = CreateWorkspace(_configurationLoader.Load());
            await workspace.CleanBatchAsync(batchId, CancellationToken.None);
            _loadedQueueBatchId = null;
            await RefreshRecentBatchesAsync();
            QueuePage.SetStatus("Recovery data cleaned. Published SRT files were kept.");
        }
        catch (Exception exception)
        {
            QueuePage.SetStatus("Could not clean the batch: " + exception.Message);
        }
    }

    private async void QueuePage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressQueueSelection || e.PropertyName != nameof(QueueView.SelectedBatch))
        {
            return;
        }

        if (QueuePage.SelectedBatchId is { } batchId && batchId != _loadedQueueBatchId)
        {
            await LoadQueueBatchAsync(batchId);
        }
    }

    private async Task RefreshRecentBatchesAsync(string? selectedBatchId = null)
    {
        var configuration = _configurationLoader.Load();
        var workspace = CreateWorkspace(configuration);
        var ids = await workspace.ListBatchIdsAsync(CancellationToken.None);
        var manifests = await Task.WhenAll(ids.Select(id => workspace.LoadBatchAsync(id, CancellationToken.None)));
        var recent = manifests
            .OfType<BatchManifest>()
            .OrderByDescending(batch => batch.CreatedAt)
            .Take(20)
            .Select(batch => new BatchListItem(
                batch.BatchId,
                CreateBatchLabel(batch),
                batch.CreatedAt,
                batch.Jobs.Count,
                batch.BatchId == _activeBatchId ? "Running" : "Ready"))
            .ToArray();

        var desiredId = selectedBatchId ?? QueuePage.SelectedBatchId ?? recent.FirstOrDefault()?.BatchId;
        _suppressQueueSelection = true;
        try
        {
            QueuePage.SetRecentBatches(recent);
            QueuePage.SetSelectedBatch(recent.FirstOrDefault(item => item.BatchId == desiredId) ?? recent.FirstOrDefault());
        }
        finally
        {
            _suppressQueueSelection = false;
        }

        if (QueuePage.SelectedBatchId is { } batchId)
        {
            await LoadQueueBatchAsync(batchId);
        }
        else
        {
            _loadedQueueBatchId = null;
            QueuePage.Jobs.Clear();
            QueuePage.SetState("No active batch.", "No jobs in the queue.", "Start a new batch to populate the queue.");
        }
    }

    private async Task LoadQueueBatchAsync(string batchId)
    {
        try
        {
            var workspace = CreateWorkspace(_configurationLoader.Load());
            var batch = await workspace.LoadBatchAsync(batchId, CancellationToken.None);
            if (batch is null)
            {
                QueuePage.SetStatus("Batch not found: " + batchId);
                return;
            }

            _loadedQueueBatchId = batchId;
            await PopulateQueueAsync(workspace, batch, CancellationToken.None);
        }
        catch (Exception exception)
        {
            QueuePage.SetStatus("Could not load the batch: " + exception.Message);
        }
    }

    private async Task PopulateQueueAsync(
        IJobWorkspace workspace,
        BatchManifest batch,
        CancellationToken cancellationToken)
    {
        QueuePage.Jobs.Clear();
        foreach (var batchJob in batch.Jobs)
        {
            var row = new JobRow(batchJob.JobId, batchJob.InputPath);
            var manifest = await workspace.LoadJobAsync(batchJob.JobId, cancellationToken);
            if (manifest is not null)
            {
                row.Apply(manifest);
            }

            QueuePage.Jobs.Add(row);
        }

        _loadedQueueBatchId = batch.BatchId;
        UpdateQueueSummary(batch);
    }

    private async Task RefreshQueueJobsAsync(
        IJobWorkspace workspace,
        BatchManifest batch,
        CancellationToken cancellationToken)
    {
        foreach (var batchJob in batch.Jobs)
        {
            var manifest = await workspace.LoadJobAsync(batchJob.JobId, cancellationToken);
            var row = QueuePage.Jobs.FirstOrDefault(item => item.JobId == batchJob.JobId);
            if (manifest is not null && row is not null)
            {
                row.Apply(manifest);
            }
        }

        UpdateQueueSummary(batch);
    }

    private void UpdateQueueSummary(BatchManifest batch)
    {
        var complete = QueuePage.Jobs.Count(row => row.Status == "Completed");
        var failed = QueuePage.Jobs.Count(row => row.Status is "Needs attention" or "Retryable");
        var running = QueuePage.Jobs.Count(row => row.Status is not ("Waiting" or "Completed" or "Needs attention" or "Retryable"));
        QueuePage.SetSummary(complete, QueuePage.Jobs.Count, failed, running);
        QueuePage.SetActivity($"{batch.BatchId} · created {batch.CreatedAt.ToLocalTime():g}");
        QueuePage.SetStatus(batch.BatchId == _activeBatchId
            ? "Batch is running. Select a job to inspect its caption track."
            : failed > 0
                ? $"{failed} jobs need attention. Resume after correcting the underlying issue."
                : complete == QueuePage.Jobs.Count && complete > 0
                    ? "Batch complete. Published SRT files are ready."
                    : "Batch is ready to resume.");
    }

    private Runtime CreateRuntime(CaptionerConfiguration configuration)
    {
        var workspace = CreateWorkspace(configuration);
        var asrHttp = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        var llmHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        var asr = new RoutingAsrClient(configuration.ModelDirectory, asrHttp);
        var llm = new OpenAiLlmClient(llmHttp);
        var subtitles = new SrtSubtitlePublisher();
        var pipeline = new PipelineRunner(
            new FfmpegMediaTool(),
            asr,
            llm,
            workspace,
            subtitles,
            subtitles);
        return new(workspace, new BatchRunner(pipeline, workspace), asr, llm, asrHttp, llmHttp);
    }

    private static FileJobWorkspace CreateWorkspace(CaptionerConfiguration configuration) =>
        new(configuration.WorkspaceDirectory);

    private static async Task<IReadOnlyList<MediaInput>> DiscoverAsync(
        NewBatchRequest request,
        CancellationToken cancellationToken)
    {
        var result = new List<MediaInput>();
        foreach (var inputPath in request.Inputs)
        {
            var discovered = await MediaDiscovery.DiscoverAsync(
                inputPath,
                request.OutputDirectory,
                cancellationToken: cancellationToken);
            result.AddRange(discovered.Select(item => item with
            {
                OutputPath = SubtitleOutputPlanner.CreatePath(
                    item.RelativePath,
                    request.OutputDirectory,
                    request.TargetLanguage,
                    request.Layout)
            }));
        }

        return result.GroupBy(item => item.Path, PathComparer()).Select(group => group.First()).ToArray();
    }

    private static void EnsureDistinctOutputs(IReadOnlyList<MediaInput> inputs)
    {
        var collision = inputs
            .GroupBy(input => input.OutputPath, PathComparer())
            .FirstOrDefault(group => group.Count() > 1);
        if (collision is not null)
        {
            throw new ArgumentException("More than one input maps to the same output path: " + collision.Key);
        }
    }

    private void SetRunning(bool running, string? text = null)
    {
        NewBatchPage.SetBusy(running);
        RunStateBadge.IsVisible = running;
        StopButton.IsVisible = running;
        SettingsButton.IsEnabled = !running;
        if (text is not null)
        {
            RunStateText.Text = text;
        }
    }

    private void ShowNewBatch()
    {
        NewBatchPage.IsVisible = true;
        QueuePage.IsVisible = false;
        SetActiveClass(NewBatchNavButton, active: true);
        SetActiveClass(QueueNavButton, active: false);
    }

    private void ShowQueue()
    {
        NewBatchPage.IsVisible = false;
        QueuePage.IsVisible = true;
        SetActiveClass(NewBatchNavButton, active: false);
        SetActiveClass(QueueNavButton, active: true);
    }

    private static void SetActiveClass(Control control, bool active)
    {
        if (active && !control.Classes.Contains("active"))
        {
            control.Classes.Add("active");
        }
        else if (!active)
        {
            control.Classes.Remove("active");
        }
    }

    private static string CreateBatchLabel(BatchManifest batch)
    {
        if (batch.Jobs.Count == 0)
        {
            return "Empty batch";
        }

        var name = Path.GetFileName(batch.Jobs[0].InputPath);
        return batch.Jobs.Count == 1 ? name : $"{name} + {batch.Jobs.Count - 1}";
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record Runtime(
        FileJobWorkspace Workspace,
        BatchRunner Runner,
        RoutingAsrClient Asr,
        OpenAiLlmClient Llm,
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
