using System.Collections.ObjectModel;
using System.Diagnostics;
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

    /// <summary>Co prave ukazuje prehravac.</summary>
    private enum PreviewMode
    {
        None,
        Library,   // jedno medium vybrane v knihovne
        Timeline   // cely sestrih z casove osy
    }

    /// <summary>Co je nactene v jednom MediaElementu pri prehravani casove osy.</summary>
    private sealed class ElementState
    {
        public string? FilePath;
        public bool IsOpened;
        public bool IsPlaying;
        public TimeSpan? PendingSeek;
    }

    private readonly ObservableCollection<MediaItem> _mediaItems = [];
    private readonly DispatcherTimer _playbackTimer;
    private readonly Dictionary<MediaItem, Task<TimeSpan?>> _durationTasks = [];
    private readonly Dictionary<string, BitmapImage> _imageCache = [];

    // Prehravace pouzite jen pro zjisteni delky; drzime je, aby je mezitim neuklidil garbage collector
    private readonly HashSet<MediaPlayer> _durationProbes = [];

    private PreviewMode _mode = PreviewMode.None;
    private MediaItem? _currentMedia;
    private bool _isPlaying;
    private bool _isSeeking;

    // Prehravani casove osy ridi vlastni hodiny; videa se k nim jen dorovnavaji
    private readonly Stopwatch _timelineClock = new();
    private TimeSpan _timelineClockStart;
    private ElementState _videoState = new();
    private ElementState _audioState = new();

    // Tazeni media z knihovny na casovou osu
    private Point _libraryDragStart;
    private MediaItem? _libraryDragItem;

    public MainWindow()
    {
        InitializeComponent();

        MediaList.ItemsSource = _mediaItems;
        _mediaItems.CollectionChanged += (_, _) => UpdateMediaLibraryView();

        TimelineEditor.DurationProvider = GetDurationAsync;
        TimelineEditor.Activated += (_, _) => EnterTimelineMode();
        TimelineEditor.SeekRequested += (_, time) => SeekTimeline(time);
        TimelineEditor.Changed += (_, _) => OnTimelineChanged();
        TimelineEditor.SelectedClipChanged += (_, clip) => OnTimelineClipSelected(clip);

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playbackTimer.Tick += (_, _) => OnPlaybackTick();
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

    // ===== Export =====

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var timeline = TimelineEditor.Model;
        if (timeline.IsEmpty)
        {
            MessageBox.Show(this, "Časová osa je prázdná. Nejdřív na ni přidej nějaká videa, obrázky nebo hudbu.",
                "Exportovat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_isPlaying)
            TogglePlayback();

        string? ffmpeg = FindOrAskForFfmpeg();
        if (ffmpeg is null)
            return;

        new ExportWindow(timeline.Snapshot(), timeline.Duration, ffmpeg) { Owner = this }.ShowDialog();
    }

    private static string? _chosenFfmpegPath;

    private string? FindOrAskForFfmpeg()
    {
        string? found = _chosenFfmpegPath ?? FfmpegExporter.FindFfmpeg();
        if (found is not null)
            return found;

        var answer = MessageBox.Show(this,
            "K exportu je potřeba program FFmpeg, ale v počítači jsem ho nenašel.\n\n" +
            "Nainstaluješ ho příkazem v terminálu:\n    winget install Gyan.FFmpeg\n" +
            "a pak editor spusť znovu.\n\n" +
            "Pokud už ffmpeg.exe máš, můžeš ho teď vybrat ručně. Chceš ho vybrat?",
            "Chybí FFmpeg", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
            return null;

        var dialog = new OpenFileDialog { Title = "Vyber ffmpeg.exe", Filter = "FFmpeg|ffmpeg.exe" };
        if (dialog.ShowDialog(this) != true)
            return null;

        _chosenFfmpegPath = dialog.FileName;
        return _chosenFfmpegPath;
    }

    // ===== Klavesove zkratky =====

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
            return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Space:
                TogglePlayback();
                break;
            case Key.Delete:
                TimelineEditor.DeleteSelected();
                break;
            case Key.B when ctrl:
                TimelineEditor.SplitAtPlayhead();
                break;
            case Key.Z when ctrl:
                TimelineEditor.Undo();
                break;
            case Key.Y when ctrl:
                TimelineEditor.Redo();
                break;
            default:
                return;
        }
        e.Handled = true;
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
                _ = GetDurationAsync(item);

            // Nahled se generuje mimo hlavni vlakno, aby okno nezamrzlo
            item.Thumbnail = await Task.Run(() => ShellThumbnail.TryGet(item.FilePath, 256));
        }
    }

    /// <summary>Zjisti delku videa/zvuku (jednou; dalsi volani vrati stejny vysledek).</summary>
    private Task<TimeSpan?> GetDurationAsync(MediaItem item)
    {
        if (item.Duration is { } known)
            return Task.FromResult<TimeSpan?>(known);
        if (_durationTasks.TryGetValue(item, out var pending))
            return pending;

        var result = new TaskCompletionSource<TimeSpan?>();
        var probe = new MediaPlayer();
        _durationProbes.Add(probe);

        void Finish(TimeSpan? duration)
        {
            probe.Close();
            _durationProbes.Remove(probe);
            if (duration is not null)
                item.Duration = duration;
            result.TrySetResult(duration);
        }

        probe.MediaOpened += (_, _) =>
            Finish(probe.NaturalDuration.HasTimeSpan ? probe.NaturalDuration.TimeSpan : null);
        probe.MediaFailed += (_, _) => Finish(null);
        probe.Open(new Uri(item.FilePath));

        _durationTasks[item] = result.Task;
        return result.Task;
    }

    private void RemoveMedia_Click(object sender, RoutedEventArgs e)
    {
        // Prave tlacitko mysi polozku v seznamu zaroven vybere
        if (MediaList.SelectedItem is not MediaItem item)
            return;

        if (item == _currentMedia)
            ClearPreview();

        TimelineEditor.RemoveClipsOf(item);
        _mediaItems.Remove(item);
    }

    private async void AddToTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is MediaItem item)
            await TimelineEditor.AddMediaAsync(item);
    }

    private async void AddTileToTimeline_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MediaItem item)
            await TimelineEditor.AddMediaAsync(item);
    }

    private async void MediaList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _libraryDragStart = e.GetPosition(MediaList);
        _libraryDragItem = ItemUnderMouse(e);

        // Dvojklik zjistujeme tady: MouseDoubleClick se u videa ztraci, protoze prvni klik nacita prehravac
        if (e.ClickCount == 2 && _libraryDragItem is { } item)
        {
            _libraryDragItem = null;
            e.Handled = true;
            await TimelineEditor.AddMediaAsync(item);
        }
    }

    private void MediaList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_libraryDragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var offset = e.GetPosition(MediaList) - _libraryDragStart;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _libraryDragItem;
        _libraryDragItem = null;
        DragDrop.DoDragDrop(MediaList, new DataObject(typeof(MediaItem), item), DragDropEffects.Copy);
    }

    private static MediaItem? ItemUnderMouse(MouseEventArgs e) =>
        (e.OriginalSource as FrameworkElement)?.DataContext as MediaItem;

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

    // ===== Prehravac: spolecne =====

    private void ClearPreview()
    {
        _timelineClock.Reset();
        Player.Stop();
        Player.Source = null;
        Player.Visibility = Visibility.Visible;
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
        _videoState = new ElementState();
        _audioState = new ElementState();

        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        AudioPlaceholder.Visibility = Visibility.Collapsed;
        PreviewMessage.Visibility = Visibility.Collapsed;

        _mode = PreviewMode.None;
        _currentMedia = null;
        PlayerTitle.Text = "Přehrávač";
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

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        // Mezernik bez vybraneho media prehraje casovou osu
        if (_mode == PreviewMode.None && !TimelineEditor.Model.IsEmpty)
            EnterTimelineMode();

        if (!PlayButton.IsEnabled)
            return;

        if (_mode == PreviewMode.Timeline)
            ToggleTimelinePlayback();
        else
            ToggleLibraryPlayback();
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayButton.Content = playing ? PauseGlyph : PlayGlyph;
        PlayButton.ToolTip = playing ? "Pozastavit (mezerník)" : "Přehrát (mezerník)";
    }

    private void OnPlaybackTick()
    {
        if (_mode == PreviewMode.Library)
            UpdateLibraryPosition();
        else if (_mode == PreviewMode.Timeline && _isPlaying)
            AdvanceTimeline();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_mode == PreviewMode.Timeline)
            OnTimelineElementOpened(Player, _videoState);
        else if (_mode == PreviewMode.Library)
            OnLibraryMediaOpened();
    }

    private void AudioPlayer_MediaOpened(object sender, RoutedEventArgs e) =>
        OnTimelineElementOpened(AudioPlayer, _audioState);

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        // Na casove ose konec prehravani urcuji hodiny, ne konec souboru
        if (_mode != PreviewMode.Library)
            return;

        Player.Pause();
        SetPlaying(false);
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Player.Source = null;
        _videoState = new ElementState();
        AudioPlaceholder.Visibility = Visibility.Collapsed;
        ShowPreviewMessage("Tento soubor nejde přehrát.\nWindows pro něj možná nemá kodek.");

        if (_mode == PreviewMode.Library)
        {
            PlayButton.IsEnabled = false;
            SeekSlider.IsEnabled = false;
        }
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SeekTo(TimeSpan.FromSeconds(SeekSlider.Value));
        _isSeeking = false;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Pri tazeni posuvniku prubezne ukazuj snimek pod nim
        if (_isSeeking)
            SeekTo(TimeSpan.FromSeconds(e.NewValue));
    }

    private void SeekTo(TimeSpan position)
    {
        if (_mode == PreviewMode.Timeline)
        {
            TimelineEditor.SetPlayhead(position, followPlayback: true);
            SeekTimeline(position);
        }
        else if (_mode == PreviewMode.Library && Player.Source is not null)
        {
            Player.Position = position;
            CurrentTimeText.Text = FormatTimecode(position);
        }
    }

    private static string FormatTimecode(TimeSpan t)
    {
        int frames = (int)(t.Milliseconds / 1000.0 * FramesPerSecond);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}:{frames:00}";
    }

    // ===== Prehravac: medium z knihovny =====

    private void ShowInPreview(MediaItem item)
    {
        ClearPreview();
        _mode = PreviewMode.Library;
        _currentMedia = item;
        PlayerTitle.Text = $"Přehrávač – {item.Name}";

        switch (item.Kind)
        {
            case MediaKind.Image:
                if (LoadImage(item.FilePath) is { } bitmap)
                {
                    PreviewImage.Source = bitmap;
                    PreviewImage.Visibility = Visibility.Visible;
                    ShowMediaDetails(item, $"{bitmap.PixelWidth} × {bitmap.PixelHeight}");
                }
                else
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

    private void OnLibraryMediaOpened()
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

    private void ToggleLibraryPlayback()
    {
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

    private void UpdateLibraryPosition()
    {
        if (Player.Source is null || _isSeeking)
            return;

        SeekSlider.Value = Player.Position.TotalSeconds;
        CurrentTimeText.Text = FormatTimecode(Player.Position);
    }

    // ===== Prehravac: casova osa =====

    private void EnterTimelineMode()
    {
        if (_mode == PreviewMode.Timeline)
            return;

        ClearPreview();
        _mode = PreviewMode.Timeline;
        PlayerTitle.Text = "Přehrávač – Časová osa";

        // Zrus vyber v knihovne, aby dalsi klik na stejne medium zase prepnul na nej
        MediaList.SelectedItem = null;

        OnTimelineChanged();
        OnTimelineClipSelected(TimelineEditor.SelectedClip);
    }

    private TimeSpan TimelinePosition =>
        _isPlaying ? _timelineClockStart + _timelineClock.Elapsed : TimelineEditor.Playhead;

    private void OnTimelineChanged()
    {
        if (_mode != PreviewMode.Timeline)
            return;

        var duration = TimelineEditor.Model.Duration;
        TotalTimeText.Text = FormatTimecode(duration);
        SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 0.001);
        SeekSlider.IsEnabled = duration > TimeSpan.Zero;
        PlayButton.IsEnabled = duration > TimeSpan.Zero;

        if (duration == TimeSpan.Zero && _isPlaying)
            StopTimelinePlayback();

        // Klip pod ukazatelem se mohl zmenit (zkraceni, presun, smazani)
        var position = TimelinePosition > duration ? duration : TimelinePosition;
        ShowTimelineFrame(position);
    }

    private void OnTimelineClipSelected(TimelineClip? clip)
    {
        if (_mode != PreviewMode.Timeline)
            return;

        if (clip is null)
            ShowProjectDetails();
        else
            ShowClipDetails(clip);
    }

    private void SeekTimeline(TimeSpan position)
    {
        if (_isPlaying)
        {
            _timelineClockStart = position;
            _timelineClock.Restart();
        }
        ShowTimelineFrame(position);
    }

    private void ToggleTimelinePlayback()
    {
        if (_isPlaying)
        {
            StopTimelinePlayback();
            return;
        }

        // Na konci zacni znovu od zacatku
        var start = TimelineEditor.Playhead;
        if (start >= TimelineEditor.Model.Duration - TimeSpan.FromMilliseconds(50))
            start = TimeSpan.Zero;

        _timelineClockStart = start;
        _timelineClock.Restart();
        SetPlaying(true);
        ShowTimelineFrame(start);
    }

    private void StopTimelinePlayback()
    {
        var position = TimelinePosition;
        _timelineClock.Reset();
        SetPlaying(false);
        TimelineEditor.SetPlayhead(position, followPlayback: false);
        ShowTimelineFrame(position);
    }

    private void AdvanceTimeline()
    {
        var position = TimelinePosition;
        var duration = TimelineEditor.Model.Duration;

        if (position >= duration)
        {
            _timelineClock.Reset();
            SetPlaying(false);
            position = duration;
        }

        TimelineEditor.SetPlayhead(position, followPlayback: true);
        ShowTimelineFrame(position);
    }

    /// <summary>Ukaze v prehravaci, co je na casove ose v danem case (a pri prehravani to prehrava).</summary>
    private void ShowTimelineFrame(TimeSpan position)
    {
        var model = TimelineEditor.Model;
        var videoHit = model.ClipAt(TrackKind.Video, position);
        var audioHit = model.ClipAt(TrackKind.Audio, position);

        // Stopa Video: video, obrazek, nebo nic (cerna)
        var videoClip = videoHit?.Clip;
        if (videoClip?.Media.Kind == MediaKind.Image)
        {
            PreviewImage.Source = LoadImage(videoClip.Media.FilePath);
            PreviewImage.Visibility = Visibility.Visible;
            Player.Visibility = Visibility.Hidden;
            SyncElement(Player, _videoState, null, TimeSpan.Zero);
        }
        else
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            Player.Visibility = videoClip is null ? Visibility.Hidden : Visibility.Visible;
            SyncElement(Player, _videoState, videoClip, videoHit?.Offset ?? TimeSpan.Zero);
        }

        // Stopa Zvuk
        SyncElement(AudioPlayer, _audioState, audioHit?.Clip, audioHit?.Offset ?? TimeSpan.Zero);

        if (!_isSeeking)
            SeekSlider.Value = position.TotalSeconds;
        CurrentTimeText.Text = FormatTimecode(position);
    }

    /// <summary>Dorovna MediaElement na spravny soubor, pozici a stav prehravani.</summary>
    private void SyncElement(MediaElement element, ElementState state, TimelineClip? clip, TimeSpan offset)
    {
        if (clip is null)
        {
            if (state.IsPlaying)
            {
                element.Pause();
                state.IsPlaying = false;
            }
            return;
        }

        var target = clip.SourceIn + offset;

        // Jiny soubor: nacti ho a po otevreni skoc na spravne misto (OnTimelineElementOpened)
        if (state.FilePath != clip.Media.FilePath)
        {
            state.FilePath = clip.Media.FilePath;
            state.IsOpened = false;
            state.PendingSeek = target;
            element.Source = new Uri(clip.Media.FilePath);
            PlayOrPause(element, state);
            return;
        }

        if (!state.IsOpened)
        {
            state.PendingSeek = target;
            return;
        }

        if (_isPlaying)
        {
            // Video bezi samo; zasahneme jen kdyz se od hodin moc vzdali (napr. na strihu)
            if (!state.IsPlaying || (element.Position - target).Duration() > TimeSpan.FromMilliseconds(250))
                element.Position = target;
        }
        else if ((element.Position - target).Duration() > TimeSpan.FromMilliseconds(15))
        {
            element.Position = target;
        }

        PlayOrPause(element, state);
    }

    private void PlayOrPause(MediaElement element, ElementState state)
    {
        if (_isPlaying && !state.IsPlaying)
            element.Play();
        else if (!_isPlaying && state.IsPlaying)
            element.Pause();
        else if (!state.IsOpened)
            element.Pause(); // otevre soubor a ukaze snimek

        state.IsPlaying = _isPlaying;
    }

    private void OnTimelineElementOpened(MediaElement element, ElementState state)
    {
        state.IsOpened = true;
        if (state.PendingSeek is { } seek)
        {
            element.Position = seek;
            state.PendingSeek = null;
        }
    }

    private BitmapImage? LoadImage(string filePath)
    {
        if (_imageCache.TryGetValue(filePath, out var cached))
            return cached;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // nezamkne soubor na disku
            bitmap.EndInit();
            bitmap.Freeze();
            _imageCache[filePath] = bitmap;
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ===== Panel Podrobnosti =====

    private void ShowProjectDetails()
    {
        var details = new Dictionary<string, string>
        {
            ["Název:"] = "Nový projekt",
            ["Umístění:"] = "Neuloženo",
            ["Poměr stran:"] = "16:9",
            ["Rozlišení:"] = "1920 × 1080",
            ["Snímková frekvence:"] = $"{FramesPerSecond} fps"
        };

        if (!TimelineEditor.Model.IsEmpty)
            details["Délka sestřihu:"] = MediaItem.FormatDuration(TimelineEditor.Model.Duration);

        DetailsList.ItemsSource = details;
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

    private void ShowClipDetails(TimelineClip clip)
    {
        var details = new Dictionary<string, string>
        {
            ["Klip:"] = clip.Media.Name,
            ["Typ:"] = clip.Media.KindName,
            ["Začátek na ose:"] = FormatTimecode(clip.Start),
            ["Délka:"] = FormatTimecode(clip.Duration)
        };

        if (clip.Media.Kind != MediaKind.Image)
            details["Úsek ze zdroje:"] = $"{FormatTimecode(clip.SourceIn)} – {FormatTimecode(clip.SourceOut)}";

        DetailsList.ItemsSource = details;
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} kB"
    };
}
