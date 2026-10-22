using System.Text.Json.Serialization;
using Stringline.Core;

namespace Stringline.Network;

/// <summary>How a corridor's locations are spaced on the distance axis.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AxisMode>))]
public enum AxisMode
{
    /// <summary>
    /// Spaced by the running time of each section (learned from play where known, else the WTT's minimum), so fast
    /// trains draw as near-straight lines.
    /// </summary>
    Derived,

    /// <summary>Spaced by the configured <see cref="CorridorLocation.DistanceKm"/>; missing distances are interpolated by running time.</summary>
    Distance,
}

/// <summary>The direction of a track or a train relative to a corridor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TrackDirection>))]
public enum TrackDirection
{
    /// <summary>Towards the first location (descending corridor order).</summary>
    Up,

    /// <summary>Away from the first location (ascending corridor order).</summary>
    Down,

    BiDirectional,
}

/// <summary>
/// An ordered line of timing locations drawn as one train graph. Locations are in Down order: the first is the Up
/// end, and a Down train runs from it towards the last. <see cref="Sections"/> has one entry per adjacent pair, in
/// the same order.
/// </summary>
/// <param name="Id">Stable key within the profile, e.g. <c>main</c>.</param>
public sealed record Corridor(
    string Id,
    string Name,
    IReadOnlyList<CorridorLocation> Locations,
    IReadOnlyList<Section> Sections,
    AxisMode Axis = AxisMode.Derived)
{
    /// <summary>The index of this location in <see cref="Locations"/>, or -1.</summary>
    public int IndexOf(Tiploc location)
    {
        for (var i = 0; i < Locations.Count; i++)
        {
            if (Locations[i].Tiploc == location)
            {
                return i;
            }
        }

        return -1;
    }

    public bool Contains(Tiploc location) => IndexOf(location) >= 0;

    /// <summary>
    /// Locations off the corridor that the graph draws at a corridor location (#22, #23): a loop or yard within a
    /// station (Harden's <c>HARDN10</c> at <c>HARDEN</c>), or an entry point beyond the corridor's end (<c>EJUNEE</c>
    /// at <c>JUNEE</c>). Found from the WTT (<see cref="CorridorDrawAt"/>) or set by hand. Drawing only: sections,
    /// the forecast and conflicts still see the location itself.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<DrawnAt> DrawAt
    {
        get => _drawAt ?? [];
        init => _drawAt = value is { Count: > 0 } ? value : null;
    }

    /// <summary><see cref="DrawAt"/> as saved: left out of the profile when there are none.</summary>
    [JsonInclude]
    [JsonPropertyName("drawAt")]
    private IReadOnlyList<DrawnAt>? DrawAtJson
    {
        get => _drawAt;
        init => _drawAt = value is { Count: > 0 } ? value : null;
    }

    private readonly IReadOnlyList<DrawnAt>? _drawAt;

    /// <summary>
    /// The index of the corridor location this location draws at: its own, else the one <see cref="DrawAt"/> gives, or -1.
    /// </summary>
    public int DrawIndexOf(Tiploc location)
    {
        var index = IndexOf(location);
        if (index >= 0)
        {
            return index;
        }

        foreach (var drawn in DrawAt)
        {
            if (drawn.Location == location)
            {
                return drawn.At is { } at ? IndexOf(at) : -1;
            }
        }

        return -1;
    }

    /// <summary>The axis position (0..1) of this location, or null if it is not on the corridor.</summary>
    public double? PositionOf(Tiploc location) => IndexOf(location) is var i and >= 0 ? Locations[i].AxisPosition : null;

    /// <summary>The section between these two locations in either order, if they are adjacent on the corridor.</summary>
    public Section? FindSection(Tiploc a, Tiploc b)
    {
        int i = IndexOf(a), j = IndexOf(b);
        return i >= 0 && j >= 0 && Math.Abs(i - j) == 1 ? Sections[Math.Min(i, j)] : null;
    }

    /// <summary>
    /// The direction of a run through these locations (in running order): <see cref="TrackDirection.Down"/> if its
    /// corridor locations mostly ascend, <see cref="TrackDirection.Up"/> if they mostly descend, null if it touches
    /// fewer than two corridor locations or is evenly split (for example a shunt out and back).
    /// </summary>
    public TrackDirection? DirectionOf(IEnumerable<Tiploc> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var balance = 0;
        var previous = -1;
        foreach (var location in locations)
        {
            var i = IndexOf(location);
            if (i < 0 || i == previous)
            {
                continue;
            }

            if (previous >= 0)
            {
                balance += i > previous ? 1 : -1;
            }

            previous = i;
        }

        return balance switch
        {
            > 0 => TrackDirection.Down,
            < 0 => TrackDirection.Up,
            _ => null,
        };
    }

    public override string ToString() => $"{Id} ({Name}, {Locations.Count} locations)";
}

