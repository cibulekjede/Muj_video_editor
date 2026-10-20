namespace MujVideoEditor.Models;

/// <summary>Pomer stran videa (napr. 16:9 na sirku, 9:16 na vysku pro TikTok).</summary>
public record AspectRatio(string Name, int Width, int Height)
{
    public static readonly AspectRatio Landscape = new("16:9", 16, 9);

    public static readonly AspectRatio[] All =
    [
        Landscape,
        new("9:16", 9, 16),
        new("1:1", 1, 1),
        new("4:3", 4, 3),
        new("3:4", 3, 4)
    ];

    public double Ratio => (double)Width / Height;

    public static AspectRatio Parse(string? name) => All.FirstOrDefault(a => a.Name == name) ?? Landscape;

    /// <summary>Rozmer vystupu: kratsi strana ma <paramref name="shortSide"/> px (1080p na vysku = 1080x1920).</summary>
    public (int Width, int Height) OutputSize(int shortSide)
    {
        static int Even(double value) => (int)Math.Round(value / 2) * 2;

        return Width >= Height
            ? (Even(shortSide * Ratio), shortSide)
            : (shortSide, Even(shortSide / Ratio));
    }
}
