using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MujVideoEditor.Models;

namespace MujVideoEditor.Controls;

/// <summary>
/// Casova osa: zobrazuje klipy, pravitko a ukazatel pozice a umi klipy
/// pridavat, presouvat, zkracovat, rozdelovat a mazat (se Zpet/Znovu).
/// </summary>
public partial class TimelineView : UserControl
{
    private const double RulerHeight = 26;
    private const double DragThreshold = 4;

    // (hlavni dilek v sekundach, pocet mensich dilku mezi nimi)
    private static readonly (double Major, int Divisions)[] RulerSteps =
        [(1, 5), (2, 4), (5, 5), (10, 5), (15, 3), (30, 6), (60, 6), (120, 4), (300, 5), (600, 6), (1800, 6), (3600, 6)];

    private readonly Stack<TimelineState> _undoStack = new();
    private readonly Stack<TimelineState> _redoStack = new();

    private double _pixelsPerSecond;
    private TimeSpan _playhead;
    private TimelineClip? _selectedClip;

    // Stav tazeni mysi
    private bool _isScrubbing;
    private TimelineClip? _dragClip;
    private double _dragStartX;
    private bool _dragMoved;
    private TimelineClip? _trimClip;
    private bool _trimLeft;
    private TimeSpan _trimOriginalIn;
    private TimeSpan _trimOriginalOut;
    private TimeSpan _originalStart;
    private TimelineState? _stateBeforeGesture;

    public TimelineView()
    {
        InitializeComponent();
        VideoTrack.ItemsSource = Model.VideoClips;
        AudioTrack.ItemsSource = Model.AudioClips;
        TextTrack.ItemsSource = Model.TextClips;
        _pixelsPerSecond = PixelsPerSecondFromZoom(ZoomSlider.Value);
        Loaded += (_, _) => Relayout();
        UpdateButtons();
    }

    public Timeline Model { get; } = new();

    /// <summary>Zjisti delku media (MainWindow ji nacita pres MediaPlayer).</summary>
    public Func<MediaItem, Task<TimeSpan?>>? DurationProvider { get; set; }

    public TimeSpan Playhead => _playhead;
    public TimelineClip? SelectedClip => _selectedClip;

    /// <summary>Uzivatel s casovou osou nejak pracuje (prehravac se ma prepnout na ni).</summary>
    public event EventHandler? Activated;

    /// <summary>Uzivatel posunul ukazatel pozice.</summary>
    public event EventHandler<TimeSpan>? SeekRequested;

    /// <summary>Zmenily se klipy (pridani, presun, zkraceni...).</summary>
    public event EventHandler? Changed;

    public event EventHandler<TimelineClip?>? SelectedClipChanged;

    // ===== Verejne operace =====

    public void SetPlayhead(TimeSpan time, bool followPlayback)
    {
        _playhead = time;
        double x = time.TotalSeconds * _pixelsPerSecond;
        Canvas.SetLeft(PlayheadMarker, x - PlayheadMarker.Width / 2);

        // Pri prehravani posouvej pohled, aby ukazatel neutekl z obrazovky
        if (followPlayback && (x > Scroller.HorizontalOffset + Scroller.ViewportWidth - 40 || x < Scroller.HorizontalOffset))
            Scroller.ScrollToHorizontalOffset(Math.Max(0, x - 40));
    }

