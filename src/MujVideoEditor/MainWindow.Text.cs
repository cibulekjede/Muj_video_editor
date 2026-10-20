using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MujVideoEditor.Models;

namespace MujVideoEditor;

/// <summary>
/// Texty ve videu: predlohy v zalozce Text, vykresleni v nahledu (s pretahovanim mysi)
/// a editor textu v pravem panelu.
/// </summary>
public partial class MainWindow
{
    // Nahled ma vnitrni vysku 900 (sirka podle pomeru stran). Velikost pisma je definovana
    // pro kratsi stranu videa 1080 px, aby se text vesel i do videa na vysku.
    private double PreviewWidth => PreviewCanvas.Width;
    private const double PreviewHeight = 900;
    private double PreviewScale => Math.Min(PreviewWidth, PreviewHeight) / 1080;

    private List<TimelineClip> _shownTexts = [];
    private bool _textLayerDirty = true;

    private TimelineClip? _editedTextClip;
    private TimelineState? _stateBeforeTextEdit;
    private bool _syncingTextEditor;

    private TimelineClip? _draggedText;
    private Vector _dragTextOffset;

    // ===== Zalozky v levem panelu =====

    private void CategoryTab_Checked(object sender, RoutedEventArgs e)
    {
        if (MediaCategoryPanel is null)
            return; // jeste se nacita okno

        bool media = sender == MediaTab || sender == AudioTab;
        bool text = sender == TextTab || sender == SubtitlesTab;

        // Zalozka Zvuk = knihovna medii jen se zvukem
        if (sender == AudioTab)
            FilterAudio.IsChecked = true;
        else if (sender == MediaTab && FilterAudio.IsChecked == true)
            FilterAll.IsChecked = true;
        MediaCategoryPanel.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        TextCategoryPanel.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        ComingSoonPanel.Visibility = media || text ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TextPreset_Click(object sender, RoutedEventArgs e)
    {
        var overlay = (sender as FrameworkElement)?.Tag switch
        {
            "Subtitle" => TextOverlay.Subtitle(),
            "Headline" => TextOverlay.Headline(),
            "Yellow" => TextOverlay.Yellow(),
            "Comic" => TextOverlay.Comic(),
            _ => TextOverlay.DefaultText()
        };

        TimelineEditor.AddText(overlay);
        TextContentBox.Focus();
        TextContentBox.SelectAll();
    }

    // ===== Vykresleni textu v nahledu =====

    private void RenderTexts(TimeSpan position)
    {
        var active = _mode == PreviewMode.Timeline ? TimelineEditor.Model.TextsAt(position) : [];
        if (!_textLayerDirty && active.SequenceEqual(_shownTexts))
            return;

        _shownTexts = active;
        _textLayerDirty = false;
        TextLayer.Children.Clear();

        foreach (var clip in active)
        {
            var element = CreateTextElement(clip);
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = element.DesiredSize;
            Canvas.SetLeft(element, clip.Overlay!.X * PreviewWidth - size.Width / 2);
            Canvas.SetTop(element, clip.Overlay.Y * PreviewHeight - size.Height / 2);
            TextLayer.Children.Add(element);
        }
    }

    private FrameworkElement CreateTextElement(TimelineClip clip)
    {
        var overlay = clip.Overlay!;
        var text = new TextBlock
        {
            Text = overlay.Text,
            FontFamily = new FontFamily(overlay.FontFamily),
            FontSize = Math.Max(1, overlay.FontSize * PreviewScale),
            FontWeight = overlay.Bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(overlay.Color)),
            TextAlignment = TextAlignment.Center
        };

        if (overlay.HasShadow)
            text.Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 3, BlurRadius = 0, Opacity = 0.6 };

