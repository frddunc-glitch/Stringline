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
    /// Profiles made before S22 have a timetable's title here, which counts as no name (<see cref="SimName"/>).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// <see cref="Name"/> was typed in the profile editor (S31), or found to be no timetable's title
    /// (<see cref="SettleName"/>), so it is shown even when it matches a title. Written only when set.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NameByHand { get; init; }

    /// <summary>
    /// The titles of the timetables this profile was made or refreshed from (S31), so a sim name that is only one of
    /// them counts as no name. Profiles from before S31 start with none; an older Stringline ignores them.
    /// </summary>
    public IReadOnlyList<string> TimetableTitles { get; init; } = [];

    /// <summary>
    /// Whether <see cref="Name"/> may be an older timetable's title not yet known (S31): set, neither typed nor settled,
    /// and none of <see cref="TimetableTitles"/>. Only a profile saved before S31 can be so.
    /// </summary>
    [JsonIgnore]
    public bool HasUnsettledName => Name is { } name && !NameByHand && !IsTimetableTitle(name);

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

    /// <summary>
    /// Routes asked of SimSig by assist (S19), one per move, most recent last. Profiles from before S19 have none; an
    /// older Stringline ignores them.
    /// </summary>
    public IReadOnlyList<AssistRoute> AssistRoutes { get; init; } = [];

    /// <summary>
    /// The signals, track circuits and TD berths learned from play (S37, #19); null for none. Profiles from before S37
    /// have none; an older Stringline ignores them. Read them through <see cref="Places"/>.
    /// </summary>
    public SignallingPlaces? Signalling { get; init; }

    /// <summary><see cref="Signalling"/>, or <see cref="SignallingPlaces.Empty"/>.</summary>
    [JsonIgnore]
    public SignallingPlaces Places => Signalling ?? SignallingPlaces.Empty;

    /// <summary>The profile with these learned places (none is written as no <see cref="Signalling"/> at all).</summary>
    public StringlineProfile WithPlaces(SignallingPlaces places)
    {
        ArgumentNullException.ThrowIfNull(places);
        return this with { Signalling = places.IsEmpty ? null : places };
    }

    /// <summary>The route last asked for this exact move, if any.</summary>
    public AssistRoute? FindAssistRoute(AssistMove move) => AssistRoutes.LastOrDefault(r => r.Move == move && r.IsComplete);

    /// <summary>The profile remembering <paramref name="route"/> for its move, in place of any earlier one for the same move.</summary>
    public StringlineProfile WithAssistRoute(AssistRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return this with { AssistRoutes = [.. AssistRoutes.Where(r => r.Move != route.Move), route] };
    }

    /// <summary>
    /// The sim's name to show, or null to show the area ID: a name typed by hand always, else the name unless it is
    /// (ignoring case and spaces) the title of <paramref name="loaded"/> or of any timetable in <see cref="TimetableTitles"/>.
    /// </summary>
    public string? SimName(TimetableHeader? loaded = null) =>
        Name is { } name && (NameByHand || !IsTimetableTitle(name, loaded)) ? name : null;

    /// <summary>The title for this sim running <paramref name="loaded"/> (<see cref="SimTitle"/>), with <see cref="SimName"/>.</summary>
    /// <param name="areaId">The gateway's area ID, if known; else the timetable's.</param>
    public SimTitle Title(string? areaId, TimetableHeader? loaded, bool review = false) =>
        SimTitle.From(SimName(loaded), areaId, loaded, review, keepName: true);

    /// <summary>Whether this is (ignoring case and spaces) the title of <paramref name="loaded"/> or of any timetable in <see cref="TimetableTitles"/>.</summary>
    public bool IsTimetableTitle(string? text, TimetableHeader? loaded = null) =>
        SimTitle.SameTitle(text, loaded?.Name) || TimetableTitles.Any(t => SimTitle.SameTitle(t, text));

    /// <summary>The profile remembering a timetable's title (<see cref="TimetableTitles"/>); unchanged if blank or known.</summary>
    public StringlineProfile WithTimetableTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) || TimetableTitles.Any(t => SimTitle.SameTitle(t, title)) ? this
            : this with { TimetableTitles = [.. TimetableTitles, title.Trim()] };

    /// <summary>
    /// Settles a name saved before S31 (<see cref="HasUnsettledName"/>) against the titles of the sim's other
    /// timetables: one that matches is remembered, so the name counts as no name; if none does, the name was typed,
    /// and is marked <see cref="NameByHand"/>. Unchanged when there is nothing to settle.
    /// </summary>
    public StringlineProfile SettleName(IEnumerable<string> otherTitles)
    {
        ArgumentNullException.ThrowIfNull(otherTitles);
        if (!HasUnsettledName)
        {
            return this;
        }

        return otherTitles.FirstOrDefault(t => SimTitle.SameTitle(t, Name)) is { } title ? WithTimetableTitle(title) : this with { NameByHand = true };
    }

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
        }.WithTimetableTitle(timetable.Header.Name);
    }

    /// <summary>
    /// The profile brought up to date with a (possibly re-imported) timetable: every corridor's derived values are
    /// recomputed, headways not set by hand are worked out again, categories new to the timetable get default
    /// styles, and its title is remembered (<see cref="TimetableTitles"/>). User settings are kept.
    /// </summary>
    public StringlineProfile Refresh(WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        var known = Categories.Select(c => c.CategoryId).ToHashSet(StringComparer.Ordinal);
        return (this with
        {
            Corridors = Corridors.Select(c => CorridorBuilder.Refresh(c, timetable, FallbackHeadwaySeconds)).ToList(),
            Categories = [.. Categories, .. CategoryStyle.CreateDefaults(timetable).Where(c => !known.Contains(c.CategoryId))],
        }).WithTimetableTitle(timetable.Header.Name);
    }

    /// <summary>The profile with this global default headway, and every headway not set by hand worked out again.</summary>
    public StringlineProfile WithGlobalHeadway(int seconds) => (this with { GlobalHeadwaySeconds = Math.Max(0, seconds) }).ResolveHeadways();

    /// <summary>
    /// The profile with each section's block headway measured from its learned places that have
    /// <paramref name="minSamples"/> samples (S39, <see cref="BlockHeadways"/>), and its headways worked out again.
    /// </summary>
    public StringlineProfile WithBlockHeadways(int minSamples = SignallingPlaces.DefaultMinSamples) =>
        WithBlockHeadways(BlockHeadways.Measure(Corridors, UsablePlaces.Of(Places, minSamples)));

    /// <summary>The profile with these measured block headways, and its headways worked out again.</summary>
    public StringlineProfile WithBlockHeadways(BlockHeadways blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return this with { Corridors = blocks.Apply(Corridors, FallbackHeadwaySeconds) };
    }

    /// <summary>Every section's headway not set by hand worked out again (<see cref="Section.ResolveHeadway"/>) against <see cref="FallbackHeadwaySeconds"/>.</summary>
    public StringlineProfile ResolveHeadways()
    {
        var fallback = FallbackHeadwaySeconds;
        return this with { Corridors = Corridors.Select(c => CorridorBuilder.ResolveHeadways(c, fallback)).ToList() };
    }
}