    public async Task AddMediaAsync(MediaItem media, int? index = null)
    {
        TimeSpan? duration = media.Kind == MediaKind.Image
            ? TimelineClip.DefaultImageDuration
            : media.Duration ?? (DurationProvider is null ? null : await DurationProvider(media));

        if (duration is not { } length || length < TimelineClip.MinDuration)
        {
            MessageBox.Show(Window.GetWindow(this), $"Soubor {media.Name} nejde přidat, nepodařilo se zjistit jeho délku.",
                "Časová osa", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PushUndo();
        var clip = new TimelineClip(media, TimeSpan.Zero, length);
        var track = Model.Track(Timeline.TrackFor(media));
        track.Insert(Math.Clamp(index ?? track.Count, 0, track.Count), clip);

        SelectClip(clip);
        OnModelChanged();
        Activated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Prida text na pozici ukazatele (nebo za nejblizsi text, aby se neprekryvaly).</summary>
    public void AddText(TextOverlay overlay)
    {
        PushUndo();
        var start = Model.FreeTextSlot(_playhead, TimelineClip.DefaultTextDuration);
        var clip = TimelineClip.CreateText(overlay, start, TimelineClip.DefaultTextDuration);
        Model.TextClips.Add(clip);

        SelectClip(clip);
        OnModelChanged();
        Activated?.Invoke(this, EventArgs.Empty);
    }

    public void SelectClip(TimelineClip? clip)
    {
        if (_selectedClip == clip)
            return;

        if (_selectedClip is not null)
            _selectedClip.IsSelected = false;
        _selectedClip = clip;
        if (clip is not null)
            clip.IsSelected = true;

        UpdateButtons();
        SelectedClipChanged?.Invoke(this, clip);
    }

    /// <summary>Ulozi stav pred zmenou, ktera probehla mimo casovou osu (napr. uprava textu), aby sla vratit.</summary>
    public void PushUndoState(TimelineState stateBeforeChange)
    {
        _undoStack.Push(stateBeforeChange);
        _redoStack.Clear();
        UpdateButtons();
    }

    public void SplitAtPlayhead()
    {
        // Vybrany klip pod ukazatelem, jinak klip pod ukazatelem ve stope Video, Zvuk, Text
        var clip = _selectedClip is { } selected && _playhead >= selected.Start && _playhead < selected.End
            ? selected
            : (Model.ClipAt(TrackKind.Video, _playhead) ?? Model.ClipAt(TrackKind.Audio, _playhead)
               ?? Model.ClipAt(TrackKind.Text, _playhead))?.Clip;

        if (clip is null || !Model.CanSplit(clip, _playhead))
            return;

        PushUndo();
        var second = Model.Split(clip, _playhead);
        SelectClip(second);
        OnModelChanged();
    }

    public void DeleteSelected()
    {
        if (_selectedClip is null)
            return;

        PushUndo();
        Model.Remove(_selectedClip);
        SelectClip(null);
        OnModelChanged();
    }

    /// <summary>Odstrani klipy media, ktere uzivatel smazal z knihovny.</summary>
    public void RemoveClipsOf(MediaItem media)
    {
        var clips = Model.AllClips.Where(c => c.Media == media).ToList();
        if (clips.Count == 0)
            return;

        PushUndo();
        foreach (var clip in clips)
            Model.Remove(clip);
        if (_selectedClip is not null && clips.Contains(_selectedClip))
            SelectClip(null);
        OnModelChanged();
    }

    /// <summary>Nahradi celou casovou osu (novy nebo otevreny projekt); historie Zpet se smaze.</summary>
    public void Load(TimelineState state)
    {
        Model.Restore(state);
        _undoStack.Clear();
        _redoStack.Clear();
        SelectClip(null);
        SetPlayhead(TimeSpan.Zero, followPlayback: false);
        Scroller.ScrollToHorizontalOffset(0);
        OnModelChanged();
    }

    public void Undo() => RestoreFrom(_undoStack, _redoStack);

    public void Redo() => RestoreFrom(_redoStack, _undoStack);

    private void RestoreFrom(Stack<TimelineState> from, Stack<TimelineState> to)
    {
        if (from.Count == 0)
            return;

        to.Push(Model.Snapshot());
        Model.Restore(from.Pop());
        SelectClip(null);
        OnModelChanged();
    }

    private void PushUndo()
    {
        _undoStack.Push(Model.Snapshot());
        _redoStack.Clear();
    }

    // ===== Vykresleni =====

    private void OnModelChanged()
    {
        Relayout();
        UpdateButtons();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Relayout()
    {
        Model.Relayout(_pixelsPerSecond);

        double viewport = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
        double contentWidth = (Model.Duration.TotalSeconds + 30) * _pixelsPerSecond;
        ContentGrid.Width = Math.Max(viewport, contentWidth);

        EmptyHint.Visibility = Model.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        DrawRuler();
        SetPlayhead(_playhead, followPlayback: false);
    }

    private void DrawRuler()
    {
        RulerCanvas.Children.Clear();

        var (major, divisions) = RulerSteps.FirstOrDefault(s => s.Major * _pixelsPerSecond >= 80, RulerSteps[^1]);
        double minor = major / divisions;
        var tickBrush = (Brush)FindResource("BorderBrush");
        var textBrush = (Brush)FindResource("TextSecondary");

        // Kreslime jen viditelnou cast, at je to rychle i u dlouhych projektu
        double from = Math.Max(0, Scroller.HorizontalOffset - 100);
        double to = Math.Min(ContentGrid.Width, Scroller.HorizontalOffset + Math.Max(Scroller.ViewportWidth, ActualWidth) + 100);

        for (long i = (long)(from / _pixelsPerSecond / minor); i * minor * _pixelsPerSecond <= to; i++)
        {
            double x = i * minor * _pixelsPerSecond;
            bool isMajor = i % divisions == 0;
            double height = isMajor ? 10 : 5;

            var tick = new Rectangle { Width = 1, Height = height, Fill = isMajor ? textBrush : tickBrush };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, RulerHeight - height);
            RulerCanvas.Children.Add(tick);

            if (isMajor)
            {
                var label = new TextBlock
                {
                    Text = FormatRulerTime(TimeSpan.FromSeconds(i * minor)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 10,
                    Foreground = textBrush
                };
                Canvas.SetLeft(label, x + 4);
                Canvas.SetTop(label, 3);
                RulerCanvas.Children.Add(label);
            }
        }
    }

    private static string FormatRulerTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    private void UpdateButtons()
    {
        UndoButton.IsEnabled = _undoStack.Count > 0;
        RedoButton.IsEnabled = _redoStack.Count > 0;
        SplitButton.IsEnabled = !Model.IsEmpty;
        DeleteButton.IsEnabled = _selectedClip is not null;
    }

    // ===== Priblizeni a posouvani =====

    private static double PixelsPerSecondFromZoom(double zoom) => 2 * Math.Pow(1.07, zoom);

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;

        // Ukazatel pozice zustane na stejnem miste obrazovky
        double playheadOnScreen = _playhead.TotalSeconds * _pixelsPerSecond - Scroller.HorizontalOffset;
        _pixelsPerSecond = PixelsPerSecondFromZoom(e.NewValue);
        Relayout();
        Scroller.ScrollToHorizontalOffset(Math.Max(0, _playhead.TotalSeconds * _pixelsPerSecond - playheadOnScreen));
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value += 10;

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomSlider.Value -= 10;

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            ZoomSlider.Value += e.Delta > 0 ? 4 : -4;
        else
            Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0)
            Relayout();
        else if (e.HorizontalChange != 0)
            DrawRuler();
    }

    // ===== Mys: posun ukazatele, presun a zkraceni klipu =====

    private void ContentGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Klik do prazdna nebo na pravitko: zrus vyber a posun ukazatel
        Activated?.Invoke(this, EventArgs.Empty);
        SelectClip(null);
        _isScrubbing = true;
        ContentGrid.CaptureMouse();
        SeekToX(e.GetPosition(ContentGrid).X);
        e.Handled = true;
    }

    private void Clip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TimelineClip clip)
            return;

