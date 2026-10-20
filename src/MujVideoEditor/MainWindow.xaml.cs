using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MujVideoEditor.Models;
using MujVideoEditor.Services;

namespace MujVideoEditor;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const int FramesPerSecond = 30;
    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    private readonly ObservableCollection<MediaItem> _mediaItems = [];
    private readonly DispatcherTimer _playbackTimer;

    // Prehravace pouzite jen pro zjisteni delky; drzime je, aby je mezitim neuklidil garbage collector
    private readonly HashSet<MediaPlayer> _durationProbes = [];

    private MediaItem? _currentMedia;
    private bool _isPlaying;
    private bool _isSeeking;

    public MainWindow()
    {
        InitializeComponent();

        // Popisky casoveho pravitka po 5 sekundach (zatim pevne na 2 minuty)
        TimeRuler.ItemsSource = Enumerable.Range(0, 25)
            .Select(i => TimeSpan.FromSeconds(i * 5).ToString(@"mm\:ss"));

        MediaList.ItemsSource = _mediaItems;
        _mediaItems.CollectionChanged += (_, _) => UpdateMediaLibraryView();

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playbackTimer.Tick += (_, _) => UpdatePlaybackPosition();
        _playbackTimer.Start();

        ShowProjectDetails();
        StateChanged += (_, _) => UpdateMaximizeState();

        // Soubory predane pri spusteni (napr. pretazene na ikonu aplikace) rovnou naimportuj
        var startupFiles = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (startupFiles.Length > 0)
            Loaded += (_, _) => AddFiles(startupFiles);
    }

    // ===== Titulkova lista =====

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeState()
    {
        bool maximized = WindowState == WindowState.Maximized;

        // Maximalizovane okno s vlastni listou presahuje okraje obrazovky, proto ho odsadime
        RootGrid.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Obnovit" : "Maximalizovat";
    }

    // ===== Import medii =====

    private void Import_Click(object sender, RoutedEventArgs e) => ImportFromDialog();

    private void EmptyImportCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ImportFromDialog();

    private void ImportFromDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Importovat média",
            Filter = MediaItem.DialogFilter,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true)
            AddFiles(dialog.FileNames);
    }

    private void MediaArea_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void MediaArea_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        // Pretazena slozka = vsechny soubory primo v ni
        var files = paths.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p) : [p]);
        AddFiles(files);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        var added = new List<MediaItem>();
        int skipped = 0;

        foreach (string path in paths)
        {
            if (MediaItem.DetectKind(path) is not { } kind)
            {
                skipped++;
                continue;
            }

            if (_mediaItems.Any(m => string.Equals(m.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            var item = new MediaItem(path, kind);
            _mediaItems.Add(item);
            added.Add(item);
        }

        if (skipped > 0)
        {
            MessageBox.Show(this, $"Některé soubory ({skipped}) nejsou podporované a nebyly přidány.",
                "Importovat média", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        if (added.Count > 0)
        {
            MediaList.SelectedItem = added[0];
            _ = LoadMediaInfoAsync(added);
        }
    }

    private async Task LoadMediaInfoAsync(IEnumerable<MediaItem> items)
    {
        foreach (var item in items)
        {
            if (item.Kind != MediaKind.Image)
                ProbeDuration(item);

            // Nahled se generuje mimo hlavni vlakno, aby okno nezamrzlo
            item.Thumbnail = await Task.Run(() => ShellThumbnail.TryGet(item.FilePath, 256));
        }
    }

    private void ProbeDuration(MediaItem item)
    {
        var probe = new MediaPlayer();
        _durationProbes.Add(probe);

        void Finish()
        {
            probe.Close();
            _durationProbes.Remove(probe);
        }

        probe.MediaOpened += (_, _) =>
        {
            if (probe.NaturalDuration.HasTimeSpan)
                item.Duration = probe.NaturalDuration.TimeSpan;
            Finish();
        };
        probe.MediaFailed += (_, _) => Finish();
        probe.Open(new Uri(item.FilePath));
    }

    private void RemoveMedia_Click(object sender, RoutedEventArgs e)
    {
        // Prave tlacitko mysi polozku v seznamu zaroven vybere
        if (MediaList.SelectedItem is not MediaItem item)
            return;

        if (item == _currentMedia)
            ClearPreview();

        _mediaItems.Remove(item);
    }

    private void UpdateMediaLibraryView()
    {
        bool hasMedia = _mediaItems.Count > 0;
        EmptyImportCard.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
        MediaListPanel.Visibility = hasMedia ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaList.SelectedItem is MediaItem item)
            ShowInPreview(item);
    }

    // ===== Prehravac =====

    private void ShowInPreview(MediaItem item)
    {
        ClearPreview();
        _currentMedia = item;

        switch (item.Kind)
        {
            case MediaKind.Image:
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(item.FilePath);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // nezamkne soubor na disku
                    bitmap.EndInit();
                    PreviewImage.Source = bitmap;
                    PreviewImage.Visibility = Visibility.Visible;
                    ShowMediaDetails(item, $"{bitmap.PixelWidth} × {bitmap.PixelHeight}");
                }
                catch (Exception)
                {
                    ShowPreviewMessage("Tento obrázek nejde otevřít.");
                    ShowMediaDetails(item, null);
                }
                return;

            case MediaKind.Audio:
                AudioNameText.Text = item.Name;
                AudioPlaceholder.Visibility = Visibility.Visible;
                break;
        }

        // Video a zvuk: Pause() soubor otevre a u videa ukaze prvni snimek
        Player.Source = new Uri(item.FilePath);
        Player.Pause();
        ShowMediaDetails(item, null);
    }

    private void ClearPreview()
    {
        Player.Stop();
        Player.Source = null;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        AudioPlaceholder.Visibility = Visibility.Collapsed;
        PreviewMessage.Visibility = Visibility.Collapsed;

        _currentMedia = null;
        SetPlaying(false);
        PlayButton.IsEnabled = false;
        SeekSlider.IsEnabled = false;
        SeekSlider.Value = 0;
        CurrentTimeText.Text = FormatTimecode(TimeSpan.Zero);
        TotalTimeText.Text = FormatTimecode(TimeSpan.Zero);
        ShowProjectDetails();
    }

    private void ShowPreviewMessage(string message)
    {
        PreviewMessage.Text = message;
        PreviewMessage.Visibility = Visibility.Visible;
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_currentMedia is null)
            return;

        var duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        _currentMedia.Duration ??= duration;

        SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 0.001);
        SeekSlider.IsEnabled = duration > TimeSpan.Zero;
        PlayButton.IsEnabled = true;
        TotalTimeText.Text = FormatTimecode(duration);

        string? resolution = Player.HasVideo ? $"{Player.NaturalVideoWidth} × {Player.NaturalVideoHeight}" : null;
        ShowMediaDetails(_currentMedia, resolution);
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Pause();
        SetPlaying(false);
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Player.Source = null;
        AudioPlaceholder.Visibility = Visibility.Collapsed;
        ShowPreviewMessage("Tento soubor nejde přehrát.\nWindows pro něj možná nemá kodek.");
        PlayButton.IsEnabled = false;
        SeekSlider.IsEnabled = false;
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox)
        {
            TogglePlayback();
            e.Handled = true;
        }
    }

    private void TogglePlayback()
    {
        if (!PlayButton.IsEnabled)
            return;

        if (_isPlaying)
        {
            Player.Pause();
            SetPlaying(false);
            return;
        }

        // Na konci zacni znovu od zacatku
        if (Player.NaturalDuration.HasTimeSpan && Player.Position >= Player.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(50))
            Player.Position = TimeSpan.Zero;

        Player.Play();
        SetPlaying(true);
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayButton.Content = playing ? PauseGlyph : PlayGlyph;
        PlayButton.ToolTip = playing ? "Pozastavit (mezerník)" : "Přehrát (mezerník)";
    }

    private void UpdatePlaybackPosition()
    {
        if (Player.Source is null || _isSeeking)
            return;

        SeekSlider.Value = Player.Position.TotalSeconds;
        CurrentTimeText.Text = FormatTimecode(Player.Position);
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Player.Position = TimeSpan.FromSeconds(SeekSlider.Value);
        _isSeeking = false;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Pri tazeni posuvniku prubezne ukazuj snimek pod nim
        if (!_isSeeking || Player.Source is null)
            return;

        var position = TimeSpan.FromSeconds(e.NewValue);
        Player.Position = position;
        CurrentTimeText.Text = FormatTimecode(position);
    }

    private static string FormatTimecode(TimeSpan t)
    {
        int frames = (int)(t.Milliseconds / 1000.0 * FramesPerSecond);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}:{frames:00}";
    }

    // ===== Panel Podrobnosti =====

    private void ShowProjectDetails()
    {
        DetailsList.ItemsSource = new Dictionary<string, string>
        {
            ["Název:"] = "Nový projekt",
            ["Umístění:"] = "Neuloženo",
            ["Poměr stran:"] = "16:9",
            ["Rozlišení:"] = "1920 × 1080",
            ["Snímková frekvence:"] = $"{FramesPerSecond} fps",
            ["Importovaná média:"] = "Zachovat původní umístění"
        };
    }

    private void ShowMediaDetails(MediaItem item, string? resolution)
    {
        var details = new Dictionary<string, string>
        {
            ["Název:"] = item.Name,
            ["Cesta:"] = Path.GetDirectoryName(item.FilePath) ?? "",
            ["Typ:"] = item.KindName
        };

        if (item.Duration is { } duration)
            details["Délka:"] = MediaItem.FormatDuration(duration);
        if (resolution is not null)
            details["Rozlišení:"] = resolution;

        try
        {
            details["Velikost:"] = FormatFileSize(new FileInfo(item.FilePath).Length);
        }
        catch (IOException)
        {
            // Soubor mezitim zmizel - velikost proste neukazeme
        }

        DetailsList.ItemsSource = details;
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} kB"
    };
}
