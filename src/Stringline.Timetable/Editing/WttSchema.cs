namespace Stringline.Timetable.Editing;

/// <summary>
/// How SimSig writes a timetable's XML: each element's children in order, which values it leaves out, and which fields
/// are yes/no. Taken from the corpus of 285 timetables (no file disagrees with these orders) and the wiki's XML format
/// page; <c>docs/08-…md</c> §3.1.
/// </summary>
internal static class WttSchema
{
    public const string RootName = "SimSigTimetable";

    /// <summary>The root's children copied to <c>TimetableHeader.xml</c>, SimSig's <c>[TH]</c> fields (every corpus file agrees).</summary>
    public static readonly HashSet<string> HeaderFields =
        ["Name", "Description", "StartTime", "FinishTime", "VMajor", "VMinor", "VBuild", "TrainDescriptionTemplate", "SeedGroupSummary", "ScenarioOptions"];

    /// <summary>Children in SimSig's order, by the parent's name. A child not listed goes after the others.</summary>
    private static readonly Dictionary<string, string[]> Orders = new(StringComparer.Ordinal)
    {
        [RootName] =
        [
            "Name", "Description", "StartTime", "FinishTime", "VMajor", "VMinor", "VBuild", "TrainDescriptionTemplate",
            "SeedGroupSummary", "ScenarioOptions", "CautionSpeedSets", "TTClassOfServiceRules", "TrainCategories", "Timetables",
            "TimetableRules", "Decisions", "SeedGroups",
        ],
        ["TrainCategory"] =
        [
            "Description", "AccelBrakeIndex", "IsFreight", "CanUseGoodsLines", "MaxSpeed", "TrainLength", "SpeedClass",
            "PowerToWeightCategory", "DwellTimes", "Electrification", "CautionSpeedSet",
        ],
        ["DwellTimes"] =
            ["RedSignalMoveOff", "StationForward", "StationReverse", "TerminateForward", "TerminateReverse", "Join", "Divide", "CrewChange"],
        ["Timetable"] =
        [
            "ID", "UID", "AccelBrakeIndex", "AsRequired", "AsRequiredPercent", "Delay", "DelayWarned", "DepartTime", "Description",
            "SeedPoint", "SeedingGap", "EntryPoint", "SeedGroup", "EntryDecision", "EntryChoice", "ActualEntryPoint", "EntrySpeed",
            "MaxSpeed", "SpeedClassIndex", "SpeedClass", "Started", "IsFreight", "CanUseGoodsLines", "TrainLength",
            "IncrementOnTransfer", "PowerToWeightCategory", "ClassOfService", "Electrification", "STP", "BonusScore",
            "AsRequiredTested", "RunOnTime", "EntryWarned", "EntryAlerted", "PermissionDelayed", "QueuePosition", "OriginName",
            "DestinationName", "OriginTime", "DestinationTime", "OperatorCode", "Notes", "ShuntMove", "ShuntLastEntry", "ShuntPhone",
            "ShuntInterpose", "ShuntTimes", "NonARSOnEntry", "StartTraction", "Category", "RedSignalMoveOff", "StationForward",
            "StationReverse", "TerminateForward", "TerminateReverse", "Join", "Divide", "CrewChange", "Trips",
        ],
        ["Trip"] =
        [
            "Location", "NewCategory", "DepPassTime", "SetDownKind", "SetDownOnly", "ArrTime", "Platform", "Line", "Path",
            "AutoPlatform", "AutoLine", "AutoPath", "EngAllowance", "PathAllowance", "PerfAllowance", "IsPassTime",
            "AllowStopsOnThroughLines", "DownDirection", "PrevPathEndDown", "NextPathStartDown", "StopLocation", "StopAdjustment",
            "DwellTime", "CrewChange", "BerthsHere", "WaitForBookedTime", "RequestPercent", "Activities",
        ],
        ["Activity"] = ["Activity", "AssociatedTrain", "AssociatedUID", "CrewChangeManualTime", "Decision", "Choice", "ActivityDuration"],
        ["TimetableRule"] = ["TrainUID", "Train", "Rule", "Location", "Time", "Train2", "Train2UID", "XOREntered", "Achieved", "TimeElapsed"],
        ["SeedGroup"] = ["ID", "StartTime", "Title"],
        ["Decision"] = ["Chosen", "Description", "Choices"],
        ["Choice"] = ["Chosen", "Description", "Weight", "Announcement"],
        ["TTCOSRule"] = ["ClassOfService", "IDmatch", "Freight", "SpeedClassMatchType", "MatchSpeedClass"],
        ["CautionSpeedSet"] = ["Name", "CautionSpeeds"],
        ["CautionSpeed"] =
            ["AspectPassed", "FromLineSpeed", "ToLineSpeed", "ReduceNowValue", "ApproachNextValue", "ApproachNextDistance", "NowValueType", "NextValueType"],
        ["ShuntTime"] = ["StartTime", "EndTime", "MinInterval", "MaxInterval"],
    };

    /// <summary>
    /// Parents whose defaults SimSig leaves out: no yes/no field is ever written as no, and no number as 0 (bar
    /// <see cref="KeepsZero"/>). Categories, the header, seed groups and the lists write every value.
    /// </summary>
    private static readonly HashSet<string> OmitsDefaults = ["Timetable", "Trip", "Activity", "TimetableRule"];

    /// <summary>Fields of those parents where 0 is a value, not the default (an activity's code 0 is "next").</summary>
    private static readonly HashSet<string> ZeroIsAValue = ["Activity/Activity", "TimetableRule/Rule", "Timetable/ClassOfService", "Timetable/ShuntInterpose"];

    /// <summary>Yes/no fields, wherever they appear (written <c>True</c>/<c>False</c> or <c>-1</c>/<c>0</c>).</summary>
    public static readonly HashSet<string> Booleans =
    [
        "AsRequired", "DelayWarned", "Started", "IsFreight", "CanUseGoodsLines", "IncrementOnTransfer", "STP", "BonusScore",
        "AsRequiredTested", "RunOnTime", "EntryAlerted", "PermissionDelayed", "ShuntMove", "ShuntPhone", "NonARSOnEntry",
        "SetDownOnly", "AutoPlatform", "AutoLine", "AutoPath", "IsPassTime", "AllowStopsOnThroughLines", "DownDirection",
        "PrevPathEndDown", "NextPathStartDown", "BerthsHere", "WaitForBookedTime", "XOREntered", "Achieved", "Chosen",
    ];

    /// <summary>The child's place in SimSig's order under this parent, or -1 if SimSig's order does not list it.</summary>
    public static int OrderOf(string parent, string child) =>
        Orders.TryGetValue(parent, out var order) ? Array.IndexOf(order, child) : -1;

    public static bool LeavesOutDefaults(string parent) => OmitsDefaults.Contains(parent);

    public static bool KeepsZero(string parent, string field) => ZeroIsAValue.Contains(parent + "/" + field);
}
