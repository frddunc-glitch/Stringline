using System.Collections.Immutable;
using System.Text;
using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Network.Profiles;

/// <summary>An edit that would leave the profile unusable, or that makes no sense (an unknown corridor, a bad value).</summary>
public sealed class ProfileEditException(string message) : Exception(message);

/// <summary>
/// Immutable edits on a <see cref="StringlineProfile"/> (COR-2, COR-5), for the profile editor. Each returns a new
/// profile that keeps every <see cref="ProfileValidator"/> rule, and also these: a corridor has a name and at least two
/// locations, a headway is not negative, a distance is a number, a category's priority is 0–100 and its name is not
/// empty. Otherwise it throws <see cref="ProfileEditException"/> and the profile is unchanged.
/// <para>
/// Edits that change a corridor's locations take the timetable, so the new sections' minimum run times, the platforms
/// and the axis are measured from it, as <see cref="CorridorBuilder.Refresh"/> does. A section keeps its tracks and
/// headway while its two locations stay neighbours; a new pair of neighbours gets a double line, and its headway is
/// worked out from the WTT and the profile's default (<see cref="Section.ResolveHeadway"/>).
/// </para>
/// </summary>
public static class ProfileEdits
{
    /// <summary>The fewest locations a corridor may have.</summary>
    public const int MinLocations = 2;