/// <summary>An off-corridor location drawn at a corridor location (<see cref="Corridor.DrawAt"/>).</summary>
/// <param name="Location">The location as the WTT or the sim names it.</param>
/// <param name="At">The corridor location it draws at; null if it is not drawn (set by hand to turn an automatic one off).</param>
/// <param name="HandSet">Set in the profile editor: kept when the corridor is refreshed. Automatic ones are found again.</param>
public sealed record DrawnAt(
    Tiploc Location,
    Tiploc? At = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool HandSet = false);

/// <param name="AxisPosition">Position on the distance axis, 0 at the first location and 1 at the last.</param>
/// <param name="DistanceKm">Configured distance from a fixed origin, if the user entered one.</param>
/// <param name="Platforms">Platforms seen in the WTT here, or as set by the user; 0 if none are named.</param>
/// <param name="IsTimingPointOnly">True if no train stops here (every booked call is a pass).</param>
/// <param name="Minor">
/// A minor row (S36): a location the corridor generator skipped (a timing location few trains are booked at), put on
/// by the user so trains reporting it show their progress there. It is a full corridor location (its sections are
/// measured like any other) but draws faintly, with no grid line and no label unless zoomed in.
/// </param>
public sealed record CorridorLocation(
    Tiploc Tiploc,
    double AxisPosition,
    double? DistanceKm = null,
    int Platforms = 0,
    bool IsTimingPointOnly = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Minor = false);

