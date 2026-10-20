using System.Collections.ObjectModel;

namespace MujVideoEditor.Models;

public enum TrackKind
{
    Video,
    Audio,
    Text
}

/// <summary>Ulozeny stav casove osy pro Zpet/Znovu, export a ukladani.</summary>
public record TimelineState(List<TimelineClip> VideoClips, List<TimelineClip> AudioClips, List<TimelineClip>? TextClips = null)
{
    public List<TimelineClip> Texts => TextClips ?? [];
}

/// <summary>
/// Casova osa se tremi stopami. Stopy Video a Zvuk jsou "magneticke": klipy jdou za sebou bez mezer
/// (jako hlavni stopa v CapCutu). Ve stope Text ma kazdy klip vlastni zacatek.
/// </summary>
public class Timeline
{
    public ObservableCollection<TimelineClip> VideoClips { get; } = [];
    public ObservableCollection<TimelineClip> AudioClips { get; } = [];
    public ObservableCollection<TimelineClip> TextClips { get; } = [];

    public IEnumerable<TimelineClip> AllClips => VideoClips.Concat(AudioClips).Concat(TextClips);
    public bool IsEmpty => VideoClips.Count == 0 && AudioClips.Count == 0 && TextClips.Count == 0;

    public TimeSpan Duration => new[] { TrackDuration(VideoClips), TrackDuration(AudioClips), TextEnd() }.Max();

    public ObservableCollection<TimelineClip> Track(TrackKind kind) => kind switch
    {
        TrackKind.Video => VideoClips,
        TrackKind.Audio => AudioClips,
        _ => TextClips
    };

    public static TrackKind TrackFor(MediaItem media) => media.Kind switch
    {
        MediaKind.Audio => TrackKind.Audio,
        MediaKind.Text => TrackKind.Text,
        _ => TrackKind.Video
    };

    public ObservableCollection<TimelineClip> TrackOf(TimelineClip clip) => Track(TrackFor(clip.Media));

    /// <summary>Prepocita zacatky klipu (krome stopy Text) a jejich pozici a sirku v pixelech.</summary>
    public void Relayout(double pixelsPerSecond)
    {
        foreach (var track in new[] { VideoClips, AudioClips })
        {
            var start = TimeSpan.Zero;
            foreach (var clip in track)
            {
                clip.Start = start;
                start += clip.Duration;
            }
        }

        foreach (var clip in AllClips)
        {
            clip.Left = clip.Start.TotalSeconds * pixelsPerSecond;
            clip.Width = clip.Duration.TotalSeconds * pixelsPerSecond;
        }
    }

    /// <summary>Klip, ktery je ve stope v case <paramref name="time"/>, a jak daleko od jeho zacatku.</summary>
    public (TimelineClip Clip, TimeSpan Offset)? ClipAt(TrackKind kind, TimeSpan time)
    {
        foreach (var clip in Track(kind))
        {
            if (time >= clip.Start && time < clip.End)
                return (clip, time - clip.Start);
        }
        return null;
    }

    /// <summary>Vsechny texty, ktere jsou v case <paramref name="time"/> videt.</summary>
    public List<TimelineClip> TextsAt(TimeSpan time) =>
        TextClips.Where(c => time >= c.Start && time < c.End).ToList();

    /// <summary>Prvni misto od <paramref name="from"/>, kam se vejde text dane delky bez prekryvu.</summary>
    public TimeSpan FreeTextSlot(TimeSpan from, TimeSpan duration)
    {
        var start = from;
        bool moved = true;
        while (moved)
        {
            moved = false;
            foreach (var clip in TextClips)
            {
                if (start < clip.End && start + duration > clip.Start)
                {
                    start = clip.End;
                    moved = true;
                }
            }
        }
        return start;
    }

    public bool CanSplit(TimelineClip clip, TimeSpan time) =>
        time - clip.Start >= TimelineClip.MinDuration && clip.End - time >= TimelineClip.MinDuration;

    /// <summary>Rozdeli klip v case <paramref name="time"/> a vrati jeho druhou cast.</summary>
    public TimelineClip Split(TimelineClip clip, TimeSpan time)
    {
        var cutPoint = clip.SourceIn + clip.ToSource(time - clip.Start);
        var second = new TimelineClip(clip.Media, cutPoint, clip.SourceOut, clip.Overlay?.Clone())
        {
            Start = time,
            Speed = clip.Speed,
            Volume = clip.Volume,
            FadeOut = clip.FadeOut // zatmivani zustane na konci, roztmivani na zacatku prvni casti
        };
        clip.SourceOut = cutPoint;
        clip.FadeOut = 0;

        var track = TrackOf(clip);
        track.Insert(track.IndexOf(clip) + 1, second);
        return second;
    }

    public void Remove(TimelineClip clip) => TrackOf(clip).Remove(clip);

    public TimelineState Snapshot() =>
        new(VideoClips.Select(c => c.Clone()).ToList(),
            AudioClips.Select(c => c.Clone()).ToList(),
            TextClips.Select(c => c.Clone()).ToList());

    public void Restore(TimelineState state)
    {
        VideoClips.Clear();
        AudioClips.Clear();
        TextClips.Clear();
        // Klonujeme, aby slo stejny stav obnovit i vickrat (Zpet/Znovu)
        foreach (var clip in state.VideoClips) VideoClips.Add(clip.Clone());
        foreach (var clip in state.AudioClips) AudioClips.Add(clip.Clone());
        foreach (var clip in state.Texts) TextClips.Add(clip.Clone());
    }

    private static TimeSpan TrackDuration(IEnumerable<TimelineClip> clips) =>
        clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);

    private TimeSpan TextEnd() => TextClips.Count == 0 ? TimeSpan.Zero : TextClips.Max(c => c.End);
}
