using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MujVideoEditor.Models;

/// <summary>
/// Klip na casove ose: usek zdrojoveho media od SourceIn do SourceOut.
/// Textovy klip ma misto media Overlay a jeho SourceIn/SourceOut urcuji jen delku.
/// </summary>
public class TimelineClip : INotifyPropertyChanged
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(0.1);
    public static readonly TimeSpan DefaultImageDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultTextDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxImageDuration = TimeSpan.FromHours(1);

    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4;

    private TimeSpan _sourceIn;
    private TimeSpan _sourceOut;
    private double _speed = 1;
    private double _volume = 1;
    private double _fadeIn;
    private double _fadeOut;
    private double _width;
    private double _left;
    private bool _isSelected;

    public TimelineClip(MediaItem media, TimeSpan sourceIn, TimeSpan sourceOut, TextOverlay? overlay = null)
    {
        Media = media;
        _sourceIn = sourceIn;
        _sourceOut = sourceOut;
        Overlay = overlay;
        if (overlay is not null)
            overlay.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TextOverlay.Text))
                    OnPropertyChanged(nameof(DisplayName));
            };
    }

    public static TimelineClip CreateText(TextOverlay overlay, TimeSpan start, TimeSpan duration) =>
        new(MediaItem.TextPlaceholder, TimeSpan.Zero, duration, overlay) { Start = start };

    public MediaItem Media { get; }
    public TextOverlay? Overlay { get; }
    public bool IsText => Media.Kind == MediaKind.Text;

    /// <summary>Popisek na casove ose: nazev souboru, u textu prvni radek textu.</summary>
    public string DisplayName => Overlay is { } o ? o.Text.Split('\n')[0].Trim() : Media.Name;

    /// <summary>Kde ve zdrojovem souboru klip zacina.</summary>
    public TimeSpan SourceIn
    {
        get => _sourceIn;
        set { _sourceIn = value; OnDurationChanged(); }
    }

    /// <summary>Kde ve zdrojovem souboru klip konci.</summary>
    public TimeSpan SourceOut
    {
        get => _sourceOut;
        set { _sourceOut = value; OnDurationChanged(); }
    }

    /// <summary>Rychlost prehravani (1 = normalni, 2 = dvakrat rychleji). Zrychleny klip je na ose kratsi.</summary>
    public double Speed
    {
        get => _speed;
        set { _speed = Math.Clamp(value, MinSpeed, MaxSpeed); OnDurationChanged(); OnPropertyChanged(); }
    }

    /// <summary>Hlasitost (1 = 100 %, 0 = ztlumeno, 2 = 200 %).</summary>
    public double Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0, 2); OnPropertyChanged(); }
    }

    /// <summary>Roztmivani na zacatku klipu (z cerne / z ticha) v sekundach, 0 = bez.</summary>
    public double FadeIn
    {
        get => _fadeIn;
        set { _fadeIn = Math.Max(0, value); OnPropertyChanged(); }
    }

    /// <summary>Zatmivani na konci klipu (do cerne / do ticha) v sekundach, 0 = bez.</summary>
    public double FadeOut
    {
        get => _fadeOut;
        set { _fadeOut = Math.Max(0, value); OnPropertyChanged(); }
    }

    public bool HasAudio => Media.Kind is MediaKind.Video or MediaKind.Audio;
    public bool CanChangeSpeed => Media.Kind is MediaKind.Video or MediaKind.Audio;

    /// <summary>Jak moc je klip v danem miste videt/slyset kvuli prolinani (0 az 1).</summary>
    public double FadeFactor(TimeSpan offset)
    {
        double factor = 1;
        double fadeIn = Math.Min(FadeIn, Duration.TotalSeconds / 2);
        double fadeOut = Math.Min(FadeOut, Duration.TotalSeconds / 2);
        if (fadeIn > 0)
            factor = Math.Min(factor, offset.TotalSeconds / fadeIn);
        if (fadeOut > 0)
            factor = Math.Min(factor, (Duration - offset).TotalSeconds / fadeOut);
        return Math.Clamp(factor, 0, 1);
    }

    /// <summary>Delka klipu na casove ose.</summary>
    public TimeSpan Duration => ToTimeline(SourceOut - SourceIn);

    /// <summary>Prevede delku na ose na delku ve zdrojovem souboru (podle rychlosti).</summary>
    public TimeSpan ToSource(TimeSpan timelineSpan) => TimeSpan.FromTicks((long)(timelineSpan.Ticks * Speed));

    public TimeSpan ToTimeline(TimeSpan sourceSpan) => TimeSpan.FromTicks((long)(sourceSpan.Ticks / Speed));
    public string DurationText => Duration.ToString(@"mm\:ss\.f");

    /// <summary>Nejdal, kam lze klip prodlouzit (obrazek a text lze natahnout libovolne).</summary>
    public TimeSpan MaxSourceOut =>
        Media.Kind is MediaKind.Image or MediaKind.Text ? MaxImageDuration : Media.Duration ?? SourceOut;

    /// <summary>Zacatek klipu na casove ose. Ve stope Video/Zvuk ho nastavuje Timeline.Relayout,
    /// textovy klip ma vlastni volnou pozici.</summary>
    public TimeSpan Start { get; set; }
    public TimeSpan End => Start + Duration;

    /// <summary>Sirka klipu v pixelech pro aktualni priblizeni.</summary>
    public double Width
    {
        get => _width;
        set { _width = value; OnPropertyChanged(); }
    }

    /// <summary>Levy okraj klipu v pixelech (pouziva stopa Text).</summary>
    public double Left
    {
        get => _left;
        set { _left = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public TimelineClip Clone() =>
        new(Media, SourceIn, SourceOut, Overlay?.Clone())
        {
            Start = Start,
            Speed = Speed,
            Volume = Volume,
            FadeIn = FadeIn,
            FadeOut = FadeOut
        };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnDurationChanged()
    {
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
