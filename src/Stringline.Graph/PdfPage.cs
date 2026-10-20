namespace Stringline.Graph;

/// <summary>
/// A PDF page for <see cref="SkiaGraphRenderer.RenderPdf"/> (GR-10, S17), in PostScript points (1/72 inch). The graph
/// is laid out afresh to fill the page inside <see cref="Margin"/>, with the view's time and corridor range, so text
/// keeps its size rather than being scaled with the picture.
/// </summary>
public sealed record PdfPage(string Name, float Width, float Height, float Margin)
{
    /// <summary>Points per millimetre.</summary>
    private const float PerMm = 72f / 25.4f;

    /// <summary>The default for printing (Fred): ISO A3, 420 × 297 mm, landscape, 10 mm margins.</summary>
    public static PdfPage A3Landscape { get; } = new("A3 landscape", 420 * PerMm, 297 * PerMm, 10 * PerMm);

    /// <summary>ISO A4, 297 × 210 mm, landscape, 10 mm margins.</summary>
    public static PdfPage A4Landscape { get; } = new("A4 landscape", 297 * PerMm, 210 * PerMm, 10 * PerMm);

    /// <summary>ISO A3 portrait, 297 × 420 mm, 10 mm margins: a vertical plot's page (S21).</summary>
    public static PdfPage A3Portrait { get; } = new("A3 portrait", 297 * PerMm, 420 * PerMm, 10 * PerMm);

    /// <summary>ISO A4 portrait, 210 × 297 mm, 10 mm margins.</summary>
    public static PdfPage A4Portrait { get; } = new("A4 portrait", 210 * PerMm, 297 * PerMm, 10 * PerMm);

    /// <summary>The paper sizes a plot can use (S21), in the order offered.</summary>
    public static IReadOnlyList<PdfPage> Paper { get; } = [A3Portrait, A4Portrait, A3Landscape, A4Landscape];

    /// <summary>True when the page is taller than it is wide.</summary>
    public bool IsPortrait => Height > Width;

    /// <summary>The width inside the margins, in whole points.</summary>
    public int InnerWidth => Math.Max(1, (int)Math.Floor(Width - (2 * Margin)));

    /// <summary>The height inside the margins, in whole points.</summary>
    public int InnerHeight => Math.Max(1, (int)Math.Floor(Height - (2 * Margin)));

    /// <summary>A page the size of the view, one point per pixel, with no margin.</summary>
    public static PdfPage OfView(GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        return new("view size", viewport.Width, viewport.Height, 0);
    }

    /// <summary>A page by name: <c>a3</c>, <c>a4</c> (landscape), <c>a3p</c>, <c>a4p</c> (portrait) or <c>view</c> (any case); null for anything else.</summary>
    public static PdfPage? Parse(string? name, GraphViewport viewport) => name?.Trim().ToUpperInvariant() switch
    {
        "A3" or "A3L" or "A3-LANDSCAPE" => A3Landscape,
        "A4" or "A4L" or "A4-LANDSCAPE" => A4Landscape,
        "A3P" or "A3-PORTRAIT" => A3Portrait,
        "A4P" or "A4-PORTRAIT" => A4Portrait,
        "VIEW" => OfView(viewport),
        _ => null,
    };

    /// <summary>The viewport the graph is drawn through on this page: the same world range, the size of the area inside the margins.</summary>
    public GraphViewport Fit(GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        return viewport.Resize(InnerWidth, InnerHeight);
    }
}
