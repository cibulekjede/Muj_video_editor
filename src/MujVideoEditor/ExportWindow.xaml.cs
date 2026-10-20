using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using MujVideoEditor.Models;
using MujVideoEditor.Services;

namespace MujVideoEditor;

/// <summary>
/// Okno exportu: nastaveni, prubeh a vysledek.
/// </summary>
public partial class ExportWindow : Window
{
    private readonly TimelineState _timeline;
    private readonly AspectRatio _aspect;
    private readonly FfmpegExporter _exporter;
    private CancellationTokenSource? _cancellation;
    private string? _outputPath;

    public ExportWindow(TimelineState timeline, TimeSpan duration, string ffmpegPath, string defaultName, AspectRatio aspect)
    {
        InitializeComponent();
        _timeline = timeline;
        _aspect = aspect;
        _exporter = new FfmpegExporter(ffmpegPath);

        NameBox.Text = defaultName.Replace(' ', '_');
        FolderBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        SummaryText.Text = $"Délka videa: {MediaItem.FormatDuration(duration)} · poměr stran {aspect.Name} · formát MP4 (H.264 + AAC)";
    }

    private bool IsExporting => _cancellation is not null;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (IsExporting)
        {
            var answer = MessageBox.Show(this, "Export ještě běží. Opravdu ho chceš zrušit?", "Exportovat",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _cancellation?.Cancel();
        }
        base.OnClosing(e);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Kam uložit video", InitialDirectory = FolderBox.Text };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        string name = string.Concat(NameBox.Text.Trim().Split(Path.GetInvalidFileNameChars()));
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Zadej název videa.", "Exportovat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            name += ".mp4";

        string path = Path.Combine(FolderBox.Text, name);
        if (File.Exists(path))
        {
            var answer = MessageBox.Show(this, $"Soubor {name} už existuje. Přepsat ho?", "Exportovat",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        var (width, height) = _aspect.OutputSize(SelectedTag(ResolutionChoices));
        var settings = new ExportSettings(path, width, height, SelectedTag(FpsChoices), SelectedTag(QualityChoices));

        _outputPath = path;
        _cancellation = new CancellationTokenSource();
        ShowPanel(ProgressPanel);
        ExportProgress.Value = 0;
        PercentText.Text = "0 %";

        var progress = new Progress<double>(value =>
        {
            ExportProgress.Value = value;
            PercentText.Text = $"{value * 100:0} %";
        });

        try
        {
            await _exporter.ExportAsync(_timeline, settings, progress, _cancellation.Token);
            OutputPathText.Text = path;
            ShowPanel(DonePanel);
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded)
                ShowPanel(SettingsPanel);
        }
        catch (ExportFailedException ex)
        {
            ShowError(ex.Message, ex.Log);
        }
        catch (Exception ex)
        {
            ShowError("Nepodařilo se spustit FFmpeg.", ex.Message);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void BackToSettings_Click(object sender, RoutedEventArgs e) => ShowPanel(SettingsPanel);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_outputPath is not null)
            Process.Start("explorer.exe", $"/select,\"{_outputPath}\"");
    }

    private void PlayOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_outputPath is not null)
            Process.Start(new ProcessStartInfo(_outputPath) { UseShellExecute = true });
    }

    private void ShowError(string message, string log)
    {
        ErrorText.Text = message;
        ErrorLog.Text = log;
        ShowPanel(ErrorPanel);
    }

    private void ShowPanel(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { SettingsPanel, ProgressPanel, DonePanel, ErrorPanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
    }

    private static int SelectedTag(Panel choices) =>
        int.Parse((string)choices.Children.OfType<RadioButton>().First(r => r.IsChecked == true).Tag);
}