    /// <summary>
    /// The sim's display name, typed by the user, so it is kept even when it matches a timetable's title
    /// (<see cref="StringlineProfile.NameByHand"/>); blank clears it.
    /// </summary>
    public static StringlineProfile WithName(this StringlineProfile profile, string? name)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.IsNullOrWhiteSpace(name) ? profile with { Name = null, NameByHand = false } : profile with { Name = name.Trim(), NameByHand = true };
    }

    /// <summary>The profile under another sim ID, for a timetable that has none.</summary>
    public static StringlineProfile WithAreaId(this StringlineProfile profile, string areaId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(areaId))
        {
            throw new ProfileEditException("The sim needs a name.");
        }

        return Checked(profile with { AreaId = areaId.Trim() });
    }

    /// <summary>A location's display name; blank removes it, so the TIPLOC shows.</summary>
    public static StringlineProfile WithLocationName(this StringlineProfile profile, Tiploc location, string? name)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (location.IsEmpty)
        {
            throw new ProfileEditException("No location given.");
        }

        var names = profile.LocationNames.ToImmutableDictionary();
        names = string.IsNullOrWhiteSpace(name) ? names.Remove(location) : names.SetItem(location, name.Trim());
        return profile with { LocationNames = names };
    }

    /// <summary>These display names added, replacing any the profile has for the same TIPLOCs.</summary>
    public static StringlineProfile WithLocationNames(this StringlineProfile profile, IEnumerable<KeyValuePair<Tiploc, string>> names)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(names);
        var merged = profile.LocationNames.ToImmutableDictionary().ToBuilder();
        foreach (var (tiploc, name) in names)
        {
            if (!tiploc.IsEmpty && !string.IsNullOrWhiteSpace(name))
            {
                merged[tiploc] = name.Trim();
            }
        }

        return profile with { LocationNames = merged.ToImmutable() };
    }

    /// <summary>A corridor added (a generated candidate, say) at the end or at <paramref name="index"/>. Its ID must be new.</summary>
    public static StringlineProfile AddCorridor(this StringlineProfile profile, Corridor corridor, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(corridor);
        if (profile.FindCorridor(corridor.Id) is not null)
        {
            throw new ProfileEditException($"There is already a corridor '{corridor.Id}'.");
        }

        var list = profile.Corridors.ToList();
        list.Insert(Math.Clamp(index ?? list.Count, 0, list.Count), CorridorBuilder.ResolveHeadways(corridor, profile.FallbackHeadwaySeconds));
        return Checked(profile with { Corridors = list });
    }

    /// <summary>A new corridor from <paramref name="first"/> (its Up end) to <paramref name="last"/>, with an ID made from its name.</summary>
    public static StringlineProfile AddCorridor(this StringlineProfile profile, string name, Tiploc first, Tiploc last, WorkingTimetable? timetable)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ProfileEditException("The corridor needs a name.");
        }

        if (first.IsEmpty || last.IsEmpty)
        {
            throw new ProfileEditException("The corridor needs a location at each end.");
        }

        if (first == last)
        {
            throw new ProfileEditException("The corridor's ends must be different locations.");
        }

        var id = NewCorridorId(profile, name);
        var corridor = Measured(new Corridor(id, name.Trim(), [new(first, 0), new(last, 1)], []), timetable, profile.FallbackHeadwaySeconds);
        return profile.AddCorridor(corridor);
    }

    /// <summary>An ID for a corridor named <paramref name="name"/> that no corridor in the profile has: <c>Down Slow</c> gives <c>down-slow</c>, then <c>down-slow-2</c>.</summary>
    public static string NewCorridorId(StringlineProfile profile, string name)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var slug = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var stem = slug.ToString().Trim('-') is { Length: > 0 } s ? s : "corridor";
        var id = stem;
        for (var n = 2; profile.FindCorridor(id) is not null; n++)
        {
            id = $"{stem}-{n}";
        }

        return id;
    }

    public static StringlineProfile RemoveCorridor(this StringlineProfile profile, string id)
    {
        var (list, index) = Find(profile, id);
        list.RemoveAt(index);
        return profile with { Corridors = list };
    }

    /// <summary>The corridor's tab name. Its ID stays, so other settings keyed on it keep working.</summary>
    public static StringlineProfile RenameCorridor(this StringlineProfile profile, string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ProfileEditException("The corridor needs a name.");
        }

        return profile.EditCorridor(id, c => c with { Name = name.Trim() });
    }

    /// <summary>The corridor's tab moved to <paramref name="newIndex"/> (clamped to the list).</summary>
    public static StringlineProfile MoveCorridor(this StringlineProfile profile, string id, int newIndex)
    {
        var (list, index) = Find(profile, id);
        var corridor = list[index];
        list.RemoveAt(index);
        list.Insert(Math.Clamp(newIndex, 0, list.Count), corridor);
        return profile with { Corridors = list };
    }

    /// <summary>Any change to one corridor, checked. Its ID must not change.</summary>
    public static StringlineProfile EditCorridor(this StringlineProfile profile, string id, Func<Corridor, Corridor> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var (list, index) = Find(profile, id);
        var edited = edit(list[index]);
        if (!string.Equals(edited.Id, list[index].Id, StringComparison.Ordinal))
        {
            throw new ProfileEditException("A corridor's ID cannot be changed.");
        }

        if (string.IsNullOrWhiteSpace(edited.Name))
        {
            throw new ProfileEditException("The corridor needs a name.");
        }

        list[index] = edited;
        return Checked(profile with { Corridors = list });
    }

    /// <summary>
    /// A location put on the corridor at <paramref name="index"/> (0 makes it the Up end), as a minor row if
    /// <paramref name="minor"/> (S36).
    /// </summary>
    public static StringlineProfile InsertLocation(this StringlineProfile profile, string id, int index, Tiploc location, WorkingTimetable? timetable, bool minor = false)
    {
        if (location.IsEmpty)
        {
            throw new ProfileEditException("No location given.");
        }

        var (list, at) = Find(profile, id);
        var corridor = list[at];
        var where = Math.Clamp(index, 0, corridor.Locations.Count);

        // A location put between two neighbours splits their section: both halves keep its tracks (a single line stays single).
        var split = where > 0 && where < corridor.Locations.Count ? corridor.FindSection(corridor.Locations[where - 1].Tiploc, corridor.Locations[where].Tiploc) : null;
        var edited = profile.EditLocations(id, timetable, locations =>
        {
            if (locations.Any(l => l.Tiploc == location))
            {
                throw new ProfileEditException($"{location} is already on the corridor.");
            }

            locations.Insert(where, new CorridorLocation(location, 0, Minor: minor));
        });
        if (split is null)
        {
            return edited;
        }

        return edited.EditCorridor(id, c => c with
        {
            Sections = c.Sections.Select((s, i) => i == where - 1 || i == where ? s with { Tracks = split.Tracks } : s).ToList(),
        });
    }

    /// <summary>
    /// Marks a location as a minor row (S36), or as an ordinary one: a minor row draws faintly, with no grid line and
    /// no label unless zoomed in. Its sections are unchanged.
    /// </summary>
    public static StringlineProfile WithMinor(this StringlineProfile profile, string id, int index, bool minor) =>
        profile.EditCorridor(id, c =>
        {
            CheckIndex(c.Locations, index);
            return c with { Locations = c.Locations.Select((l, i) => i == index ? l with { Minor = minor } : l).ToList() };
        });

    public static StringlineProfile RemoveLocation(this StringlineProfile profile, string id, int index, WorkingTimetable? timetable) =>
        profile.EditLocations(id, timetable, locations =>
        {
            CheckIndex(locations, index);
            if (locations.Count <= MinLocations)
            {
                throw new ProfileEditException($"A corridor needs at least {MinLocations} locations.");
            }

            locations.RemoveAt(index);
        });

    /// <summary>The location at <paramref name="from"/> moved to <paramref name="to"/>.</summary>
    public static StringlineProfile MoveLocation(this StringlineProfile profile, string id, int from, int to, WorkingTimetable? timetable) =>
        profile.EditLocations(id, timetable, locations =>
        {
            CheckIndex(locations, from);
            var location = locations[from];
            locations.RemoveAt(from);
            locations.Insert(Math.Clamp(to, 0, locations.Count), location);
        });

    /// <summary>A location's distance in km from any fixed origin, or null to space it by run time. Used when the axis is <see cref="AxisMode.Distance"/>.</summary>
    public static StringlineProfile WithDistance(this StringlineProfile profile, string id, int index, double? km)
    {
        if (km is { } d && (double.IsNaN(d) || double.IsInfinity(d)))
        {
            throw new ProfileEditException("The distance must be a number.");
        }

        return profile.EditCorridor(id, c =>
        {
            CheckIndex(c.Locations, index);
            return CorridorAxis.Apply(c with { Locations = c.Locations.Select((l, i) => i == index ? l with { DistanceKm = km } : l).ToList() });
        });
    }

    /// <summary>The number of platforms at a location; 0 when none are named.</summary>
    public static StringlineProfile WithPlatforms(this StringlineProfile profile, string id, int index, int platforms)
    {
        if (platforms < 0)
        {
            throw new ProfileEditException("The number of platforms cannot be negative.");
        }

        return profile.EditCorridor(id, c =>
        {
            CheckIndex(c.Locations, index);
            return c with { Locations = c.Locations.Select((l, i) => i == index ? l with { Platforms = platforms } : l).ToList() };
        });
    }

    /// <summary>Space the corridor by run time or by the entered distances.</summary>
    public static StringlineProfile WithAxis(this StringlineProfile profile, string id, AxisMode axis) =>
        profile.EditCorridor(id, c => CorridorAxis.Apply(c with { Axis = axis }));

    /// <summary>The tracks of section <paramref name="index"/> (between locations <c>index</c> and <c>index + 1</c>).</summary>
    public static StringlineProfile WithTracks(this StringlineProfile profile, string id, int index, IReadOnlyList<TrackDef> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            throw new ProfileEditException("A section needs at least one track.");
        }

        if (tracks.Any(t => string.IsNullOrWhiteSpace(t.Name)))
        {
            throw new ProfileEditException("Every track needs a name.");
        }

        return profile.EditSection(id, index, s => s with { Tracks = tracks.ToList() });
    }

    /// <summary>
    /// The least time between trains following each other through section <paramref name="index"/>, set by hand; null
    /// goes back to the WTT's headway or the default (<see cref="Section.ResolveHeadway"/>).
    /// </summary>
    public static StringlineProfile WithHeadway(this StringlineProfile profile, string id, int index, int? seconds)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (seconds < 0)
        {
            throw new ProfileEditException("The headway cannot be negative.");
        }

        return profile.EditSection(id, index, s => seconds is { } h
            ? s with { DefaultHeadwaySeconds = h, HeadwaySource = HeadwaySource.Hand }
            : (s with { HeadwaySource = HeadwaySource.Default }).ResolveHeadway(profile.FallbackHeadwaySeconds));
    }

    /// <summary>
    /// The signalling's aspects in section <paramref name="index"/> (2–4), set by hand (S39); null goes back to what
    /// play has shown. The block headways are measured again, with places that have <paramref name="minSamples"/> samples.
    /// </summary>
    public static StringlineProfile WithAspects(this StringlineProfile profile, string id, int index, int? aspects, int minSamples = SignallingPlaces.DefaultMinSamples)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (aspects is < 2 or > 4)
        {
            throw new ProfileEditException("Aspects must be 2, 3 or 4.");
        }

        return profile.EditSection(id, index, s => s with { Aspects = aspects }).WithBlockHeadways(minSamples);
    }

    /// <summary>
    /// The profile's default headway, for sections with neither a hand-set nor a WTT headway; null uses the global one
    /// (Tools ▸ Options). Every headway not set by hand is worked out again.
    /// </summary>
    public static StringlineProfile WithDefaultHeadway(this StringlineProfile profile, int? seconds)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (seconds < 0)
        {
            throw new ProfileEditException("The headway cannot be negative.");
        }

        return (profile with { DefaultHeadwaySeconds = seconds }).ResolveHeadways();
    }

    /// <summary>A category's style, replacing the profile's style for the same category or adding it.</summary>
    public static StringlineProfile WithCategoryStyle(this StringlineProfile profile, CategoryStyle style)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(style);
        if (string.IsNullOrWhiteSpace(style.Name))
        {
            throw new ProfileEditException("The category needs a name.");
        }

        if (style.Priority is < 0 or > 100)
        {
            throw new ProfileEditException("The priority must be from 0 to 100.");
        }

        var list = profile.Categories.ToList();
        var index = list.FindIndex(c => c.CategoryId == style.CategoryId);
        if (index >= 0)
        {
            list[index] = style;
        }
        else
        {
            list.Add(style);
        }

        return profile with { Categories = list };
    }

    /// <summary>
    /// The profile's corridors that differ from what the generator would give for this timetable now: a corridor
    /// the user made, or one whose name, locations, distances, platforms, tracks or axis were changed, or with a headway set by hand.
    /// </summary>
    public static IReadOnlyList<Corridor> HandTuned(this StringlineProfile profile, WorkingTimetable timetable, CorridorGeneratorOptions? options = null) =>
        profile.HandTuned(CorridorGenerator.Generate(timetable, options).Select(c => c.Corridor).ToList(), timetable);

    /// <inheritdoc cref="HandTuned(StringlineProfile, WorkingTimetable, CorridorGeneratorOptions?)"/>
    public static IReadOnlyList<Corridor> HandTuned(this StringlineProfile profile, IReadOnlyList<Corridor> generated, WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(generated);
        return profile.Corridors
            .Where(c => !generated.Any(g => SameSettings(CorridorBuilder.Refresh(c, timetable), g)))
            .ToList();
    }

    /// <summary>
    /// The corridors replaced by freshly generated ones. With <paramref name="keepHandTuned"/>, the
    /// <see cref="HandTuned(StringlineProfile, WorkingTimetable, CorridorGeneratorOptions?)"/> corridors stay: one
    /// with a generated corridor's ID takes its place, and the others follow the generated ones.
    /// </summary>
    public static StringlineProfile RegenerateCorridors(this StringlineProfile profile, WorkingTimetable timetable, bool keepHandTuned, CorridorGeneratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var generated = CorridorGenerator.Generate(timetable, options).Select(c => c.Corridor).ToList();
        var corridors = generated.ToList();
        if (keepHandTuned)
        {
            foreach (var kept in profile.HandTuned(generated, timetable))
            {
                var refreshed = CorridorBuilder.Refresh(kept, timetable);
                var index = corridors.FindIndex(c => string.Equals(c.Id, kept.Id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    corridors[index] = refreshed;
                }
                else
                {
                    corridors.Add(refreshed);
                }
            }
        }

        return Checked((profile with { Corridors = corridors }).ResolveHeadways());
    }

    /// <summary>The first rule the profile breaks, including the editor's own rules, or null.</summary>
    public static string? FindProblem(StringlineProfile profile)
    {
        if (ProfileValidator.FindProblem(profile) is { } problem)
        {
            return problem;
        }

        foreach (var corridor in profile.Corridors)
        {
            if (corridor.Locations.Count < MinLocations)
            {
                return $"Corridor {corridor.Name} needs at least {MinLocations} locations.";
            }
        }

        return null;
    }

    private static StringlineProfile EditSection(this StringlineProfile profile, string id, int index, Func<Section, Section> edit) =>
        profile.EditCorridor(id, c =>
        {
            CheckIndex(c.Sections, index);
            return c with { Sections = c.Sections.Select((s, i) => i == index ? edit(s) : s).ToList() };
        });

    private static StringlineProfile EditLocations(this StringlineProfile profile, string id, WorkingTimetable? timetable, Action<List<CorridorLocation>> edit) =>
        profile.EditCorridor(id, c =>
        {
            var locations = c.Locations.ToList();
            edit(locations);
            return Measured(c with { Locations = locations }, timetable, profile.FallbackHeadwaySeconds);
        });

    /// <summary>
    /// Sections rebuilt for the corridor's locations (kept where a pair stays neighbours), then the WTT-derived values
    /// measured, or without a timetable just the axis.
    /// </summary>
    private static Corridor Measured(Corridor corridor, WorkingTimetable? timetable, int fallbackHeadwaySeconds)
    {
        var locations = corridor.Locations;
        var sections = new List<Section>(Math.Max(0, locations.Count - 1));
        for (var i = 0; i + 1 < locations.Count; i++)
        {
            var (a, b) = (locations[i].Tiploc, locations[i + 1].Tiploc);
            var existing = corridor.Sections.FirstOrDefault(s => (s.From == a && s.To == b) || (s.From == b && s.To == a));
            sections.Add(existing is null
                ? new Section(a, b, Section.DoubleLine, fallbackHeadwaySeconds, Section.DefaultMinRunSeconds)
                : existing with { From = a, To = b });
        }

        var rebuilt = corridor with { Sections = sections };
        return timetable is null
            ? CorridorAxis.Apply(CorridorBuilder.ResolveHeadways(rebuilt, fallbackHeadwaySeconds))
            : CorridorBuilder.Refresh(rebuilt, timetable, fallbackHeadwaySeconds);
    }

    /// <summary>
    /// True if the two corridors have the same user settings (ignoring what is measured from the WTT). Of the headways,
    /// only those set by hand are settings.
    /// </summary>
    private static bool SameSettings(Corridor a, Corridor b) =>
        string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
        && a.Name == b.Name
        && a.Axis == b.Axis
        && a.Locations.Count == b.Locations.Count
        && a.Locations.Zip(b.Locations).All(p => p.First.Tiploc == p.Second.Tiploc && p.First.DistanceKm == p.Second.DistanceKm && p.First.Platforms == p.Second.Platforms && p.First.Minor == p.Second.Minor)
        && a.Sections.Zip(b.Sections).All(p => SameHandHeadway(p.First, p.Second) && p.First.Tracks.SequenceEqual(p.Second.Tracks));

    private static bool SameHandHeadway(Section a, Section b) =>
        (a.HeadwaySource == HeadwaySource.Hand) == (b.HeadwaySource == HeadwaySource.Hand)
        && (a.HeadwaySource != HeadwaySource.Hand || a.DefaultHeadwaySeconds == b.DefaultHeadwaySeconds);

    private static (List<Corridor> List, int Index) Find(StringlineProfile profile, string id)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var list = profile.Corridors.ToList();
        var index = list.FindIndex(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? throw new ProfileEditException($"There is no corridor '{id}'.") : (list, index);
    }

    private static void CheckIndex<T>(IReadOnlyList<T> list, int index)
    {
        if (index < 0 || index >= list.Count)
        {
            throw new ProfileEditException($"There is no item {index + 1} (there are {list.Count}).");
        }
    }

    private static StringlineProfile Checked(StringlineProfile profile) =>
        FindProblem(profile) is { } problem ? throw new ProfileEditException(problem) : profile;
}
