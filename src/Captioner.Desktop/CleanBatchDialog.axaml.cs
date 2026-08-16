using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Captioner.Desktop;

public sealed partial class CleanBatchDialog : Window
{
    public CleanBatchDialog(string batchId)
    {
        InitializeComponent();
        BatchIdText.Text = batchId;
    }

    public CleanBatchDialog() : this("Batch")
    {
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Clean_Click(object? sender, RoutedEventArgs e) => Close(true);
}
