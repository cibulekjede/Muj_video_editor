using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MujVideoEditor.Models;

/// <summary>
/// Vzhled textu ve videu. Velikost pisma je v pixelech pro video, jehoz kratsi strana ma 1080 px,
/// pozice X/Y je stred textu v pomeru k sirce/vysce obrazu (0 az 1).
/// </summary>
public class TextOverlay : INotifyPropertyChanged
{
    public static readonly string[] Fonts = ["Segoe UI", "Arial", "Impact", "Georgia", "Comic Sans MS", "Consolas"];
    public static readonly string[] Colors = ["#FFFFFF", "#000000", "#FFDD00", "#FF4D4D", "#00C1CD", "#4CD964", "#B36BFF", "#FF8AD8"];

    private string _text = "Tvůj text";
    private string _fontFamily = "Segoe UI";
    private double _fontSize = 80;
    private string _color = "#FFFFFF";
    private bool _bold = true;
    private bool _hasBackground;
    private bool _hasShadow = true;
    private double _x = 0.5;
    private double _y = 0.5;

    public string Text { get => _text; set => Set(ref _text, value); }
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value); }
    public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }
    public string Color { get => _color; set => Set(ref _color, value); }
    public bool Bold { get => _bold; set => Set(ref _bold, value); }
    public bool HasBackground { get => _hasBackground; set => Set(ref _hasBackground, value); }
    public bool HasShadow { get => _hasShadow; set => Set(ref _hasShadow, value); }
    public double X { get => _x; set => Set(ref _x, Math.Clamp(value, 0, 1)); }
    public double Y { get => _y; set => Set(ref _y, Math.Clamp(value, 0, 1)); }

    public TextOverlay Clone() => (TextOverlay)MemberwiseClone();

    // ===== Predlohy v zalozce Text =====

    public static TextOverlay DefaultText() => new();

    public static TextOverlay Subtitle() => new()
    {
        Text = "Titulek",
        FontSize = 54,
        Bold = false,
        HasBackground = true,
        HasShadow = false,
        Y = 0.88
    };

    public static TextOverlay Headline() => new()
    {
        Text = "NADPIS",
        FontFamily = "Impact",
        FontSize = 150,
        Bold = false,
        Y = 0.45
    };

    public static TextOverlay Yellow() => new()
    {
        Text = "Wow!",
        FontFamily = "Arial",
        FontSize = 110,
        Color = "#FFDD00",
        Y = 0.3
    };

    public static TextOverlay Comic() => new()
    {
        Text = "Haha!",
        FontFamily = "Comic Sans MS",
        FontSize = 90,
        Color = "#FF8AD8"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
