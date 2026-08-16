using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Captioner.Desktop;

public partial class QueueView : UserControl, INotifyPropertyChanged
{
    private BatchListItem? _selectedBatch;
    private JobRow? _selectedJob;
    private string _activityText = "No active batch.";
    private string _summaryText = "No jobs in the queue.";
    private string _statusText = "Select a batch to inspect its jobs.";
    private bool _hasJobs;

    public QueueView()
    {
        InitializeComponent();
        DataContext = this;
        RecentBatches.CollectionChanged += (_, _) =>
        {
            if (SelectedBatch is null && RecentBatches.Count > 0)
            {
                SelectedBatch = RecentBatches[0];
            }

            OnPropertyChanged(nameof(HasRecentBatches));
            OnPropertyChanged(nameof(CanResume));
        };
        Jobs.CollectionChanged += (_, _) =>
        {
            if (SelectedJob is null && Jobs.Count > 0)
            {
                SelectedJob = Jobs[0];
            }
            else if (SelectedJob is not null && !Jobs.Contains(SelectedJob))
            {
                SelectedJob = Jobs.Count > 0 ? Jobs[0] : null;
            }

            HasJobs = Jobs.Count > 0;
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(CanRevealOutput));
            OnPropertyChanged(nameof(CanCopyDiagnostic));
            UpdateCounts();
        };
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SelectedBatchChanged;
    public event EventHandler? BatchSelectionChanged;

    public event EventHandler<BatchActionEventArgs>? ResumeRequested;
    public event EventHandler<BatchActionEventArgs>? StopRequested;
    public event EventHandler<BatchActionEventArgs>? RevealOutputRequested;
    public event EventHandler<BatchActionEventArgs>? CopyDiagnosticRequested;
    public event EventHandler<BatchActionEventArgs>? CleanRequested;

    public ObservableCollection<JobRow> Jobs { get; } = [];

    public ObservableCollection<BatchListItem> RecentBatches { get; } = [];

    public ObservableCollection<BatchListItem> Batches => RecentBatches;

    public BatchListItem? SelectedBatch
    {
        get => _selectedBatch;
        set
        {
            if (ReferenceEquals(_selectedBatch, value))
            {
                return;
            }

            _selectedBatch = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedBatchId));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(CanClean));
            SelectedBatchChanged?.Invoke(this, EventArgs.Empty);
            BatchSelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedBatchId => SelectedBatch?.BatchId;

    public JobRow? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (ReferenceEquals(_selectedJob, value))
            {
                return;
            }

            _selectedJob = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedJob));
            OnPropertyChanged(nameof(CanRevealOutput));
            OnPropertyChanged(nameof(CanCopyDiagnostic));
        }
    }

    public bool HasSelectedJob => SelectedJob is not null;

    public bool HasRecentBatches => RecentBatches.Count > 0;

    public bool HasJobs
    {
        get => _hasJobs;
        private set
        {
            if (_hasJobs == value)
            {
                return;
            }

            _hasJobs = value;
            OnPropertyChanged();
        }
    }

    public bool CanResume => SelectedBatch is not null;

    public bool CanStop => SelectedBatch is not null && HasJobs;

    public bool CanClean => SelectedBatch is not null;

    public bool CanRevealOutput => SelectedJob?.CanRevealOutput == true;

    public bool CanCopyDiagnostic => SelectedJob?.CanCopyDiagnostic == true;

    public string ActivityText
    {
        get => _activityText;
        private set => SetField(ref _activityText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public int TotalJobs { get; private set; }

    public int CompletedJobs { get; private set; }

    public int FailedJobs { get; private set; }

    public int RunningJobs { get; private set; }

    public void SetRecentBatches(IEnumerable<BatchListItem> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        SelectedBatch = null;
        RecentBatches.Clear();
        foreach (var batch in batches)
        {
            RecentBatches.Add(batch);
        }
    }

    public void SetSelectedBatch(BatchListItem? batch) => SelectedBatch = batch;

    public void SetSelectedJob(JobRow? job) => SelectedJob = job;

    public void SetActivity(string? activity) => ActivityText = string.IsNullOrWhiteSpace(activity)
        ? "No active batch."
        : activity.Trim();

    public void SetSummary(string? summary) => SummaryText = string.IsNullOrWhiteSpace(summary)
        ? "No jobs in the queue."
        : summary.Trim();

    public void SetSummary(int completed, int total, int failed = 0, int running = 0)
    {
        CompletedJobs = Math.Max(0, completed);
        TotalJobs = Math.Max(0, total);
        FailedJobs = Math.Max(0, failed);
        RunningJobs = Math.Max(0, running);
        OnPropertyChanged(nameof(CompletedJobs));
        OnPropertyChanged(nameof(TotalJobs));
        OnPropertyChanged(nameof(FailedJobs));
        OnPropertyChanged(nameof(RunningJobs));
        SetSummary($"{CompletedJobs} of {TotalJobs} complete");
    }

    public void SetStatus(string? status) => StatusText = string.IsNullOrWhiteSpace(status)
        ? "Select a batch to inspect its jobs."
        : status.Trim();

    public void SetState(string? activity, string? summary, string? status)
    {
        SetActivity(activity);
        SetSummary(summary);
        SetStatus(status);
    }

    private void UpdateCounts()
    {
        TotalJobs = Jobs.Count;
        CompletedJobs = Jobs.Count(job => string.Equals(job.Status, "Completed", StringComparison.OrdinalIgnoreCase));
        FailedJobs = Jobs.Count(job => string.Equals(job.Status, "Needs attention", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(job.Status, "Retryable", StringComparison.OrdinalIgnoreCase));
        RunningJobs = Jobs.Count(job => !string.Equals(job.Status, "Waiting", StringComparison.OrdinalIgnoreCase) &&
                                       !string.Equals(job.Status, "Completed", StringComparison.OrdinalIgnoreCase) &&
                                       !string.Equals(job.Status, "Needs attention", StringComparison.OrdinalIgnoreCase) &&
                                       !string.Equals(job.Status, "Retryable", StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(TotalJobs));
        OnPropertyChanged(nameof(CompletedJobs));
        OnPropertyChanged(nameof(FailedJobs));
        OnPropertyChanged(nameof(RunningJobs));
    }

    private void Resume_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedBatchId is { Length: > 0 } batchId)
        {
            ResumeRequested?.Invoke(this, BatchActionEventArgs.ForBatch(batchId));
        }
    }

    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedBatchId is { Length: > 0 } batchId)
        {
            StopRequested?.Invoke(this, BatchActionEventArgs.ForBatch(batchId));
        }
    }

    private void RevealOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedJob?.OutputPath is { Length: > 0 } outputPath)
        {
            RevealOutputRequested?.Invoke(this, BatchActionEventArgs.ForOutput(outputPath));
        }
    }

    private void CopyDiagnostic_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedJob?.Diagnostic is { Length: > 0 } diagnostic)
        {
            CopyDiagnosticRequested?.Invoke(this, BatchActionEventArgs.ForDiagnostic(diagnostic));
        }
    }

    private void Clean_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedBatchId is { Length: > 0 } batchId)
        {
            CleanRequested?.Invoke(this, BatchActionEventArgs.ForBatch(batchId));
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}

