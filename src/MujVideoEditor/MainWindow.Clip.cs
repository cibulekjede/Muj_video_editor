using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MujVideoEditor.Models;

namespace MujVideoEditor;

/// <summary>
/// Upravy vybraneho klipu (hlasitost, rychlost, prolinani) a zalozka Prechody.
/// </summary>
public partial class MainWindow
{
    private const double DefaultCutFade = 0.5;

    private TimelineClip? _editedClip;
    private TimelineState? _stateBeforeClipEdit;
    private bool _syncingClipEditor;

    private void ShowClipEditor(TimelineClip? clip)
    {
        _editedClip = clip is { IsText: false } ? clip : null;
        ClipEditorPanel.Visibility = _editedClip is null ? Visibility.Collapsed : Visibility.Visible;
        if (_editedClip is null)
            return;

        // Stav pred upravami, aby sly vratit pres Zpet
        _stateBeforeClipEdit = TimelineEditor.Model.Snapshot();
        SyncClipEditor(_editedClip);
    }

    private void SyncClipEditor(TimelineClip clip)
    {
        _syncingClipEditor = true;
        try
        {
            VolumeSection.Visibility = clip.HasAudio ? Visibility.Visible : Visibility.Collapsed;
            SpeedSection.Visibility = clip.CanChangeSpeed ? Visibility.Visible : Visibility.Collapsed;

            VolumeSlider.Value = clip.Volume * 100;
            VolumeText.Text = clip.Volume == 0 ? "ztlumeno" : $"{clip.Volume * 100:0} %";
            MuteCheck.IsChecked = clip.Volume == 0;

            CheckByTag(SpeedChoices, clip.Speed);
            CheckByTag(FadeInChoices, clip.FadeIn);
            CheckByTag(FadeOutChoices, clip.FadeOut);
        }
        finally
        {
            _syncingClipEditor = false;
        }
    }

    private static void CheckByTag(Panel choices, double value)
    {
        foreach (RadioButton choice in choices.Children)
            choice.IsChecked = Math.Abs(TagValue(choice) - value) < 0.001;
    }

    private static double TagValue(object element) =>
        double.Parse((string)((FrameworkElement)element).Tag, CultureInfo.InvariantCulture);

    private void EditClip(Action<TimelineClip> change)
    {
        if (_syncingClipEditor || _editedClip is not { } clip)
            return;

        // Prvni uprava vybraneho klipu ulozi stav pro Zpet
        if (_stateBeforeClipEdit is not null)
        {
            TimelineEditor.PushUndoState(_stateBeforeClipEdit);
            _stateBeforeClipEdit = null;
        }

        change(clip);
        TimelineEditor.Refresh(); // zmena rychlosti meni delku klipu na ose
        SyncClipEditor(clip);
        ShowClipDetails(clip);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        EditClip(c => c.Volume = Math.Round(e.NewValue) / 100);

    private void MuteCheck_Click(object sender, RoutedEventArgs e) =>
        EditClip(c => c.Volume = MuteCheck.IsChecked == true ? 0 : 1);

    private void Speed_Click(object sender, RoutedEventArgs e) => EditClip(c => c.Speed = TagValue(sender));

    private void FadeIn_Click(object sender, RoutedEventArgs e) => EditClip(c => c.FadeIn = TagValue(sender));

    private void FadeOut_Click(object sender, RoutedEventArgs e) => EditClip(c => c.FadeOut = TagValue(sender));

    // ===== Zalozka Prechody =====

    private void FadeAllCuts_Click(object sender, RoutedEventArgs e)
    {
        var clips = TimelineEditor.Model.VideoClips;
        if (clips.Count < 2)
        {
            MessageBox.Show(this, "Na časové ose musí být aspoň dva klipy ve stopě Video, aby mezi nimi byl střih.",
                "Přechody", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ChangeAllVideoClips(clips =>
        {
            for (int i = 0; i < clips.Count; i++)
            {
                if (i > 0)
                    clips[i].FadeIn = DefaultCutFade;
                if (i < clips.Count - 1)
                    clips[i].FadeOut = DefaultCutFade;
            }
        });
    }

    private void RemoveAllFades_Click(object sender, RoutedEventArgs e) => ChangeAllVideoClips(clips =>
    {
        foreach (var clip in clips)
        {
            clip.FadeIn = 0;
            clip.FadeOut = 0;
        }
    });

    private void ChangeAllVideoClips(Action<IList<TimelineClip>> change)
    {
        TimelineEditor.PushUndoState(TimelineEditor.Model.Snapshot());
        change(TimelineEditor.Model.VideoClips);
        TimelineEditor.Refresh();
        if (_editedClip is not null)
            SyncClipEditor(_editedClip);
    }
}
