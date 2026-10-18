using SkiaSharp;

namespace Stringline.Graph;

/// <summary>
/// The typefaces the graph draws with: Noto Sans, embedded (SIL Open Font Licence, <c>Fonts/OFL.txt</c>), so text
/// looks the same on every machine and in PNG snapshots whatever fonts are installed.
/// </summary>
public static class GraphFonts
{
    private static readonly Lazy<SKTypeface> RegularFace = new(() => Load("NotoSans-Regular.ttf"));
    private static readonly Lazy<SKTypeface> BoldFace = new(() => Load("NotoSans-Bold.ttf"));

    public static SKTypeface Regular => RegularFace.Value;

    public static SKTypeface Bold => BoldFace.Value;

    /// <summary>A font for labels: anti-aliased, no hinting, so it scales smoothly and renders alike across platforms.</summary>
    public static SKFont Create(SKTypeface typeface, float size) => new(typeface, size)
    {
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.None,
        Subpixel = true,
    };

    private static SKTypeface Load(string file)
    {
        var name = $"Stringline.Graph.Fonts.{file}";
        using var stream = typeof(GraphFonts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded font {name} is missing.");
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data) ?? throw new InvalidOperationException($"Embedded font {name} cannot be read.");
    }
}
