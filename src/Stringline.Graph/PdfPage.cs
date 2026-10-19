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

    /// <summary>A page the size of the view, one point per pixel, with no margin.</summary>
    public static PdfPage OfView(GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        return new("view size", viewport.Width, viewport.Height, 0);
    }

    /// <summary>A page by name: <c>a3</c>, <c>a4</c> or <c>view</c> (any case); null for anything else.</summary>
    public static PdfPage? Parse(string? name, GraphViewport viewport) => name?.Trim().ToUpperInvariant() switch
    {
        "A3" or "A3L" or "A3-LANDSCAPE" => A3Landscape,
        "A4" or "A4L" or "A4-LANDSCAPE" => A4Landscape,
        "VIEW" => OfView(viewport),
        _ => null,
    };

    /// <summary>The viewport the graph is drawn through on this page: the same world range, the size of the area inside the margins.</summary>
    public GraphViewport Fit(GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var width = Math.Max(1, (int)Math.Floor(Width - (2 * Margin)));
        var height = Math.Max(1, (int)Math.Floor(Height - (2 * Margin)));
        return viewport.Resize(width, height);
    }
}
