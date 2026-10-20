namespace Stringline.Core.Timetables;

/// <summary>
/// A SimSig working timetable (WTT) as imported from a <c>.wtt</c> file: the immutable plan source. Trains are in
/// file order; <see cref="Train.Id"/> is unique within one timetable and stable across re-imports of the same file.
/// </summary>
public sealed class WorkingTimetable
{
    private readonly Dictionary<TrainId, Train> _byId;
    private readonly ILookup<Headcode, Train> _byHeadcode;
    private readonly Dictionary<string, TrainCategory> _categories;

    public WorkingTimetable(TimetableHeader header, IReadOnlyList<TrainCategory> categories, IReadOnlyList<Train> trains, IReadOnlyList<TimetableRule> rules)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(trains);
        ArgumentNullException.ThrowIfNull(rules);
        Header = header;
        Categories = categories;
        Trains = trains;
        Rules = rules;
        _byId = new Dictionary<TrainId, Train>(trains.Count);
        foreach (var train in trains)
        {
            if (!_byId.TryAdd(train.Id, train))
            {
                throw new ArgumentException($"Train ID {train.Id} is used twice.", nameof(trains));
            }
        }

        _byHeadcode = trains.ToLookup(t => t.Headcode);
        _categories = new Dictionary<string, TrainCategory>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            _categories.TryAdd(category.Id, category);
        }
    }

    public TimetableHeader Header { get; }

    /// <summary>The simulation's area ID (e.g. <c>waterloo</c>), matching the gateway's <c>area_id</c>; keys the profile.</summary>
    public string AreaId => Header.AreaId;

    public IReadOnlyList<TrainCategory> Categories { get; }

    public IReadOnlyList<Train> Trains { get; }

    public IReadOnlyList<TimetableRule> Rules { get; }

    /// <summary>
    /// The WTT's seed groups (<c>SeedGroups</c>): the times other than the timetable's start that the sim can be started
    /// at, each with its own seeded trains. Empty for most timetables.
    /// </summary>
    public IReadOnlyList<SeedGroup> SeedGroups { get; init; } = [];

    /// <summary>The seed group with this ID, if the WTT defines it.</summary>
    public SeedGroup? FindSeedGroup(string? id) => id is null ? null : SeedGroups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    public Train? Find(TrainId id) => _byId.GetValueOrDefault(id);

    public Train Get(TrainId id) => _byId.TryGetValue(id, out var train) ? train : throw new KeyNotFoundException($"No train {id} in the timetable.");

    /// <summary>Every train with this headcode, in file order. Headcodes are exact: <c>2O03</c> and <c>2O03-1</c> are different workings.</summary>
    public IEnumerable<Train> WithHeadcode(Headcode headcode) => _byHeadcode[headcode];

    public TrainCategory? FindCategory(string? id) => id is null ? null : _categories.GetValueOrDefault(id);
}

/// <param name="AreaId">The <c>ID</c> attribute of the root element: the simulation, e.g. <c>waterloo</c>.</param>
/// <param name="SimSigVersion">The SimSig version that saved the file (root <c>Version</c> attribute), e.g. <c>5.18</c>.</param>
/// <param name="Name">The timetable's title.</param>
/// <param name="TimetableVersion">The author's version number (<c>VMajor.VMinor.VBuild</c>), if given.</param>
/// <param name="StartTime">Sim time the timetable starts.</param>
/// <param name="FinishTime">Sim time the timetable finishes; may be after midnight (more than 24 h).</param>
public sealed record TimetableHeader(
    string AreaId,
    string SimSigVersion,
    string Name,
    string Description,
    string? TimetableVersion,
    SimTime StartTime,
    SimTime FinishTime,
    string? TrainDescriptionTemplate = null);

/// <summary>
/// A seed group from the WTT header: a time the sim can be started at, other than the timetable's own start. Seeded
/// trains name the group they are seeded in (<see cref="Train.SeedGroup"/>); a seeded train with no group is seeded
/// when the sim starts at the timetable's start.
/// </summary>
/// <param name="Id">The group's ID as the WTT writes it (a GUID in braces).</param>
/// <param name="StartTime">When a sim started with this group begins.</param>
/// <param name="Title">The name SimSig's Loader shows, e.g. <c>14:00</c>.</param>
public sealed record SeedGroup(string Id, SimTime StartTime, string Title)
{
    public override string ToString() => Title.Length > 0 ? Title : StartTime.ToString();
}

/// <summary>
/// A <c>TrainCategory</c> from the WTT header: performance and dwell defaults shared by trains. Display colour and
/// priority are not in the WTT; they come from the profile.
/// </summary>
public sealed record TrainCategory(
    string Id,
    string Description,
    bool IsFreight,
    int? MaxSpeedMph,
    int? LengthMetres,
    int? AccelBrakeIndex,
    int? SpeedClass,
    string? Electrification,
    DwellTimes Dwell);

/// <summary>SimSig dwell and reaction times in seconds; null where the file gives none.</summary>
public sealed record DwellTimes(
    int? RedSignalMoveOff = null,
    int? StationForward = null,
    int? StationReverse = null,
    int? TerminateForward = null,
    int? TerminateReverse = null,
    int? Join = null,
    int? Divide = null,
    int? CrewChange = null)
{
    public static DwellTimes None { get; } = new();

    /// <summary>Each value from this record, or from <paramref name="fallback"/> where this one has none.</summary>
    public DwellTimes Or(DwellTimes fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return new(
            RedSignalMoveOff ?? fallback.RedSignalMoveOff,
            StationForward ?? fallback.StationForward,
            StationReverse ?? fallback.StationReverse,
            TerminateForward ?? fallback.TerminateForward,
            TerminateReverse ?? fallback.TerminateReverse,
            Join ?? fallback.Join,
            Divide ?? fallback.Divide,
            CrewChange ?? fallback.CrewChange);
    }
}
