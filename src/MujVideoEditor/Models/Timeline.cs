using System.Collections.ObjectModel;

namespace MujVideoEditor.Models;

public enum TrackKind
{
    Video,
    Audio
}

/// <summary>Ulozeny stav casove osy pro Zpet/Znovu.</summary>
public record TimelineState(List<TimelineClip> VideoClips, List<TimelineClip> AudioClips);

/// <summary>
/// Casova osa se dvema "magnetickymi" stopami: klipy jdou za sebou bez mezer (jako hlavni stopa v CapCutu).
/// Videa a obrazky patri do stopy Video, hudba do stopy Zvuk.
/// </summary>
public class Timeline
{
    public ObservableCollection<TimelineClip> VideoClips { get; } = [];
    public ObservableCollection<TimelineClip> AudioClips { get; } = [];

    public IEnumerable<TimelineClip> AllClips => VideoClips.Concat(AudioClips);
    public bool IsEmpty => VideoClips.Count == 0 && AudioClips.Count == 0;

    public TimeSpan Duration => TimeSpan.FromTicks(Math.Max(TrackDuration(VideoClips).Ticks, TrackDuration(AudioClips).Ticks));

    public ObservableCollection<TimelineClip> Track(TrackKind kind) => kind == TrackKind.Video ? VideoClips : AudioClips;

    public static TrackKind TrackFor(MediaItem media) => media.Kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Video;

    public ObservableCollection<TimelineClip> TrackOf(TimelineClip clip) => Track(TrackFor(clip.Media));

    /// <summary>Prepocita zacatky klipu a jejich sirku v pixelech.</summary>
    public void Relayout(double pixelsPerSecond)
    {
        foreach (var track in new[] { VideoClips, AudioClips })
        {
            var start = TimeSpan.Zero;
            foreach (var clip in track)
            {
                clip.Start = start;
                clip.Width = clip.Duration.TotalSeconds * pixelsPerSecond;
                start += clip.Duration;
            }
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

    public bool CanSplit(TimelineClip clip, TimeSpan time) =>
        time - clip.Start >= TimelineClip.MinDuration && clip.End - time >= TimelineClip.MinDuration;

    /// <summary>Rozdeli klip v case <paramref name="time"/> a vrati jeho druhou cast.</summary>
    public TimelineClip Split(TimelineClip clip, TimeSpan time)
    {
        var cutPoint = clip.SourceIn + (time - clip.Start);
        var second = new TimelineClip(clip.Media, cutPoint, clip.SourceOut);
        clip.SourceOut = cutPoint;

        var track = TrackOf(clip);
        track.Insert(track.IndexOf(clip) + 1, second);
        return second;
    }

    public void Remove(TimelineClip clip) => TrackOf(clip).Remove(clip);

    public TimelineState Snapshot() =>
        new(VideoClips.Select(c => c.Clone()).ToList(), AudioClips.Select(c => c.Clone()).ToList());

    public void Restore(TimelineState state)
    {
        VideoClips.Clear();
        AudioClips.Clear();
        // Klonujeme, aby slo stejny stav obnovit i vickrat (Zpet/Znovu)
        foreach (var clip in state.VideoClips) VideoClips.Add(clip.Clone());
        foreach (var clip in state.AudioClips) AudioClips.Add(clip.Clone());
    }

    private static TimeSpan TrackDuration(IEnumerable<TimelineClip> clips) =>
        clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);
}
