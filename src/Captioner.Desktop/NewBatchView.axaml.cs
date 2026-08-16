using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Captioner.Core;
using Captioner.Infrastructure;

namespace Captioner.Desktop;

public sealed partial class NewBatchView : UserControl
{
    private bool _isApplyingOptions;
    private bool _segmentationTouched;
    private bool _targetWasEmpty = true;
    private string? _defaultReference;

    public NewBatchView()
    {
        InitializeComponent();
        DataContext = this;
        OutputPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Captioner");
        TargetLanguageBox.TextChanged += TargetLanguage_Changed;
        SegmentBox.IsCheckedChanged += SegmentBox_Changed;
    }

    public event EventHandler? StartRequested;
    public event EventHandler? SettingsRequested;

    public ObservableCollection<InputPathItem> Inputs { get; } = [];

    public void LoadConfiguration(CaptionerConfiguration configuration)
    {
        _defaultReference = Normalize(configuration.ReferenceText);
        DefaultReferenceText.Text = _defaultReference is null
            ? "No default glossary configured."
            : $"{_defaultReference.Length:N0} characters will be added to this batch.";
        LlmJobsBox.Value = Math.Max(1, configuration.Llm.MaxConcurrency);
        ReadinessText.Text = configuration.Asr.Backend == EndpointBackend.SherpaOnnx
            ? $"Local ASR · {configuration.Asr.Model}"
            : $"Remote ASR · {configuration.Asr.Model}";
    }

    internal NewBatchRequest CreateRequest()
    {
        if (Inputs.Count == 0)
        {
            throw new ArgumentException("Add at least one media file, SRT file, or folder.");
        }

        var output = Normalize(OutputPathBox.Text) ?? throw new ArgumentException("Choose an SRT output folder.");
        var target = Normalize(TargetLanguageBox.Text);
        return new(
            Inputs.Select(item => item.FullPath).ToArray(),
            Path.GetFullPath(output),
            Normalize(SourceLanguageBox.Text),
            target,
            ResolveLayout(target),
            SegmentBox.IsChecked == true,
            CorrectBox.IsChecked == true,
            DecimalValue(JobsBox, 2),
            DecimalValue(LlmJobsBox, 4),
            OverwriteBox.IsChecked == true,
            Normalize(BatchReferenceBox.Text),
            DecimalValue(MaxCjkBox, 18),
            DecimalValue(MaxWordsBox, 12),
            decimal.ToInt64(MaxDurationBox.Value ?? 7000));
    }

    public void ApplyOptions(PipelineOptions options)
    {
        _isApplyingOptions = true;
        try
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
            LlmJobsBox.Value = Math.Max(1, options.Llm.MaxConcurrency);
            SegmentBox.IsChecked = options.EnableSegmentation;
            CorrectBox.IsChecked = options.EnableCorrection;
            OverwriteBox.IsChecked = options.Overwrite;
            MaxCjkBox.Value = options.EffectiveMaxCueCharactersCjk;
            MaxWordsBox.Value = Math.Max(1, options.EffectiveMaxCueWordsLatin);
            MaxDurationBox.Value = options.MaxCueDurationMs;
            BatchReferenceBox.Text = options.ReferenceText;
            _targetWasEmpty = string.IsNullOrWhiteSpace(options.TargetLanguage);
            _segmentationTouched = true;
        }
        finally
        {
            _isApplyingOptions = false;
        }
    }

    public void SetBusy(bool busy)
    {
        StartBatchButton.IsEnabled = !busy && Inputs.Count > 0;
        AddFilesButton.IsEnabled = !busy;
        AddFolderButton.IsEnabled = !busy;
    }

    public void ShowValidation(string message, bool failure = true)
    {
        ValidationBanner.IsVisible = true;
        ValidationText.Text = message;
        ValidationText.Foreground = new SolidColorBrush(Color.Parse(failure ? "#C94B5F" : "#1E9C89"));
    }

    public void ClearValidation() => ValidationBanner.IsVisible = false;

    internal void AddInputPaths(IEnumerable<string> paths)
    {
        ClearValidation();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var existing = Inputs.Select(item => item.FullPath).ToHashSet(comparer);
        foreach (var path in paths)
        {
            try
            {
                var item = InputPathItem.Create(path);
                if (existing.Add(item.FullPath))
                {
                    Inputs.Add(item);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                ShowValidation(exception.Message);
            }
        }

        RefreshInputState();
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("Window is unavailable.");
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose media or SRT files",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Media and SRT files")
                    {
                        Patterns = MediaDiscovery.SupportedExtensions.Select(extension => "*" + extension).ToArray()
                    }
                ]
            });
            AddInputPaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception exception)
        {
            ShowValidation("Could not choose files: " + exception.Message);
        }
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("Window is unavailable.");
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose a media or subtitle folder",
                AllowMultiple = true
            });
            AddInputPaths(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception exception)
        {
            ShowValidation("Could not choose a folder: " + exception.Message);
        }
    }

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this) ?? throw new InvalidOperationException("Window is unavailable.");
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the SRT output folder",
                AllowMultiple = false
            });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            {
                OutputPathBox.Text = path;
            }
        }
        catch (Exception exception)
        {
            ShowValidation("Could not choose the output folder: " + exception.Message);
        }
    }

    private void RemoveInput_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: InputPathItem item })
        {
            Inputs.Remove(item);
            RefreshInputState();
        }
    }

    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void DropZone_Drop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is not null)
        {
            AddInputPaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        }
    }

    private void TargetLanguage_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_isApplyingOptions)
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

    private void SegmentBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_isApplyingOptions)
        {
            _segmentationTouched = true;
        }
    }

    private void EditReference_Click(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void StartBatch_Click(object? sender, RoutedEventArgs e)
    {
        ClearValidation();
        StartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshInputState()
    {
        var hasInputs = Inputs.Count > 0;
        EmptyInputPanel.IsVisible = !hasInputs;
        InputList.IsVisible = hasInputs;
        StartBatchButton.IsEnabled = hasInputs;
        var media = Inputs.Count(item => item.Kind == "Media");
        var subtitles = Inputs.Count(item => item.Kind == "Subtitle");
        var folders = Inputs.Count(item => item.Kind == "Folder");
        InputStatusText.Text = hasInputs
            ? $"{Inputs.Count} inputs · {media} media · {subtitles} SRT · {folders} folders"
            : "No inputs selected.";
        DraftSummaryText.Text = hasInputs
            ? $"{Inputs.Count} input paths ready for validation."
            : "Add files to create a batch.";

        if (!_segmentationTouched)
        {
            _isApplyingOptions = true;
            SegmentBox.IsChecked = Inputs.Count == 0 || Inputs.Any(item => item.Kind is "Media" or "Folder");
            _isApplyingOptions = false;
        }
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

    private static int DecimalValue(NumericUpDown control, int fallback) =>
        Math.Max(1, decimal.ToInt32(control.Value ?? fallback));

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
