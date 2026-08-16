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
        private set => SetField(ref _outputPath, value);
    }

    public void Apply(JobManifest manifest)
    {
        OutputPath = manifest.OutputPath;
        for (var index = 0; index < StageNames.Ordered.Count; index++)
        {
            var name = StageNames.Ordered[index];
            var state = manifest.Stages.TryGetValue(name, out var value) ? value.Status : StageStatus.Pending;
            Stages[index].SetState(state);
        }

        var active = StageNames.Ordered.Select(name => manifest.Stages[name]).FirstOrDefault(stage => stage.Status != StageStatus.Ready);
        if (active is null)
        {
            SetStatus("Completed", "#DDF3EE", "#17705D");
        }
        else if (active.Status is StageStatus.Blocked or StageStatus.RetryableFailed)
        {
            SetStatus(active.Status == StageStatus.Blocked ? "Needs attention" : "Retryable", "#FBE4E8", "#A82D46");
        }
        else
        {
            SetStatus(StageLabel(active.Name), "#E0F4F7", "#166777");
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

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new(propertyName));
    }
}

public sealed class StageMarker(string label) : INotifyPropertyChanged
{
    private IBrush _fill = new SolidColorBrush(Color.Parse("#D7E0ED"));

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; } = label;

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

    public void SetState(StageStatus status)
    {
        Fill = new SolidColorBrush(Color.Parse(status switch
        {
            StageStatus.Ready => "#47B8A4",
            StageStatus.Running => "#72D0E0",
            StageStatus.Blocked or StageStatus.RetryableFailed => "#E4677D",
            StageStatus.Invalidated => "#E8B35D",
            _ => "#D7E0ED"
        }));
    }
}
