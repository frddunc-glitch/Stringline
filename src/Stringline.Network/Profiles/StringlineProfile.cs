using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Network.Profiles;

/// <summary>
/// Per-sim settings, saved as JSON keyed by <see cref="AreaId"/> (<c>02-…md</c> §5): corridors, location display
/// names and category styles. Created from a WTT on first use, then edited by the user.
/// </summary>
public sealed record StringlineProfile
{
    /// <summary>
    /// The format version this build writes. Older versions are read; newer ones are refused. Version 2 (S27) added
    /// each section's headway source; see <see cref="ProfileJson"/> for how a version 1 profile is read.
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>The sim's <c>area_id</c>, e.g. <c>waterloo</c>.</summary>
    public required string AreaId { get; init; }

    /// <summary>
    /// The sim's name for display, set by the user. Null (the default) shows the area ID tidied (<see cref="SimTitle"/>).
    /// Profiles made before S22 have the timetable's title here, which <see cref="SimTitle"/> takes as no name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>Corridors in tab order.</summary>
    public IReadOnlyList<Corridor> Corridors { get; init; } = [];

    /// <summary>Display names by TIPLOC. The WTT has none; a location without one shows its TIPLOC.</summary>
    public IReadOnlyDictionary<Tiploc, string> LocationNames { get; init; } = ImmutableDictionary<Tiploc, string>.Empty;

    public IReadOnlyList<CategoryStyle> Categories { get; init; } = [];

    /// <summary>
    /// The headway for this sim's sections that have neither a hand-set nor a WTT headway; null (the default) uses
    /// <see cref="GlobalHeadwaySeconds"/>.
    /// </summary>
    public int? DefaultHeadwaySeconds { get; init; }

    /// <summary>
    /// The global default headway from Tools ▸ Options, which the app sets after loading (<see cref="WithGlobalHeadway"/>).
    /// Not saved with the profile.
    /// </summary>
    [JsonIgnore]
    public int GlobalHeadwaySeconds { get; init; } = Section.StandardHeadwaySeconds;

    /// <summary>The headway a section falls back to: the profile's default, else the global one.</summary>
    [JsonIgnore]
    public int FallbackHeadwaySeconds => DefaultHeadwaySeconds ?? GlobalHeadwaySeconds;

    public string DisplayName(Tiploc location) =>
        LocationNames.TryGetValue(location, out var name) && !string.IsNullOrWhiteSpace(name) ? name : location.Code;

    public Corridor? FindCorridor(string id) => Corridors.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The style for a category, or <see cref="CategoryStyle.Fallback"/>.</summary>
    public CategoryStyle StyleFor(string? categoryId) =>
        (categoryId is null ? null : Categories.FirstOrDefault(c => c.CategoryId == categoryId)) ?? CategoryStyle.Fallback;

    /// <summary>A new profile for a timetable: the generated corridor candidates and default category styles.</summary>
    public static StringlineProfile Create(WorkingTimetable timetable, CorridorGeneratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        return Create(timetable, CorridorGenerator.Generate(timetable, options).Select(c => c.Corridor));
    }

    /// <summary>A new profile for a timetable with these corridors and default category styles.</summary>
    public static StringlineProfile Create(WorkingTimetable timetable, IEnumerable<Corridor> corridors)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        ArgumentNullException.ThrowIfNull(corridors);
        return new StringlineProfile
        {
            AreaId = timetable.AreaId,
            Corridors = corridors.ToList(),
            Categories = CategoryStyle.CreateDefaults(timetable),
        };
    }

    /// <summary>
    /// The profile brought up to date with a (possibly re-imported) timetable: every corridor's derived values are
    /// recomputed, headways not set by hand are worked out again, and categories new to the timetable get default
    /// styles. User settings are kept.
    /// </summary>
    public StringlineProfile Refresh(WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        var known = Categories.Select(c => c.CategoryId).ToHashSet(StringComparer.Ordinal);
        return this with
        {
            Corridors = Corridors.Select(c => CorridorBuilder.Refresh(c, timetable, FallbackHeadwaySeconds)).ToList(),
            Categories = [.. Categories, .. CategoryStyle.CreateDefaults(timetable).Where(c => !known.Contains(c.CategoryId))],
        };
    }

    /// <summary>The profile with this global default headway, and every headway not set by hand worked out again.</summary>
    public StringlineProfile WithGlobalHeadway(int seconds) => (this with { GlobalHeadwaySeconds = Math.Max(0, seconds) }).ResolveHeadways();

    /// <summary>Every section's headway not set by hand worked out again (<see cref="Section.ResolveHeadway"/>) against <see cref="FallbackHeadwaySeconds"/>.</summary>
    public StringlineProfile ResolveHeadways()
    {
        var fallback = FallbackHeadwaySeconds;
        return this with { Corridors = Corridors.Select(c => CorridorBuilder.ResolveHeadways(c, fallback)).ToList() };
    }
}
