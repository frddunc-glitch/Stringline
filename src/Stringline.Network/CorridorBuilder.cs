using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Network;

/// <summary>Builds corridors from a location order and refreshes their derived values from a WTT.</summary>
public static class CorridorBuilder
{
    /// <summary>
    /// A new corridor through these locations (in Down order) with WTT-derived values: platforms, timing-point-only
    /// flags, section minimum run times and headways, and the derived axis. Every section is double line until the user
    /// says otherwise, and its headway is the WTT's where that is less than <paramref name="fallbackHeadwaySeconds"/>
    /// (<see cref="Section.ResolveHeadway"/>).
    /// </summary>
    public static Corridor Create(string id, string name, IReadOnlyList<Tiploc> order, WorkingTimetable timetable, int fallbackHeadwaySeconds = Section.StandardHeadwaySeconds) =>
        CorridorDrawAt.Apply(Create(id, name, order, CorridorMeasurement.Measure(timetable, order), fallbackHeadwaySeconds), timetable);

    public static Corridor Create(string id, string name, IReadOnlyList<Tiploc> order, CorridorMeasurement measurement, int fallbackHeadwaySeconds = Section.StandardHeadwaySeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(measurement);
        var locations = order.Select((t, i) => new CorridorLocation(
            t,
            0,
            Platforms: measurement.Locations[i].Platforms.Count,
            IsTimingPointOnly: measurement.Locations[i].IsTimingPointOnly)).ToList();
        var sections = measurement.Sections.Select((s, i) => Measured(
            new Section(order[i], order[i + 1], Section.DoubleLine, fallbackHeadwaySeconds, Section.DefaultMinRunSeconds), s, fallbackHeadwaySeconds)).ToList();
        return CorridorAxis.Apply(new Corridor(id, name, locations, sections));
    }

    /// <summary>
    /// Recomputes what comes from the WTT (minimum run times, samples, booked headways, timing-point-only flags, axis
    /// positions) and keeps what the user sets (order, distances, tracks, hand-set headways, axis mode). A location's
    /// platform count is kept if set, else taken from the WTT. A section keeps its settings if its two locations are
    /// still adjacent (in either order); a new pair of neighbours gets a default double-line section. Headways not set
    /// by hand are worked out again against <paramref name="fallbackHeadwaySeconds"/>. The locations drawn at the
    /// corridor's (<see cref="Corridor.DrawAt"/>) are found again, keeping those set by hand.
    /// </summary>
    public static Corridor Refresh(Corridor corridor, WorkingTimetable timetable, int fallbackHeadwaySeconds = Section.StandardHeadwaySeconds)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        var order = corridor.Locations.Select(l => l.Tiploc).ToList();
        var measurement = CorridorMeasurement.Measure(timetable, order);
        var locations = corridor.Locations.Select((l, i) => l with
        {
            Platforms = l.Platforms > 0 ? l.Platforms : measurement.Locations[i].Platforms.Count,
            IsTimingPointOnly = measurement.Locations[i].IsTimingPointOnly,
        }).ToList();
        var sections = measurement.Sections.Select((s, i) =>
        {
            var (a, b) = (order[i], order[i + 1]);
            var existing = corridor.Sections.FirstOrDefault(x => (x.From == a && x.To == b) || (x.From == b && x.To == a))
                ?? new Section(a, b, Section.DoubleLine, fallbackHeadwaySeconds, Section.DefaultMinRunSeconds);
            return Measured(existing with { From = a, To = b }, s, fallbackHeadwaySeconds);
        }).ToList();
        return CorridorDrawAt.Apply(CorridorAxis.Apply(corridor with { Locations = locations, Sections = sections }), timetable);
    }

    /// <summary>The corridor with every headway not set by hand worked out again (<see cref="Section.ResolveHeadway"/>).</summary>
    public static Corridor ResolveHeadways(Corridor corridor, int fallbackHeadwaySeconds)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        return corridor with { Sections = corridor.Sections.Select(s => s.ResolveHeadway(fallbackHeadwaySeconds)).ToList() };
    }

    private static Section Measured(Section section, SectionStats stats, int fallbackHeadwaySeconds) => (section with
    {
        MinRunSeconds = stats.MinRunSeconds ?? Section.DefaultMinRunSeconds,
        MinRunDownSeconds = stats.MinRunDownSeconds,
        MinRunUpSeconds = stats.MinRunUpSeconds,
        Samples = stats.Samples,
        BookedHeadwaySeconds = stats.BookedHeadway.Seconds,
        BookedHeadwayPairs = stats.BookedHeadway.Pairs,
    }).ResolveHeadway(fallbackHeadwaySeconds);
}
