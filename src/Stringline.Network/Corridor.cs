using System.Text.Json.Serialization;
using Stringline.Core;

namespace Stringline.Network;

/// <summary>How a corridor's locations are spaced on the distance axis.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AxisMode>))]
public enum AxisMode
{
    /// <summary>Spaced by the minimum running time of each section, so fast trains draw as near-straight lines.</summary>
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

/// <param name="AxisPosition">Position on the distance axis, 0 at the first location and 1 at the last.</param>
/// <param name="DistanceKm">Configured distance from a fixed origin, if the user entered one.</param>
/// <param name="Platforms">Platforms seen in the WTT here, or as set by the user; 0 if none are named.</param>
/// <param name="IsTimingPointOnly">True if no train stops here (every booked call is a pass).</param>
public sealed record CorridorLocation(
    Tiploc Tiploc,
    double AxisPosition,
    double? DistanceKm = null,
    int Platforms = 0,
    bool IsTimingPointOnly = false);

/// <summary>The line between two adjacent corridor locations, <see cref="From"/> before <see cref="To"/> in corridor order.</summary>
/// <param name="MinRunSeconds">
/// Fastest booked running time over the section in either direction, less allowances (derived from the WTT); spaces
/// the derived axis. <see cref="Section.DefaultMinRunSeconds"/> if no train is timed at both ends.
/// </param>
/// <param name="MinRunDownSeconds">Fastest booked time from <see cref="From"/> to <see cref="To"/>, if any train is timed that way.</param>
/// <param name="MinRunUpSeconds">Fastest booked time from <see cref="To"/> to <see cref="From"/>, if any train is timed that way.</param>
/// <param name="Samples">How many timed runs over the section the minimums come from.</param>
public sealed record Section(
    Tiploc From,
    Tiploc To,
    IReadOnlyList<TrackDef> Tracks,
    int DefaultHeadwaySeconds,
    int MinRunSeconds,
    int? MinRunDownSeconds = null,
    int? MinRunUpSeconds = null,
    int Samples = 0)
{
    /// <summary>Headway used when the user has not set one: 3 minutes.</summary>
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

    /// <summary>The minimum run time in this direction, falling back to <see cref="MinRunSeconds"/>.</summary>
    public int MinRunFor(TrackDirection direction) => direction switch
    {
        TrackDirection.Down => MinRunDownSeconds ?? MinRunSeconds,
        TrackDirection.Up => MinRunUpSeconds ?? MinRunSeconds,
        _ => MinRunSeconds,
    };

    public override string ToString() => $"{From}–{To} {MinRunSeconds}s";
}

public sealed record TrackDef(string Name, TrackDirection Direction);
