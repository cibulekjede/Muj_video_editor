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

    private record MediaData(string Path, string? RelativePath);

    private record ClipData(int Media, double In, double Out);

    private record ProjectData(int Version, List<MediaData> Media, List<ClipData> VideoClips, List<ClipData> AudioClips);

    public record LoadedProject(List<MediaItem> Media, TimelineState Timeline, List<string> MissingFiles);

    public static void Save(string projectPath, IEnumerable<MediaItem> library, TimelineState timeline)
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
            new(media.IndexOf(clip.Media), clip.SourceIn.TotalSeconds, clip.SourceOut.TotalSeconds);

        var data = new ProjectData(
            CurrentVersion,
            // Relativni cesta pomuze, kdyz se slozka s projektem i videi presune jinam
            media.Select(m => new MediaData(m.FilePath, Path.GetRelativePath(projectDir, m.FilePath))).ToList(),
            timeline.VideoClips.Select(ToData).ToList(),
            timeline.AudioClips.Select(ToData).ToList());

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
                media.Add(new MediaItem(path, kind));
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
            .Select(c => new TimelineClip(media[c.Media]!, TimeSpan.FromSeconds(c.In), TimeSpan.FromSeconds(c.Out)))
            .ToList();

        return new LoadedProject(
            media.OfType<MediaItem>().ToList(),
            new TimelineState(ToClips(data.VideoClips), ToClips(data.AudioClips)),
            missing);
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
