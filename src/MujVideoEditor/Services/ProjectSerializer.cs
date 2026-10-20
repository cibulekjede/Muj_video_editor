using System.IO;
using System.Text.Json;
using MujVideoEditor.Models;

namespace MujVideoEditor.Services;

/// <summary>
/// Uklada a nacita projekt (.mvproj) jako JSON: seznam medii a klipy na casove ose.
/// </summary>
public static class ProjectSerializer
{
    public const string Extension = ".mvproj";
    public const string DialogFilter = "Projekt Můj video editor (*.mvproj)|*.mvproj";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private record MediaData(string Path, string? RelativePath, bool Favorite = false);

    private record ClipData(int Media, double In, double Out,
        double Speed = 1, double Volume = 1, double FadeIn = 0, double FadeOut = 0);

    private record TextData(double Start, double Duration, string Text, string Font, double Size, string Color,
        bool Bold, bool Shadow, bool Background, double X, double Y);

    private record ProjectData(int Version, List<MediaData> Media, List<ClipData> VideoClips, List<ClipData> AudioClips,
        List<TextData>? Texts = null, string? Aspect = null);

    public record LoadedProject(List<MediaItem> Media, TimelineState Timeline, List<string> MissingFiles, AspectRatio Aspect);

    public static void Save(string projectPath, IEnumerable<MediaItem> library, TimelineState timeline, AspectRatio aspect)
    {
        string projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var media = library.ToList();

        // Klip by mel vzdy odkazovat na medium z knihovny; kdyby ne, pridame ho
        foreach (var clip in timeline.VideoClips.Concat(timeline.AudioClips))
        {
            if (!media.Contains(clip.Media))
                media.Add(clip.Media);
        }

        ClipData ToData(TimelineClip clip) =>
            new(media.IndexOf(clip.Media), clip.SourceIn.TotalSeconds, clip.SourceOut.TotalSeconds,
                clip.Speed, clip.Volume, clip.FadeIn, clip.FadeOut);

        var data = new ProjectData(
            CurrentVersion,
            // Relativni cesta pomuze, kdyz se slozka s projektem i videi presune jinam
            media.Select(m => new MediaData(m.FilePath, Path.GetRelativePath(projectDir, m.FilePath), m.IsFavorite)).ToList(),
            timeline.VideoClips.Select(ToData).ToList(),
            timeline.AudioClips.Select(ToData).ToList(),
            timeline.Texts.Where(c => c.Overlay is not null).Select(c => new TextData(
                c.Start.TotalSeconds, c.Duration.TotalSeconds, c.Overlay!.Text, c.Overlay.FontFamily, c.Overlay.FontSize,
                c.Overlay.Color, c.Overlay.Bold, c.Overlay.HasShadow, c.Overlay.HasBackground, c.Overlay.X, c.Overlay.Y)).ToList(),
            aspect.Name);

        // Nejdriv do docasneho souboru, at pri chybe neprijdeme o puvodni projekt
        string tempPath = projectPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(tempPath, projectPath, overwrite: true);
    }

    public static LoadedProject Load(string projectPath)
    {
        string projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var data = JsonSerializer.Deserialize<ProjectData>(File.ReadAllText(projectPath), JsonOptions)
                   ?? throw new InvalidDataException("Soubor projektu je prázdný.");

        if (data.Version > CurrentVersion)
            throw new InvalidDataException("Projekt byl uložen novější verzí editoru.");

        var media = new List<MediaItem?>();
        var missing = new List<string>();

        foreach (var entry in data.Media ?? [])
        {
            string? path = ResolvePath(entry, projectDir);
            if (path is not null && MediaItem.DetectKind(path) is { } kind)
            {
                media.Add(new MediaItem(path, kind) { IsFavorite = entry.Favorite });
            }
            else
            {
                media.Add(null);
                missing.Add(entry.Path);
            }
        }

        List<TimelineClip> ToClips(List<ClipData>? clips) =>
            (clips ?? [])
            .Where(c => c.Media >= 0 && c.Media < media.Count && media[c.Media] is not null && c.Out > c.In)
            .Select(c => new TimelineClip(media[c.Media]!, TimeSpan.FromSeconds(c.In), TimeSpan.FromSeconds(c.Out))
            {
                Speed = c.Speed > 0 ? c.Speed : 1,
                Volume = c.Volume,
                FadeIn = c.FadeIn,
                FadeOut = c.FadeOut
            })
            .ToList();

        var texts = (data.Texts ?? [])
            .Where(t => t.Duration > 0)
            .Select(t => TimelineClip.CreateText(new TextOverlay
            {
                Text = t.Text ?? "",
                FontFamily = t.Font ?? "Segoe UI",
                FontSize = t.Size,
                Color = t.Color ?? "#FFFFFF",
                Bold = t.Bold,
                HasShadow = t.Shadow,
                HasBackground = t.Background,
                X = t.X,
                Y = t.Y
            }, TimeSpan.FromSeconds(t.Start), TimeSpan.FromSeconds(t.Duration)))
            .ToList();

        return new LoadedProject(
            media.OfType<MediaItem>().ToList(),
            new TimelineState(ToClips(data.VideoClips), ToClips(data.AudioClips), texts),
            missing,
            AspectRatio.Parse(data.Aspect));
    }

    private static string? ResolvePath(MediaData entry, string projectDir)
    {
        if (File.Exists(entry.Path))
            return entry.Path;

        if (entry.RelativePath is not null)
        {
            string relative = Path.GetFullPath(Path.Combine(projectDir, entry.RelativePath));
            if (File.Exists(relative))
                return relative;
        }

        return null;
    }
}
