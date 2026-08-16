using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Captioner.Core;

namespace Captioner.Desktop;

public sealed class JobRow : INotifyPropertyChanged
{
    private string _status = "Waiting";
    private IBrush _statusBackground = Brush("#E5ECF6");
    private IBrush _statusForeground = Brush("#536887");
    private string? _outputPath;
    private SourceKind _sourceKind = SourceKind.Media;
    private string _type = "Media";
    private double _progress;
    private string _currentStage = "Queued";
    private string? _diagnostic;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private TimeSpan? _elapsed;

    public JobRow(string jobId, string inputPath)
    {
        JobId = jobId;
        InputPath = inputPath;
        FileName = Path.GetFileName(inputPath);
        Stages = new(StageNames.Ordered.Select(name => new StageMarker(StageLabel(name))));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string JobId { get; }
    public string InputPath { get; }
    public string FileName { get; }
    public ObservableCollection<StageMarker> Stages { get; }

    /// <summary>Human-readable source kind used by the queue table.</summary>
    public string Type
    {
        get => _type;
        private set => SetField(ref _type, value);
    }

    public string TypeLabel => Type;

    public SourceKind SourceKind
    {
        get => _sourceKind;
        private set
        {
            if (!SetField(ref _sourceKind, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSubtitle));
        }
    }

    public bool IsSubtitle => SourceKind == SourceKind.Subtitle;

    /// <summary>Completion percentage in the range 0..100.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (!SetField(ref _progress, Math.Clamp(value, 0, 100)))
            {
                return;
            }

            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressLabel));
        }
    }

    public int ProgressPercent => (int)Math.Round(Progress, MidpointRounding.AwayFromZero);

    public string ProgressLabel => $"{ProgressPercent}%";

    public string CurrentStage
    {
        get => _currentStage;
        private set => SetField(ref _currentStage, value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public IBrush StatusBackground
    {
        get => _statusBackground;
        private set => SetField(ref _statusBackground, value);
    }

    public IBrush StatusForeground
    {
        get => _statusForeground;
        private set => SetField(ref _statusForeground, value);
    }

    public string? OutputPath
    {
        get => _outputPath;
        private set
        {
            if (!SetField(ref _outputPath, value))
            {
                return;
            }

            OnPropertyChanged(nameof(Output));
            OnPropertyChanged(nameof(HasOutput));
        }
    }

    public string? Output => OutputPath;

    public bool HasOutput => !string.IsNullOrWhiteSpace(OutputPath);

    public string? Diagnostic
    {
        get => _diagnostic;
        private set
        {
            if (!SetField(ref _diagnostic, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasDiagnostic));
        }
    }

    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);

    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        private set
        {
            if (!SetField(ref _startedAt, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ElapsedLabel));
        }
    }

    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        private set
        {
            if (!SetField(ref _completedAt, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ElapsedLabel));
        }
    }

    public TimeSpan? Elapsed
    {
        get => _elapsed;
        private set
        {
            if (!SetField(ref _elapsed, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ElapsedLabel));
        }
    }

    public string ElapsedLabel => Elapsed is null ? "—" : FormatElapsed(Elapsed.Value);

    public string Timing => ElapsedLabel;

    public bool CanRevealOutput => HasOutput;

    public bool CanCopyDiagnostic => HasDiagnostic;

    public void Apply(JobManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        SourceKind = manifest.SourceKind;
        Type = manifest.SourceKind == SourceKind.Subtitle ? "Subtitle" : "Media";
        OutputPath = manifest.OutputPath;
        var notNeeded = manifest.SourceKind == SourceKind.Subtitle
            ? new HashSet<string>([StageNames.Probe, StageNames.Chunks, StageNames.Transcribe], StringComparer.Ordinal)
            : [];

        var stages = manifest.Stages ?? new Dictionary<string, StageRecord>(StringComparer.Ordinal);
        for (var index = 0; index < StageNames.Ordered.Count; index++)
        {
            var name = StageNames.Ordered[index];
            if (notNeeded.Contains(name))
            {
                Stages[index].SetNotNeeded();
                continue;
            }

            var state = stages.TryGetValue(name, out var value) ? value : null;
            Stages[index].SetState(state?.Status ?? StageStatus.Pending, state?.Diagnostic);
        }

        var requiredNames = StageNames.Ordered.Where(name => !notNeeded.Contains(name)).ToArray();
        var requiredStages = requiredNames
            .Select(name => stages.TryGetValue(name, out var stage) ? stage : null)
            .ToArray();
        var activeName = requiredNames.FirstOrDefault(name =>
            !stages.TryGetValue(name, out var stage) || stage.Status != StageStatus.Ready);
        var active = activeName is not null && stages.TryGetValue(activeName, out var activeStage)
            ? activeStage
            : null;
        Progress = requiredStages.Length == 0
            ? 100
            : requiredStages.Count(stage => stage?.Status == StageStatus.Ready) * 100d / requiredStages.Length;

        StartedAt = requiredStages
            .Where(stage => stage?.StartedAt is not null)
            .Select(stage => stage!.StartedAt)
            .Min() ?? manifest.CreatedAt;
        CompletedAt = activeName is null ? manifest.UpdatedAt : null;
        Elapsed = StartedAt is null
            ? null
            : (CompletedAt ?? (active?.Status == StageStatus.Running ? DateTimeOffset.UtcNow : StartedAt)) - StartedAt;
        Diagnostic = requiredStages
            .Where(stage => stage is not null && !string.IsNullOrWhiteSpace(stage.Diagnostic))
            .Select(stage => stage!.Diagnostic)
            .FirstOrDefault(diagnostic => !string.IsNullOrWhiteSpace(diagnostic));

        if (activeName is null)
        {
            CurrentStage = "Complete";
            SetStatus("Completed", "#DDF3EE", "#17705D");
        }
        else if (active is { Status: StageStatus.Blocked or StageStatus.RetryableFailed })
        {
            CurrentStage = StageLabel(active.Name);
            SetStatus(active.Status == StageStatus.Blocked ? "Needs attention" : "Retryable", "#FBE4E8", "#A82D46");
        }
        else
        {
            CurrentStage = StageLabel(activeName);
            SetStatus(active is null ? "Waiting" : StageLabel(activeName), "#E0F4F7", "#166777");
        }
    }

    private void SetStatus(string status, string background, string foreground)
    {
        Status = status;
        StatusBackground = Brush(background);
        StatusForeground = Brush(foreground);
    }

    private static string StageLabel(string name) => name switch
    {
        StageNames.Probe => "Probe",
        StageNames.Chunks => "Audio",
        StageNames.Transcribe => "Transcribe",
        StageNames.Segment => "Segment",
        StageNames.Correct => "Polish",
        StageNames.Translate => "Translate",
        StageNames.Export => "Export",
        _ => name
    };

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m"
        : elapsed.TotalMinutes >= 1
            ? $"{elapsed.Minutes}m {elapsed.Seconds:00}s"
            : $"{Math.Max(0, elapsed.Seconds)}s";

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new(propertyName));
    }
}

