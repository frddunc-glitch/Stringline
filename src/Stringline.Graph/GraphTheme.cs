using Stringline.Network.Profiles;

namespace Stringline.Graph;

/// <summary>How one layer's lines are drawn (<c>05-…md</c> §2, line styles).</summary>
/// <param name="Opacity">Multiplies the category colour's alpha.</param>
/// <param name="Dashed">Dashed rather than solid.</param>
public sealed record LayerStyle(float Width, double Opacity, bool Dashed = false);

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

    public Colour Text { get; init; } = new(0x21, 0x21, 0x21);

    public Colour MutedText { get; init; } = new(0x75, 0x75, 0x75);

    public Colour Border { get; init; } = new(0x9E, 0x9E, 0x9E);

    public Colour NowLine { get; init; } = new(0xD3, 0x2F, 0x2F);

    public LayerStyle Wtt { get; init; } = new(1f, 0.3);

    public LayerStyle Plan { get; init; } = new(1.5f, 1);

    public LayerStyle Actual { get; init; } = new(2.5f, 1);

    public LayerStyle Forecast { get; init; } = new(1.5f, 1, Dashed: true);

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
