using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Network;

/// <summary>Builds corridors from a location order and refreshes their derived values from a WTT.</summary>
public static class CorridorBuilder
{
    /// <summary>
    /// A new corridor through these locations (in Down order) with WTT-derived values: platforms, timing-point-only
    /// flags, section minimum run times and the derived axis. Every section is double line with the standard headway
    /// until the user says otherwise.
    /// </summary>
    public static Corridor Create(string id, string name, IReadOnlyList<Tiploc> order, WorkingTimetable timetable) =>
        Create(id, name, order, CorridorMeasurement.Measure(timetable, order));

    public static Corridor Create(string id, string name, IReadOnlyList<Tiploc> order, CorridorMeasurement measurement)
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
        var sections = measurement.Sections.Select((s, i) => WithRunTimes(
            new Section(order[i], order[i + 1], Section.DoubleLine, Section.StandardHeadwaySeconds, Section.DefaultMinRunSeconds), s)).ToList();
        return CorridorAxis.Apply(new Corridor(id, name, locations, sections));
    }

    /// <summary>
    /// Recomputes what comes from the WTT (minimum run times, samples, timing-point-only flags, axis positions) and
    /// keeps what the user sets (order, distances, tracks, headways, axis mode). A location's platform count is kept
    /// if set, else taken from the WTT. A section keeps its settings if its two locations are still adjacent (in either
    /// order); a new pair of neighbours gets a default double-line section.
    /// </summary>
    public static Corridor Refresh(Corridor corridor, WorkingTimetable timetable)
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
                ?? new Section(a, b, Section.DoubleLine, Section.StandardHeadwaySeconds, Section.DefaultMinRunSeconds);
            return WithRunTimes(existing with { From = a, To = b }, s);
        }).ToList();
        return CorridorAxis.Apply(corridor with { Locations = locations, Sections = sections });
    }

    private static Section WithRunTimes(Section section, SectionStats stats) => section with
    {
        MinRunSeconds = stats.MinRunSeconds ?? Section.DefaultMinRunSeconds,
        MinRunDownSeconds = stats.MinRunDownSeconds,
        MinRunUpSeconds = stats.MinRunUpSeconds,
        Samples = stats.Samples,
    };
}