public sealed class StageMarker(string label) : INotifyPropertyChanged
{
    private IBrush _fill = new SolidColorBrush(Color.Parse("#D7E0ED"));
    private IBrush _foreground = new SolidColorBrush(Color.Parse("#536887"));
    private StageStatus _status = StageStatus.Pending;
    private string _stateText = "Waiting";
    private bool _isNotNeeded;
    private string? _diagnostic;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; } = label;

    public StageStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
        }
    }

    public string StateText
    {
        get => _stateText;
        private set
        {
            if (_stateText == value)
            {
                return;
            }

            _stateText = value;
            PropertyChanged?.Invoke(this, new(nameof(StateText)));
            PropertyChanged?.Invoke(this, new(nameof(State)));
        }
    }

    public string State => StateText;

    public string StatusText => StateText;

    public string Name => Label;

    public bool IsNotNeeded
    {
        get => _isNotNeeded;
        private set
        {
            if (_isNotNeeded == value)
            {
                return;
            }

            _isNotNeeded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsNotNeeded)));
        }
    }

    public string? Diagnostic
    {
        get => _diagnostic;
        private set
        {
            if (_diagnostic == value)
            {
                return;
            }

            _diagnostic = value;
            PropertyChanged?.Invoke(this, new(nameof(Diagnostic)));
        }
    }

    public IBrush Fill
    {
        get => _fill;
        private set
        {
            if (ReferenceEquals(_fill, value))
            {
                return;
            }

            _fill = value;
            PropertyChanged?.Invoke(this, new(nameof(Fill)));
        }
    }

    public IBrush Foreground
    {
        get => _foreground;
        private set
        {
            if (ReferenceEquals(_foreground, value))
            {
                return;
            }

            _foreground = value;
            PropertyChanged?.Invoke(this, new(nameof(Foreground)));
        }
    }

    public void SetState(StageStatus status, string? diagnostic = null)
    {
        Status = status;
        IsNotNeeded = false;
        Diagnostic = diagnostic;
        StateText = status switch
        {
            StageStatus.Ready => "Done",
            StageStatus.Running => "Running",
            StageStatus.Blocked => "Blocked",
            StageStatus.RetryableFailed => "Retryable failure",
            StageStatus.Invalidated => "Needs refresh",
            _ => "Waiting"
        };
        Fill = new SolidColorBrush(Color.Parse(status switch
        {
            StageStatus.Ready => "#47B8A4",
            StageStatus.Running => "#72D0E0",
            StageStatus.Blocked or StageStatus.RetryableFailed => "#E4677D",
            StageStatus.Invalidated => "#E8B35D",
            _ => "#D7E0ED"
        }));
        Foreground = new SolidColorBrush(Color.Parse(status switch
        {
            StageStatus.Ready => "#17705D",
            StageStatus.Running => "#166777",
            StageStatus.Blocked or StageStatus.RetryableFailed => "#A82D46",
            StageStatus.Invalidated => "#855D10",
            _ => "#536887"
        }));
    }

    public void SetNotNeeded()
    {
        Status = StageStatus.Pending;
        IsNotNeeded = true;
        Diagnostic = null;
        StateText = "Not needed";
        Fill = new SolidColorBrush(Color.Parse("#E3E8F0"));
        Foreground = new SolidColorBrush(Color.Parse("#68758B"));
    }
}