/// <summary>The line between two adjacent corridor locations, <see cref="From"/> before <see cref="To"/> in corridor order.</summary>
/// <param name="MinRunSeconds">
/// Fastest booked running time over the section in either direction, less allowances (derived from the WTT); spaces
/// the derived axis. <see cref="Section.DefaultMinRunSeconds"/> if no train is timed at both ends.
/// </param>
/// <param name="MinRunDownSeconds">Fastest booked time from <see cref="From"/> to <see cref="To"/>, if any train is timed that way.</param>
/// <param name="MinRunUpSeconds">Fastest booked time from <see cref="To"/> to <see cref="From"/>, if any train is timed that way.</param>
/// <param name="DefaultHeadwaySeconds">
/// The headway used here: set by hand, from the signal blocks, from the WTT, or the default, as <see cref="HeadwaySource"/> says (see
/// <see cref="ResolveHeadway"/>). The name is kept from profiles made before S27.
/// </param>
/// <param name="Samples">How many timed runs over the section the minimums come from.</param>
/// <param name="BookedHeadwaySeconds">The headway the WTT books trains to here (<see cref="BookedHeadway"/>), if it gives one; measured.</param>
/// <param name="BookedHeadwayPairs">How many booked pairs <see cref="BookedHeadwaySeconds"/> comes from; measured.</param>
/// <param name="HeadwaySource">Where <see cref="DefaultHeadwaySeconds"/> comes from.</param>
public sealed record Section(
    Tiploc From,
    Tiploc To,
    IReadOnlyList<TrackDef> Tracks,
    int DefaultHeadwaySeconds,
    int MinRunSeconds,
    int? MinRunDownSeconds = null,
    int? MinRunUpSeconds = null,
    int Samples = 0,
    int? BookedHeadwaySeconds = null,
    int BookedHeadwayPairs = 0,
    HeadwaySource HeadwaySource = HeadwaySource.Default)
{
    /// <summary>The global default headway, unless changed in Tools ▸ Options: 3 minutes.</summary>
    public const int StandardHeadwaySeconds = 180;

    /// <summary>Minimum run time assumed for a section no train is timed over.</summary>
    public const int DefaultMinRunSeconds = 60;

    /// <summary>The usual pair of tracks: one Up, one Down.</summary>
    public static IReadOnlyList<TrackDef> DoubleLine { get; } = [new("Up", TrackDirection.Up), new("Down", TrackDirection.Down)];

    /// <summary>One track used in both directions.</summary>
    public static IReadOnlyList<TrackDef> SingleLine { get; } = [new("Single", TrackDirection.BiDirectional)];

    /// <summary>True if every track here is bi-directional and there is only one: opposing trains cannot pass in the section.</summary>
    [JsonIgnore]
    public bool IsSingleLine => Tracks.Count == 1 && Tracks[0].Direction == TrackDirection.BiDirectional;

    /// <summary>
    /// The signalling's aspects here, set by hand in the profile (3 or 4; S39), or null to go by what play has shown
    /// (<see cref="BlockAspects"/>). A follower needs one block more than the aspects less two clear ahead of it, plus
    /// the overlap, to run on green.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Aspects { get; init; }

    /// <summary>
    /// The headway measured from the learned signal blocks (S39, <c>BlockHeadways</c>), or null if too few signals are
    /// placed here. Used ahead of the WTT's and the default (<see cref="ResolveHeadway"/>); measured.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BlockHeadwaySeconds { get; init; }

    /// <summary>
    /// How long a fast train takes to run through the section as learned from play (<see cref="Profiles.SignallingPlaces.LearnedRunSeconds"/>),
    /// or null if too few runs are known. Spaces the derived axis ahead of <see cref="MinRunSeconds"/>, so trains draw
    /// as they really run where the WTT's times are rounded or missing; measured.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LearnedRunSeconds { get; init; }

    /// <summary>The run time that spaces the derived axis: <see cref="LearnedRunSeconds"/>, else <see cref="MinRunSeconds"/>.</summary>
    [JsonIgnore]
    public int AxisRunSeconds => LearnedRunSeconds ?? MinRunSeconds;

    /// <summary>How many placed signals <see cref="BlockHeadwaySeconds"/> was measured at; measured.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int BlockSignals { get; init; }

    /// <summary>The aspects <see cref="BlockHeadwaySeconds"/> was measured with: <see cref="Aspects"/>, else learned; measured.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BlockAspects { get; init; }

    /// <summary>The minimum run time in this direction, falling back to <see cref="MinRunSeconds"/>.</summary>
    public int MinRunFor(TrackDirection direction) => direction switch
    {
        TrackDirection.Down => MinRunDownSeconds ?? MinRunSeconds,
        TrackDirection.Up => MinRunUpSeconds ?? MinRunSeconds,
        _ => MinRunSeconds,
    };

    /// <summary>
    /// The section with its headway worked out (<c>06-…md</c> §3), in order of preference: the one set by hand; the
    /// signal blocks' (S39), above or below the default; the WTT's, if it is less than <paramref name="fallbackSeconds"/>;
    /// else <paramref name="fallbackSeconds"/> (the profile's default headway, or the global one).
    /// </summary>
    public Section ResolveHeadway(int fallbackSeconds) =>
        HeadwaySource == HeadwaySource.Hand ? this
        : BlockHeadwaySeconds is { } blocks ? this with { DefaultHeadwaySeconds = blocks, HeadwaySource = HeadwaySource.Blocks }
        : BookedHeadwaySeconds is { } booked && booked < fallbackSeconds ? this with { DefaultHeadwaySeconds = booked, HeadwaySource = HeadwaySource.Wtt }
        : this with { DefaultHeadwaySeconds = fallbackSeconds, HeadwaySource = HeadwaySource.Default };

    public override string ToString() => $"{From}–{To} {MinRunSeconds}s";
}

public sealed record TrackDef(string Name, TrackDirection Direction);