        Activated?.Invoke(this, EventArgs.Empty);
        SelectClip(clip);
        _dragClip = clip;
        _dragStartX = e.GetPosition(ContentGrid).X;
        _originalStart = clip.Start;
        _dragMoved = false;
        _stateBeforeGesture = Model.Snapshot();
        ContentGrid.CaptureMouse();
        e.Handled = true;
    }

    private void TrimHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TimelineClip clip } handle)
            return;

        Activated?.Invoke(this, EventArgs.Empty);
        SelectClip(clip);
        _trimClip = clip;
        _trimLeft = (string)handle.Tag == "Left";
        _dragStartX = e.GetPosition(ContentGrid).X;
        _trimOriginalIn = clip.SourceIn;
        _trimOriginalOut = clip.SourceOut;
        _originalStart = clip.Start;
        _stateBeforeGesture = Model.Snapshot();
        ContentGrid.CaptureMouse();
        e.Handled = true;
    }

    private void ContentGrid_MouseMove(object sender, MouseEventArgs e)
    {
        double x = e.GetPosition(ContentGrid).X;

        if (_isScrubbing)
        {
            SeekToX(x);
        }
        else if (_trimClip is not null)
        {
            ApplyTrim(x);
        }
        else if (_dragClip is not null)
        {
            if (!_dragMoved && Math.Abs(x - _dragStartX) < DragThreshold)
                return;
            _dragMoved = true;
            MoveClipTo(_dragClip, x);
        }
    }

    private void ContentGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ContentGrid.ReleaseMouseCapture();

    private void ContentGrid_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _isScrubbing = false;
        _dragClip = null;
        _trimClip = null;
        _stateBeforeGesture = null;
    }

    private void SeekToX(double x)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, x / _pixelsPerSecond));
        if (time > Model.Duration)
            time = Model.Duration;

        SetPlayhead(time, followPlayback: false);
        SeekRequested?.Invoke(this, time);
    }

    /// <summary>Prvni zmena behem tazeni ulozi stav pred tazenim pro Zpet.</summary>
    private void CommitGestureUndo()
    {
        if (_stateBeforeGesture is null)
            return;

        _undoStack.Push(_stateBeforeGesture);
        _redoStack.Clear();
        _stateBeforeGesture = null;
    }

    private void ApplyTrim(double x)
    {
        var clip = _trimClip!;
        var delta = TimeSpan.FromSeconds((x - _dragStartX) / _pixelsPerSecond);
        var oldDuration = clip.Duration;

        if (clip.IsText)
        {
            // Text: levy okraj posouva zacatek (konec zustava), pravy meni delku
            if (_trimLeft)
            {
                var end = _originalStart + (_trimOriginalOut - _trimOriginalIn);
                var newStart = Clamp(_originalStart + delta, TimeSpan.Zero, end - TimelineClip.MinDuration);
                clip.Start = newStart;
                clip.SourceIn = TimeSpan.Zero;
                clip.SourceOut = end - newStart;
            }
            else
            {
                clip.SourceOut = Clamp(_trimOriginalOut + delta, clip.SourceIn + TimelineClip.MinDuration, clip.MaxSourceOut);
            }
        }
        else if (clip.Media.Kind == MediaKind.Image)
        {
            // Obrazek nema "zdroj", oba okraje jen meni jeho delku
            var newOut = _trimLeft ? _trimOriginalOut - delta : _trimOriginalOut + delta;
            clip.SourceOut = Clamp(newOut, clip.SourceIn + TimelineClip.MinDuration, TimelineClip.MaxImageDuration);
        }
        else if (_trimLeft)
        {
            clip.SourceIn = Clamp(_trimOriginalIn + delta, TimeSpan.Zero, clip.SourceOut - TimelineClip.MinDuration);
        }
        else
        {
            clip.SourceOut = Clamp(_trimOriginalOut + delta, clip.SourceIn + TimelineClip.MinDuration, clip.MaxSourceOut);
        }

        if (clip.Duration != oldDuration)
        {
            CommitGestureUndo();
            OnModelChanged();
        }
    }

    private void MoveClipTo(TimelineClip clip, double x)
    {
        if (clip.IsText)
        {
            // Text se posouva volne
            var newStart = TimeSpan.FromSeconds(Math.Max(0, (_originalStart.TotalSeconds * _pixelsPerSecond + x - _dragStartX) / _pixelsPerSecond));
            if (newStart == clip.Start)
                return;
            CommitGestureUndo();
            clip.Start = newStart;
            OnModelChanged();
            return;
        }

        var track = Model.TrackOf(clip);
        int oldIndex = track.IndexOf(clip);
        int newIndex = InsertionIndex(track.Where(c => c != clip), x);

        if (newIndex == oldIndex)
            return;

        CommitGestureUndo();
        track.Move(oldIndex, newIndex);
        OnModelChanged();
    }

    /// <summary>Kolik klipu lezi (svym stredem) vlevo od pozice x.</summary>
    private static int InsertionIndex(IEnumerable<TimelineClip> clips, double x)
    {
        int index = 0;
        double position = 0;
        foreach (var clip in clips)
        {
            if (x > position + clip.Width / 2)
                index++;
            position += clip.Width;
        }
        return index;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    // ===== Pretazeni media z knihovny =====

    private void ContentGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(MediaItem)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ContentGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(MediaItem)) is not MediaItem media)
            return;

        var track = Model.Track(Timeline.TrackFor(media));
        int index = InsertionIndex(track, e.GetPosition(ContentGrid).X);
        await AddMediaAsync(media, index);
    }

    // ===== Tlacitka =====

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void RedoButton_Click(object sender, RoutedEventArgs e) => Redo();

    private void SplitButton_Click(object sender, RoutedEventArgs e) => SplitAtPlayhead();

    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteSelected();
}
