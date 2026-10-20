using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MujVideoEditor.Models;

public enum MediaKind
{
    Video,
    Image,
    Audio,
    Text
}

/// <summary>
/// Jeden soubor importovany do knihovny medii.
/// </summary>
public class MediaItem : INotifyPropertyChanged
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".wmv", ".avi", ".mkv"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];
    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac"];

    public static string DialogFilter =>
        "Všechna média|" + Patterns(VideoExtensions.Concat(ImageExtensions).Concat(AudioExtensions)) +
        "|Videa|" + Patterns(VideoExtensions) +
        "|Obrázky|" + Patterns(ImageExtensions) +
        "|Zvuk|" + Patterns(AudioExtensions);

    /// <summary>Spolecne "medium" vsech textovych klipu (text sam je v TimelineClip.Overlay).</summary>
    public static readonly MediaItem TextPlaceholder = new("Text", MediaKind.Text);

    private ImageSource? _thumbnail;
    private TimeSpan? _duration;

    public MediaItem(string filePath, MediaKind kind)
    {
        FilePath = filePath;
        Kind = kind;
    }

    public string FilePath { get; }
    public MediaKind Kind { get; }
    public string Name => Path.GetFileName(FilePath);

    public string KindName => Kind switch
    {
        MediaKind.Video => "Video",
        MediaKind.Image => "Obrázek",
        MediaKind.Text => "Text",
        _ => "Zvuk"
    };

    /// <summary>Ikona zobrazena, dokud neni nacteny nahledovy obrazek.</summary>
    public string KindGlyph => Kind switch
    {
        MediaKind.Video => "",
        MediaKind.Image => "",
        _ => ""
    };

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public TimeSpan? Duration
    {
        get => _duration;
        set { _duration = value; OnPropertyChanged(); OnPropertyChanged(nameof(DurationText)); }
    }

    public string DurationText => Duration is { } d ? FormatDuration(d) : "";

    /// <summary>Podle pripony urci typ media, nebo vrati null, kdyz soubor neni podporovany.</summary>
    public static MediaKind? DetectKind(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        if (ImageExtensions.Contains(ext)) return MediaKind.Image;
        if (AudioExtensions.Contains(ext)) return MediaKind.Audio;
        return null;
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"mm\:ss");

    private static string Patterns(IEnumerable<string> extensions) =>
        string.Join(";", extensions.Select(e => "*" + e));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
