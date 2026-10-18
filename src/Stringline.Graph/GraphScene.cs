using Stringline.Core;
using Stringline.Network;
using Stringline.Network.Profiles;

namespace Stringline.Graph;

/// <summary>The kind of line a path is, in drawing order (later layers draw on top). See <c>05-…md</c> §2.</summary>
public enum GraphLayer
{
    /// <summary>The train as booked in the WTT.</summary>
    Wtt,

    /// <summary>The working plan, where it differs from the WTT (S10).</summary>
    Plan,

    /// <summary>Where the train has actually been.</summary>
    Actual,

    /// <summary>Where the train is expected to go (S11).</summary>
    Forecast,
}

/// <summary>A corner of a train path in world coordinates: sim seconds and corridor axis position (0..1).</summary>
public readonly record struct GraphVertex(double Time, double Axis);

/// <summary>
/// One line on the graph: a train's run along the corridor in one layer. A train that leaves the corridor and comes
/// back has one path per visit. Vertices are in time order; a stop draws as two vertices at the same axis position.
/// </summary>
/// <param name="Key">Unique within the scene, e.g. <c>wtt:1A23#0</c> or <c>act:5102/0#0</c>.</param>
/// <param name="Train">The WTT train this path belongs to; null for an unmatched live working.</param>
/// <param name="Headcode">Label text.</param>
/// <param name="Colour">The category colour, or grey for an unmatched live working.</param>
/// <param name="IsUnmatched">An actual path of a live working not (yet) matched to the WTT.</param>
/// <param name="Direction">Down if the path runs away from the corridor's first location, Up towards it; null if neither.</param>
/// <param name="LiveUid">For an actual path: the sim uid of the live train.</param>
/// <param name="Working">For an actual path: the index of the working in <c>LiveTrain.Workings</c>.</param>
/// <param name="SegmentColours">
/// One colour per segment (vertex <c>i</c> to <c>i + 1</c>) when the line changes colour along its length, as an
/// actual path does when coloured by delay band; null when the whole line is <paramref name="Colour"/>.
/// </param>
/// <remarks>The bounds are worked out once, when the path is made; do not copy a path <c>with</c> new vertices.</remarks>
public sealed record GraphPath(
    string Key,
    GraphLayer Layer,
    TrainId? Train,
    Headcode Headcode,
    Colour Colour,
    IReadOnlyList<GraphVertex> Vertices,
    bool IsUnmatched = false,
    TrackDirection? Direction = null,
    string? LiveUid = null,
    int? Working = null,
    IReadOnlyList<Colour>? SegmentColours = null)
{
    public double TimeMin => Vertices[0].Time;

    public double TimeMax => Vertices[^1].Time;

    public double AxisMin { get; } = Vertices.Count == 0 ? 0 : Vertices.Min(v => v.Axis);

    public double AxisMax { get; } = Vertices.Count == 0 ? 0 : Vertices.Max(v => v.Axis);

    /// <summary>The colour of segment <paramref name="index"/> (from vertex <c>index</c> to the next).</summary>
    public Colour ColourOf(int index) => SegmentColours is { } colours ? colours[index] : Colour;

    /// <summary>True if this path draws exactly as <paramref name="other"/> does: same key, colours and vertices.</summary>
    public bool DrawsLike(GraphPath other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(this, other) || (Key == other.Key && Layer == other.Layer && Colour == other.Colour &&
            Vertices.SequenceEqual(other.Vertices) &&
            (SegmentColours is null ? other.SegmentColours is null : other.SegmentColours is not null && SegmentColours.SequenceEqual(other.SegmentColours)));
    }
}

/// <summary>A location line across the graph.</summary>
/// <param name="IsStation">Trains stop here (it has platforms or booked stops); drawn heavier with a bold label.</param>
public sealed record GraphLocation(Tiploc Tiploc, string Name, double Axis, bool IsStation, int Platforms);

/// <summary>
/// Everything one corridor's graph shows, in world coordinates, independent of the viewport: build it once per data
/// change and render it at any pan and zoom.
/// </summary>
/// <param name="Now">The sim time of the now line, if live. The host may copy the scene <c>with</c> a later now each
/// frame; the renderer keeps its caches as long as <see cref="Paths"/> is the same list.</param>
public sealed record GraphScene(
    string CorridorId,
    string Title,
    IReadOnlyList<GraphLocation> Locations,
    IReadOnlyList<GraphPath> Paths,
    SimTime? Now)
{
    public IEnumerable<GraphPath> InLayer(GraphLayer layer) => Paths.Where(p => p.Layer == layer);
}
