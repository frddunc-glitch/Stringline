namespace Stringline.Core.Timetables;

/// <summary>SimSig timetable rule types, from the WTT <c>TimetableRule/Rule</c> number. X is <c>Train</c>, Y is <c>Train2</c>.</summary>
public enum RuleKind
{
    XAppearsAfterYEnters = 0,
    XAppearsAfterYLeaves = 1,
    XAppearsAfterYArrives = 2,
    XNotIfY = 3,
    XDepartsAfterYArrives = 4,
    XDepartsAfterYEnters = 5,
    XDepartsAfterYLeaves = 6,
    XDepartsAfterYJoins = 7,
    XDepartsAfterYDivides = 8,
    XDepartsAfterYForms = 9,
    MutuallyExclusive = 10,
    XAppearsAfterYJoins = 11,
    XAppearsAfterYDivides = 12,
    XAppearsAfterYForms = 13,
    Alternatives = 14,

    /// <summary>A code this version of Stringline does not know; see <see cref="TimetableRule.Code"/>.</summary>
    Unknown = -1,
}

/// <summary>
/// A <c>TimetableRule</c>: SimSig holds train X until something happens to train Y. Kept for the forecaster;
/// trains are named by headcode in older files and by UID in newer ones.
/// </summary>
/// <param name="Code">The raw rule number.</param>
/// <param name="TrainText">Train X as written in the file (headcode or UID).</param>
/// <param name="Train2Text">Train Y as written in the file.</param>
/// <param name="Train">Train X, if it resolves to exactly one train.</param>
/// <param name="Train2">Train Y, if it resolves to exactly one train.</param>
/// <param name="DelaySeconds">The rule's <c>Time</c>: how long after Y's event X may go, in seconds.</param>
/// <param name="Location">Where Y's event happens, if the rule names one.</param>
public sealed record TimetableRule(
    RuleKind Kind,
    int Code,
    string? TrainText,
    string? Train2Text,
    TrainId? Train,
    TrainId? Train2,
    int? DelaySeconds,
    Tiploc? Location)
{
    public static RuleKind KindFromCode(int code) => code is >= 0 and <= 14 ? (RuleKind)code : RuleKind.Unknown;
}
