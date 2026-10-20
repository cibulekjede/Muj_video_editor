using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MujVideoEditor.Models;

/// <summary>
/// Klip na casove ose: usek zdrojoveho media od SourceIn do SourceOut.
/// </summary>
public class TimelineClip : INotifyPropertyChanged
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(0.1);
    public static readonly TimeSpan DefaultImageDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxImageDuration = TimeSpan.FromHours(1);

    private TimeSpan _sourceIn;
    private TimeSpan _sourceOut;
    private double _width;
    private bool _isSelected;

    public TimelineClip(MediaItem media, TimeSpan sourceIn, TimeSpan sourceOut)
    {
        Media = media;
        _sourceIn = sourceIn;
        _sourceOut = sourceOut;
    }

    public MediaItem Media { get; }

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

    public TimeSpan Duration => SourceOut - SourceIn;
    public string DurationText => Duration.ToString(@"mm\:ss\.f");

    /// <summary>Nejdal, kam lze klip prodlouzit (obrazek lze natahnout libovolne).</summary>
    public TimeSpan MaxSourceOut =>
        Media.Kind == MediaKind.Image ? MaxImageDuration : Media.Duration ?? SourceOut;

    /// <summary>Zacatek klipu na casove ose; nastavuje Timeline.Relayout.</summary>
    public TimeSpan Start { get; set; }
    public TimeSpan End => Start + Duration;

    /// <summary>Sirka klipu v pixelech pro aktualni priblizeni.</summary>
    public double Width
    {
        get => _width;
        set { _width = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public TimelineClip Clone() => new(Media, SourceIn, SourceOut);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnDurationChanged()
    {
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