        bool selected = clip == TimelineEditor.SelectedClip;
        var box = new Border
        {
            Child = text,
            Tag = clip,
            Cursor = Cursors.SizeAll,
            Padding = new Thickness(overlay.HasBackground ? 10 * PreviewScale : 0),
            Background = overlay.HasBackground ? new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) : Brushes.Transparent,
            // Vybrany text ma oramovani jako v CapCutu
            BorderBrush = selected ? Brushes.White : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 2 : 0)
        };
        box.MouseLeftButtonDown += TextElement_MouseLeftButtonDown;
        return box;
    }

    private void RefreshTexts()
    {
        _textLayerDirty = true;
        RenderTexts(TimelinePosition);
    }

    // ===== Pretahovani textu mysi v nahledu =====

    private void TextElement_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TimelineClip clip } element || clip.Overlay is null)
            return;

        if (_isPlaying)
            TogglePlayback();

        TimelineEditor.SelectClip(clip);
        _draggedText = clip;
        var center = new Point(clip.Overlay.X * PreviewWidth, clip.Overlay.Y * PreviewHeight);
        _dragTextOffset = e.GetPosition(TextLayer) - center;
        TextLayer.CaptureMouse();
        e.Handled = true;
    }

    private void TextLayer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedText?.Overlay is not { } overlay)
            return;

        var position = e.GetPosition(TextLayer) - _dragTextOffset;
        overlay.X = position.X / PreviewWidth;
        overlay.Y = position.Y / PreviewHeight;
    }

    private void TextLayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => TextLayer.ReleaseMouseCapture();

    private void TextLayer_LostMouseCapture(object sender, MouseEventArgs e) => _draggedText = null;

    // ===== Editor textu v pravem panelu =====

    private void ShowTextEditor(TimelineClip? clip)
    {
        if (_editedTextClip?.Overlay is { } previous)
            previous.PropertyChanged -= EditedOverlay_PropertyChanged;

        _editedTextClip = clip?.Overlay is null ? null : clip;
        bool editing = _editedTextClip is not null;
        TextEditorPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        DetailsList.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        DetailsTitle.Text = editing ? "Text" : "Podrobnosti";
        RefreshTexts(); // zmena oramovani vybraneho textu

        if (_editedTextClip?.Overlay is not { } overlay)
            return;

        // Stav pred upravami, aby sly vratit pres Zpet
        _stateBeforeTextEdit = TimelineEditor.Model.Snapshot();
        overlay.PropertyChanged += EditedOverlay_PropertyChanged;
        SyncTextEditor(overlay);
    }

    private void SyncTextEditor(TextOverlay overlay)
    {
        _syncingTextEditor = true;
        try
        {
            if (TextContentBox.Text != overlay.Text)
                TextContentBox.Text = overlay.Text;
            FontSizeSlider.Value = overlay.FontSize;
            FontSizeText.Text = $"{overlay.FontSize:0} px";
            BoldCheck.IsChecked = overlay.Bold;
            ShadowCheck.IsChecked = overlay.HasShadow;
            BackgroundCheck.IsChecked = overlay.HasBackground;

            EnsureChoiceButtons();
            foreach (RadioButton font in FontChoices.Children)
                font.IsChecked = (string)font.Tag == overlay.FontFamily;
            foreach (RadioButton color in ColorChoices.Children)
                color.IsChecked = string.Equals((string)color.Tag, overlay.Color, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _syncingTextEditor = false;
        }
    }

    /// <summary>Vytvori tlacitka pisem a barev (jednou).</summary>
    private void EnsureChoiceButtons()
    {
        if (FontChoices.Children.Count > 0)
            return;

        var chip = (Style)FindResource("ChipRadio");
        foreach (string font in TextOverlay.Fonts)
        {
            var button = new RadioButton
            {
                Style = chip,
                GroupName = "TextFont",
                Content = font,
                FontFamily = new FontFamily(font),
                Tag = font,
                Margin = new Thickness(0, 0, 6, 6)
            };
            button.Click += (_, _) => EditText(o => o.FontFamily = font);
            FontChoices.Children.Add(button);
        }

        var swatch = (Style)FindResource("ColorSwatch");
        foreach (string color in TextOverlay.Colors)
        {
            var button = new RadioButton
            {
                Style = swatch,
                GroupName = "TextColor",
                Tag = color,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
            };
            button.Click += (_, _) => EditText(o => o.Color = color);
            ColorChoices.Children.Add(button);
        }
    }

    private void EditText(Action<TextOverlay> change)
    {
        if (!_syncingTextEditor && _editedTextClip?.Overlay is { } overlay)
            change(overlay);
    }

    private void TextContentBox_TextChanged(object sender, TextChangedEventArgs e) =>
        EditText(o => o.Text = TextContentBox.Text);

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        EditText(o => o.FontSize = Math.Round(e.NewValue));

    private void TextStyle_Click(object sender, RoutedEventArgs e) => EditText(o =>
    {
        o.Bold = BoldCheck.IsChecked == true;
        o.HasShadow = ShadowCheck.IsChecked == true;
        o.HasBackground = BackgroundCheck.IsChecked == true;
    });

    private void TextPosition_Click(object sender, RoutedEventArgs e) => EditText(o =>
    {
        o.X = 0.5;
        o.Y = double.Parse((string)((FrameworkElement)sender).Tag, System.Globalization.CultureInfo.InvariantCulture);
    });

    private void EditedOverlay_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Prvni uprava vybraneho textu ulozi stav pro Zpet
        if (_stateBeforeTextEdit is not null)
        {
            TimelineEditor.PushUndoState(_stateBeforeTextEdit);
            _stateBeforeTextEdit = null;
        }

        if (sender is TextOverlay overlay && !_syncingTextEditor)
            SyncTextEditor(overlay); // napr. pri tazeni mysi v nahledu

        RefreshTexts();
        MarkDirty();
    }
}
