namespace Stringline.Core.Timetables;

/// <summary>How a train comes into the simulation.</summary>
public enum TrainStart
{
    /// <summary>Starts at its first timing point: formed from another train (next working, divide, detach) or appearing there.</summary>
    AtFirstLocation,

    /// <summary>Enters from off the area at <see cref="Train.EntryPoint"/> at <see cref="Train.EntryTime"/>.</summary>
    EntryPoint,

    /// <summary>Already in the sim when it starts, at <see cref="Train.SeedPoint"/> (a signal or berth).</summary>
    Seeded,
}

/// <summary>One working in the WTT: a headcode and its ordered timing points.</summary>
public sealed record Train
{
    /// <summary>Stringline's identity for this train: the WTT UID if present, else the headcode; repeats get <c>#2</c>, <c>#3</c>… in file order.</summary>
    public required TrainId Id { get; init; }

    public required Headcode Headcode { get; init; }

    /// <summary>The WTT's own UID, if it has one (older timetables do not). Not unique: alternative paths share one.</summary>
    public string? Uid { get; init; }

    /// <summary>Description text, with any SimSig <c>$template</c> fields filled in.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The <see cref="TrainCategory.Id"/> this train uses, or null.</summary>
    public string? CategoryId { get; init; }

    public TrainStart Start { get; init; }

    /// <summary>Entry point code, when <see cref="Start"/> is <see cref="TrainStart.EntryPoint"/>. Entry points never appear as gateway locations.</summary>
    public Tiploc? EntryPoint { get; init; }

    /// <summary>Booked entry time at <see cref="EntryPoint"/>.</summary>
    public SimTime? EntryTime { get; init; }

    /// <summary>Seed signal or berth, when <see cref="Start"/> is <see cref="TrainStart.Seeded"/>.</summary>
    public Tiploc? SeedPoint { get; init; }

    /// <summary>
    /// The ID of the <see cref="SeedGroup"/> a seeded train is seeded in, or null for none (seeded at the timetable's
    /// start). Only seeded trains have one.
    /// </summary>
    public string? SeedGroup { get; init; }

    /// <summary>The WTT's <c>Started</c> flag: SimSig sets it on seeded trains, which are already under way when the sim starts.</summary>
    public bool Started { get; init; }

    public int? MaxSpeedMph { get; init; }

    public int? LengthMetres { get; init; }

    public bool IsFreight { get; init; }

    /// <summary>The power types the train can use, one letter each as SimSig writes them (<c>D</c> diesel, <c>O</c> overhead, <c>3</c> third rail…), falling back to its category's.</summary>
    public string? Electrification { get; init; }

    /// <summary>Runs only as required (SimSig may or may not run it).</summary>
    public bool AsRequired { get; init; }

    public string? OriginName { get; init; }

    public string? DestinationName { get; init; }

    /// <summary>Time at the real origin, which may be outside the sim (and on the previous day).</summary>
    public SimTime? OriginTime { get; init; }

    public SimTime? DestinationTime { get; init; }

    public string? OperatorCode { get; init; }

    public string? Notes { get; init; }

    /// <summary>The train's dwell times, falling back to its category's where the train gives none.</summary>
    public DwellTimes Dwell { get; init; } = DwellTimes.None;

    /// <summary>Timing points in order; never empty.</summary>
    public required IReadOnlyList<TimingPoint> Points { get; init; }

    /// <summary>The train whose next-working, divide, detach or drop activity forms this one, if any.</summary>
    public TrainId? FormedFrom { get; init; }

    public Tiploc Origin => Points[0].Location;

    public Tiploc Destination => Points[^1].Location;

    /// <summary>The first booked time at any point, or the entry time if no point is timed.</summary>
    public SimTime? FirstTime => Points.Select(p => p.FirstTime).FirstOrDefault(t => t is not null) ?? EntryTime;

    /// <summary>
    /// How long before its first booked time the train enters at <see cref="EntryPoint"/> (#22): from
    /// <see cref="EntryTime"/> to the first point's time, if both are booked and the entry is earlier; null otherwise.
    /// </summary>
    public int? EntryLeadSeconds =>
        EntryPoint is not null && EntryTime is { } entry && Points.Count > 0 && Points[0].FirstTime is { } first && first > entry ? first - entry : null;

    /// <summary>
    /// When the train is booked to come into the sim: its entry time if it enters, else its first booked time (see
    /// <see cref="FirstTime"/>). Seeded trains are in the sim from the start.
    /// </summary>
    public SimTime? BookedStart => Start == TrainStart.EntryPoint && EntryTime is { } entry ? entry : FirstTime;

    /// <summary>The last booked time at any point.</summary>
    public SimTime? LastTime => Points.Select(p => p.LastTime).LastOrDefault(t => t is not null);

    /// <summary>The train this one becomes at its last point (activity <see cref="ActivityKind.Next"/>), if resolved.</summary>
    public TrainId? NextWorking => Points
        .SelectMany(p => p.Activities)
        .LastOrDefault(a => a.Kind == ActivityKind.Next)?.Train;

    public override string ToString() => $"{Headcode} {Id}";
}
