using Stringline.Network.Profiles;

namespace Stringline.Graph;

/// <summary>How one layer's lines are drawn (<c>05-…md</c> §2, line styles).</summary>
/// <param name="Opacity">Multiplies the category colour's alpha.</param>
/// <param name="DashDot">
/// Dash-dot rather than solid (<see cref="SkiaGraphRenderer.ForecastPattern"/>), a pattern no category line style uses.
/// </param>
public sealed record LayerStyle(float Width, double Opacity, bool DashDot = false);

/// <summary>Colours and line weights for the graph. <see cref="Light"/> is the only theme so far.</summary>
public sealed record GraphTheme
{
    public static GraphTheme Light { get; } = new();

    public Colour Background { get; init; } = new(0xFF, 0xFF, 0xFF);

    public Colour PlotBackground { get; init; } = new(0xFC, 0xFC, 0xFA);

    /// <summary>Shading over the past (left of the now line).</summary>
    public Colour PastShade { get; init; } = new(0x00, 0x00, 0x00, 0x0B);

    public Colour MinorGrid { get; init; } = new(0xE6, 0xE6, 0xE6);

    public Colour MajorGrid { get; init; } = new(0xD2, 0xD2, 0xD2);

    public Colour HourGrid { get; init; } = new(0xA8, 0xA8, 0xA8);

    public Colour LocationLine { get; init; } = new(0xE0, 0xE0, 0xE0);

    public Colour StationLine { get; init; } = new(0xBD, 0xBD, 0xBD);

    /// <summary>A minor row's faint dotted line (S36): fainter than a location line, so it does not read as part of the grid.</summary>
    public Colour MinorLocationLine { get; init; } = new(0xC8, 0xC8, 0xC8, 0x90);

    /// <summary>A minor row is labelled only with at least this many pixels to the rows either side (zoomed in).</summary>
    public float MinorLabelRoom { get; init; } = 28;

    /// <summary>The live head (S36): the short line from a running train's last report to now ends in a dot this wide.</summary>
    public float LiveHeadRadius { get; init; } = 3.5f;

    public Colour Text { get; init; } = new(0x21, 0x21, 0x21);

    public Colour MutedText { get; init; } = new(0x75, 0x75, 0x75);

    public Colour Border { get; init; } = new(0x9E, 0x9E, 0x9E);

    public Colour NowLine { get; init; } = new(0xD3, 0x2F, 0x2F);

    public LayerStyle Wtt { get; init; } = new(1f, 0.3);

    public LayerStyle Plan { get; init; } = new(1.5f, 1);

    public LayerStyle Actual { get; init; } = new(2.5f, 1);

    /// <summary>The forecast: dash-dot (S31), so it is never taken for a dashed or dotted category.</summary>
    public LayerStyle Forecast { get; init; } = new(1.5f, 1, DashDot: true);

    /// <summary>A cancelled train on the plan layer: thin, in <see cref="GraphSceneBuilder.CancelledColour"/>, with crosses at its ends.</summary>
    public LayerStyle Cancelled { get; init; } = new(1f, 1);

    /// <summary>Half the size of a cancelled train's end crosses, in pixels.</summary>
    public float CancelCross { get; init; } = 4;

    /// <summary>The ghost of a drag edit (S12): where the train would run if the edit were made.</summary>
    public LayerStyle Ghost { get; init; } = new(3f, 0.6);

    /// <summary>Line width of the selected train's actual, plan and forecast paths.</summary>
    public float SelectedWidth { get; init; } = 4;

    /// <summary>Line width of the selected train's WTT path.</summary>
    public float SelectedWttWidth { get; init; } = 2;

    /// <summary>The halo around the selected train, this wide on each side.</summary>
    public float HaloWidth { get; init; } = 2;

    public Colour Halo { get; init; } = new(0xFF, 0xFF, 0xFF, 0xE6);

    /// <summary>Other trains' opacity while one is selected.</summary>
    public double DimOpacity { get; init; } = 0.5;

    /// <summary>A block or possession: hatched in this colour (<c>05-…md</c> §2).</summary>
    public Colour Block { get; init; } = new(0xC6, 0x28, 0x28);

    /// <summary>A speed restriction: a translucent band in this colour.</summary>
    public Colour Restriction { get; init; } = new(0xF5, 0x9E, 0x0B);

    /// <summary>The fill of a note's label.</summary>
    public Colour NoteFill { get; init; } = new(0xFF, 0xF6, 0xC2, 0xF0);

    /// <summary>A note's marker and the border of its label.</summary>
    public Colour NoteBorder { get; init; } = new(0x8D, 0x6E, 0x00);

    /// <summary>A conflict's ring and highlight (<c>05-…md</c> §2).</summary>
    public Colour Conflict { get; init; } = new(0xE5, 0x1A, 0x1A);

    /// <summary>The side of a timing point's grip square on the selected train (S26), in pixels.</summary>
    public float GripSize { get; init; } = 8;

    /// <summary>The border of a locked grip, drawn hollow.</summary>
    public Colour GripLocked { get; init; } = new(0x8A, 0x8A, 0x8A);

    /// <summary>A grip held at a limit while dragging, and its label's border.</summary>
    public Colour GripClamp { get; init; } = new(0xC6, 0x28, 0x28);

    /// <summary>The ring round the grip the arrow keys nudge.</summary>
    public Colour GripFocus { get; init; } = new(0x15, 0x65, 0xC0);

    /// <summary>Spacing of a block's hatching, in pixels.</summary>
    public float HatchSpacing { get; init; } = 7;

    public float LocationFontSize { get; init; } = 12;

    public float TiplocFontSize { get; init; } = 9;

    public float TimeFontSize { get; init; } = 11;

    public float HeadcodeFontSize { get; init; } = 10;

    public float TitleFontSize { get; init; } = 13;

    public LayerStyle StyleOf(GraphLayer layer) => layer switch
    {
        GraphLayer.Wtt => Wtt,
        GraphLayer.Plan => Plan,
        GraphLayer.Actual => Actual,
        GraphLayer.Forecast => Forecast,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, null),
    };
}
