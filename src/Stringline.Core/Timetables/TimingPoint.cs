namespace Stringline.Core.Timetables;

/// <summary>One <c>Trip</c> in a WTT train: a location with booked times.</summary>
public sealed record TimingPoint
{
    /// <summary>Position in the train's point list, from 0.</summary>
    public required int Index { get; init; }

    public required Tiploc Location { get; init; }

    /// <summary>Booked arrival; null at an origin or a pass.</summary>
    public SimTime? Arrive { get; init; }

    /// <summary>Booked departure, or the pass time when <see cref="IsPass"/>. Some points are untimed (both times null).</summary>
    public SimTime? Depart { get; init; }

    public bool IsPass { get; init; }

    public string? Platform { get; init; }

    /// <summary>Line taken out of this location.</summary>
    public string? Line { get; init; }

    /// <summary>Path taken into this location.</summary>
    public string? Path { get; init; }

    /// <summary>SimSig's direction flag at this point (true = Down), if the file gives one.</summary>
    public bool? DownDirection { get; init; }

    /// <summary>Engineering allowance in seconds, taken in the section from this point to the next.</summary>
    public int EngineeringAllowanceSeconds { get; init; }

    /// <summary>Pathing allowance in seconds, taken in the section from this point to the next.</summary>
    public int PathingAllowanceSeconds { get; init; }

    /// <summary>Performance allowance in seconds, taken in the section from this point to the next.</summary>
    public int PerformanceAllowanceSeconds { get; init; }

    /// <summary>Booked dwell override in seconds, if any.</summary>
    public int? DwellSeconds { get; init; }

    /// <summary>Request stop: the percentage chance SimSig stops the train here.</summary>
    public int? RequestPercent { get; init; }

    public bool SetDownOnly { get; init; }

    /// <summary>The category the train changes to here, if any.</summary>
    public string? NewCategoryId { get; init; }

    public IReadOnlyList<TrainActivity> Activities { get; init; } = [];

    /// <summary>All allowances in the section after this point, in seconds.</summary>
    public int AllowanceSeconds => EngineeringAllowanceSeconds + PathingAllowanceSeconds + PerformanceAllowanceSeconds;

    /// <summary>Arrival if booked, else departure or pass.</summary>
    public SimTime? FirstTime => Arrive ?? Depart;

    /// <summary>Departure or pass if booked, else arrival.</summary>
    public SimTime? LastTime => Depart ?? Arrive;

    public bool IsTimed => Arrive is not null || Depart is not null;

    public override string ToString() =>
        $"{Location} {(IsPass ? "pass " + Depart : $"{Arrive?.ToString() ?? "-"}/{Depart?.ToString() ?? "-"}")}";
}

/// <summary>SimSig's activity codes on a timing point, from the WTT <c>Activity/Activity</c> number.</summary>
public enum ActivityKind
{
    /// <summary>This train becomes the associated train (next working). Code 0.</summary>
    Next = 0,

    /// <summary>Divides; the associated train is the new rear portion. Code 1.</summary>
    DivideRear = 1,

    /// <summary>Divides; the associated train is the new front portion. Code 2.</summary>
    DivideFront = 2,

    /// <summary>Joins the associated train. Code 3.</summary>
    Join = 3,

    /// <summary>Detaches an engine at the rear, which becomes the associated train. Code 4.</summary>
    DetachEngineRear = 4,

    /// <summary>Detaches an engine at the front, which becomes the associated train. Code 5.</summary>
    DetachEngineFront = 5,

    /// <summary>Drops coaches at the rear, which become the associated train. Code 6.</summary>
    DropCoachesRear = 6,

    /// <summary>Drops coaches at the front, which become the associated train. Code 7.</summary>
    DropCoachesFront = 7,

    /// <summary>Is joined by the associated train. Code 8.</summary>
    JoinedBy = 8,

    /// <summary>Shares the platform with the associated train. Code 9.</summary>
    PlatformShare = 9,

    /// <summary>Crew change here. Code 10.</summary>
    CrewChange = 10,

    /// <summary>A code this version of Stringline does not know; see <see cref="TrainActivity.Code"/>.</summary>
    Unknown = -1,
}

/// <param name="Code">The raw WTT activity number.</param>
/// <param name="AssociatedHeadcode">The associated train as the WTT names it by headcode (older files).</param>
/// <param name="AssociatedUid">The associated train as the WTT names it by UID (newer files).</param>
/// <param name="Train">The associated train, resolved at import; null if none was named or it could not be found.</param>
public sealed record TrainActivity(ActivityKind Kind, int Code, Headcode? AssociatedHeadcode, string? AssociatedUid, TrainId? Train)
{
    /// <summary>True for activities after which the associated train starts here (next working, divide, detach, drop).</summary>
    public bool FormsAssociatedTrain => Kind is ActivityKind.Next
        or ActivityKind.DivideRear or ActivityKind.DivideFront
        or ActivityKind.DetachEngineRear or ActivityKind.DetachEngineFront
        or ActivityKind.DropCoachesRear or ActivityKind.DropCoachesFront;

    public static ActivityKind KindFromCode(int code) =>
        code is >= 0 and <= 10 ? (ActivityKind)code : ActivityKind.Unknown;

    /// <summary>The associated train as written in the file, for display when it was not resolved.</summary>
    public string? AssociatedText => AssociatedUid ?? AssociatedHeadcode?.Value;
}