public sealed class BatchListItem : INotifyPropertyChanged
{
    private string _batchId;
    private string _label;
    private DateTimeOffset? _createdAt;
    private int _jobCount;
    private string _status;

    public BatchListItem(
        string batchId,
        string? label = null,
        DateTimeOffset? createdAt = null,
        int jobCount = 0,
        string status = "Ready")
    {
        _batchId = batchId;
        _label = string.IsNullOrWhiteSpace(label) ? batchId : label.Trim();
        _createdAt = createdAt;
        _jobCount = Math.Max(0, jobCount);
        _status = status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string BatchId
    {
        get => _batchId;
        set => SetField(ref _batchId, value);
    }

    public string Label
    {
        get => _label;
        set => SetField(ref _label, string.IsNullOrWhiteSpace(value) ? BatchId : value.Trim());
    }

    public DateTimeOffset? CreatedAt
    {
        get => _createdAt;
        set
        {
            if (!SetField(ref _createdAt, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CreatedLabel));
            OnPropertyChanged(nameof(Details));
        }
    }

    public int JobCount
    {
        get => _jobCount;
        set => SetField(ref _jobCount, Math.Max(0, value));
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, string.IsNullOrWhiteSpace(value) ? "Ready" : value.Trim());
    }

    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? BatchId : Label;

    public string CreatedLabel => CreatedAt is { } createdAt
        ? createdAt.ToLocalTime().ToString("g")
        : "No timestamp";

    public string Details => $"{(JobCount == 1 ? "1 job" : $"{JobCount} jobs")} · {CreatedLabel}";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new(propertyName));
        if (propertyName is nameof(Label) or nameof(BatchId))
        {
            PropertyChanged?.Invoke(this, new(nameof(DisplayLabel)));
        }
        if (propertyName is nameof(JobCount))
        {
            PropertyChanged?.Invoke(this, new(nameof(Details)));
        }
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}

public sealed class BatchActionEventArgs : EventArgs
{
    private BatchActionEventArgs(string? batchId, string? outputPath, string? diagnostic)
    {
        BatchId = batchId;
        OutputPath = outputPath;
        Diagnostic = diagnostic;
    }

    public string? BatchId { get; }

    public string? OutputPath { get; }

    public string? Diagnostic { get; }

    public static BatchActionEventArgs ForBatch(string batchId) => new(batchId, null, null);

    public static BatchActionEventArgs ForOutput(string outputPath) => new(null, outputPath, null);

    public static BatchActionEventArgs ForDiagnostic(string diagnostic) => new(null, null, diagnostic);
}
