using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MujVideoEditor.Models;

namespace MujVideoEditor;

/// <summary>
/// Pomer stran, prehravani na celou obrazovku a filtry knihovny medii.
/// </summary>
public partial class MainWindow
{
    private AspectRatio _aspect = AspectRatio.Landscape;
    private string _libraryFilter = "All";

    private void InitializeViewFeatures()
    {
        MediaList.ContextMenuOpening += (_, _) =>
            FavoriteMenuItem.Header = (MediaList.SelectedItem as MediaItem)?.IsFavorite == true
                ? "Odebrat z oblíbených"
                : "Přidat do oblíbených";
    }

    // ===== Pomer stran =====

    private void AspectButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = AspectButton.ContextMenu!;
        foreach (MenuItem item in menu.Items)
            item.FontWeight = (string)item.Tag == _aspect.Name ? FontWeights.SemiBold : FontWeights.Normal;
        menu.PlacementTarget = AspectButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void AspectMenu_Click(object sender, RoutedEventArgs e)
    {
        var aspect = AspectRatio.Parse((string)((FrameworkElement)sender).Tag);
        if (aspect == _aspect)
            return;

        SetAspect(aspect);
        MarkDirty();
    }

    private void SetAspect(AspectRatio aspect)
    {
        _aspect = aspect;
        // Nahled ma vzdy vysku 900; sirka podle pomeru stran
        PreviewCanvas.Width = Math.Round(PreviewHeight * aspect.Ratio);
        AspectButton.Content = aspect.Name;
        RefreshTexts();
        if (_mode != PreviewMode.Library && TimelineEditor.SelectedClip is null)
            ShowProjectDetails();
    }

    // ===== Cela obrazovka =====

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ShowFullscreen();

    private void PreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ShowFullscreen();
    }

    private void ShowFullscreen()
    {
        // VisualBrush zobrazuje zivy obsah nahledu (vcetne videa a textu) zvetseny na celou obrazovku
        var picture = new Rectangle { Fill = new VisualBrush(PreviewCanvas) { Stretch = Stretch.Uniform } };
        var hint = new TextBlock
        {
            Text = "Esc – ukončit   ·   mezerník – přehrát / pozastavit",
            Foreground = Brushes.White,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 40)
        };

        var window = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowState = WindowState.Maximized,
            ShowInTaskbar = false,
            Background = Brushes.Black,
            Cursor = Cursors.None,
            Content = new Grid { Children = { picture, hint } }
        };

        window.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F)
                window.Close();
            else if (e.Key == Key.Space)
                TogglePlayback();
            e.Handled = true;
        };
        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                window.Close();
        };
        window.MouseMove += (_, _) => window.Cursor = null;

        // Napoveda po chvili zmizi
        hint.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1))
        {
            BeginTime = TimeSpan.FromSeconds(2.5)
        });

        window.ShowDialog();
    }

    // ===== Filtry knihovny =====

    private void LibraryFilter_Checked(object sender, RoutedEventArgs e)
    {
        _libraryFilter = (string)((FrameworkElement)sender).Tag;
        ApplyLibraryFilter();
    }

    private void ApplyLibraryFilter()
    {
        if (MediaList is null)
            return; // jeste se nacita okno

        var view = CollectionViewSource.GetDefaultView(_mediaItems);
        view.Filter = _libraryFilter switch
        {
            "Video" => o => ((MediaItem)o).Kind == MediaKind.Video,
            "Image" => o => ((MediaItem)o).Kind == MediaKind.Image,
            "Audio" => o => ((MediaItem)o).Kind == MediaKind.Audio,
            "Favorite" => o => ((MediaItem)o).IsFavorite,
            _ => null
        };

        LibraryFilterEmpty.Visibility = _mediaItems.Count > 0 && view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not MediaItem item)
            return;

        item.IsFavorite = !item.IsFavorite;
        ApplyLibraryFilter();
        MarkDirty();
    }
}
