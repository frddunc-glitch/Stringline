using System.Xml.Linq;
using Stringline.Core;

namespace Stringline.Timetable.Editing;

/// <summary>The root's own fields: the sim, title, times and version (SimSig's <c>[TH]</c> fields, <c>04-…md</c> §1).</summary>
public sealed class HeaderNode(WttDocument document, XElement element) : WttNode(document, element)
{
    /// <summary>The sim's area ID (root <c>ID</c> attribute), e.g. <c>waterloo</c>.</summary>
    public string? AreaId { get => GetAttribute("ID"); set => SetAttribute("ID", value); }

    /// <summary>The SimSig version that saved the file (root <c>Version</c> attribute).</summary>
    public string? SimSigVersion { get => GetAttribute("Version"); set => SetAttribute("Version", value); }

    public string? Name { get => GetText("Name"); set => SetText("Name", value); }

    /// <summary>The description, kept as written: SimSig stores line breaks as the two characters <c>\n</c>.</summary>
    public string? Description { get => GetText("Description"); set => SetText("Description", value); }

    public SimTime? StartTime { get => GetTime("StartTime"); set => SetInt("StartTime", value?.Seconds); }

    public SimTime? FinishTime { get => GetTime("FinishTime"); set => SetInt("FinishTime", value?.Seconds); }

    public string? TrainDescriptionTemplate { get => GetText("TrainDescriptionTemplate"); set => SetText("TrainDescriptionTemplate", value); }

    public string? ScenarioOptions { get => GetText("ScenarioOptions"); set => SetText("ScenarioOptions", value); }

    /// <summary><c>VMajor.VMinor.VBuild</c>, or null if the file has none (a missing minor or build is 0).</summary>
    public WttVersion? Version
    {
        get => GetInt("VMajor") is { } major ? new WttVersion(major, GetInt("VMinor") ?? 0, GetInt("VBuild") ?? 0) : null;
        set
        {
            SetInt("VMajor", value?.Major);
            SetInt("VMinor", value?.Minor);
            SetInt("VBuild", value?.Build);
        }
    }

    internal override WttNode Wrap(XElement element) => new HeaderNode(Document, element);
}

/// <summary>A <c>TrainCategory</c>: a train type, with its dwell times. SimSig writes every value of a category.</summary>
public sealed class CategoryNode(WttDocument document, XElement element) : WttNode(document, element)
{
    public string? Id { get => GetAttribute("ID"); set => SetAttribute("ID", value); }

    public string? Description { get => GetText("Description"); set => SetText("Description", value); }

    public int? AccelBrakeIndex { get => GetInt("AccelBrakeIndex"); set => SetInt("AccelBrakeIndex", value); }

    public bool IsFreight { get => GetBool("IsFreight"); set => SetBool("IsFreight", value); }

    public bool CanUseGoodsLines { get => GetBool("CanUseGoodsLines"); set => SetBool("CanUseGoodsLines", value); }

    public int? MaxSpeed { get => GetInt("MaxSpeed"); set => SetInt("MaxSpeed", value); }

    public int? TrainLength { get => GetInt("TrainLength"); set => SetInt("TrainLength", value); }

    public int? SpeedClass { get => GetInt("SpeedClass"); set => SetInt("SpeedClass", value); }

    public int? PowerToWeightCategory { get => GetInt("PowerToWeightCategory"); set => SetInt("PowerToWeightCategory", value); }

    public string? Electrification { get => GetText("Electrification"); set => SetText("Electrification", value); }

    public string? CautionSpeedSet { get => GetText("CautionSpeedSet"); set => SetText("CautionSpeedSet", value); }

    /// <summary>The category's <c>DwellTimes</c> (seconds by kind: <c>StationForward</c>, <c>Join</c>…), made if missing when written.</summary>
    public int? GetDwell(string kind) => Element.Element("DwellTimes") is { } dwell ? new ElementNode(Document, dwell).GetInt(kind) : null;

    public void SetDwell(string kind, int? seconds)
    {
        var dwell = Element.Element("DwellTimes");
        if (dwell is null)
        {
            if (seconds is null)
            {
                return;
            }

            dwell = new XElement("DwellTimes");
            FieldWriter.InsertInOrder(Element, dwell);
        }

        new ElementNode(Document, dwell).SetInt(kind, seconds);
    }

    internal override WttNode Wrap(XElement element) => new CategoryNode(Document, element);
}

/// <summary>
/// A train (<c>Timetable</c> element). Its identity in the editor is the element, not the headcode, so renumbering
/// keeps it (<see cref="TimetableEdit.Renumber"/>). Saved-game state (<c>Started</c>, <c>Delay</c>, <c>Entry…</c>) is
/// reached by field name.
/// </summary>
public sealed class TrainNode(WttDocument document, XElement element) : WttNode(document, element)
{
    /// <summary>The headcode (<c>ID</c>), e.g. <c>1A23</c> or <c>2O03-1</c>.</summary>
    public string? Headcode { get => GetText("ID"); set => SetText("ID", value); }

    public string? Uid { get => GetText("UID"); set => SetText("UID", value); }

    /// <summary>The description as written, often <c>$template</c>.</summary>
    public string? Description { get => GetText("Description"); set => SetText("Description", value); }

    public string? Category { get => GetText("Category"); set => SetText("Category", value); }

    /// <summary>Entry or seed time (<c>DepartTime</c>).</summary>
    public SimTime? DepartTime { get => GetTime("DepartTime"); set => SetTime("DepartTime", value); }

    public string? EntryPoint { get => GetText("EntryPoint"); set => SetText("EntryPoint", value); }

    public string? SeedPoint { get => GetText("SeedPoint"); set => SetText("SeedPoint", value); }

    public int? SeedingGap { get => GetInt("SeedingGap"); set => SetInt("SeedingGap", value); }

    public string? SeedGroup { get => GetText("SeedGroup"); set => SetText("SeedGroup", value); }

    public int? MaxSpeed { get => GetInt("MaxSpeed"); set => SetInt("MaxSpeed", value); }

    public int? TrainLength { get => GetInt("TrainLength"); set => SetInt("TrainLength", value); }

    public bool IsFreight { get => GetBool("IsFreight"); set => SetBool("IsFreight", value); }

    public bool CanUseGoodsLines { get => GetBool("CanUseGoodsLines"); set => SetBool("CanUseGoodsLines", value); }

    public bool AsRequired { get => GetBool("AsRequired"); set => SetBool("AsRequired", value); }

    public int? AsRequiredPercent { get => GetInt("AsRequiredPercent"); set => SetInt("AsRequiredPercent", value); }

    public int? AccelBrakeIndex { get => GetInt("AccelBrakeIndex"); set => SetInt("AccelBrakeIndex", value); }

    public int? SpeedClass { get => GetInt("SpeedClass"); set => SetInt("SpeedClass", value); }

    public int? PowerToWeightCategory { get => GetInt("PowerToWeightCategory"); set => SetInt("PowerToWeightCategory", value); }

    public int? ClassOfService { get => GetInt("ClassOfService"); set => SetInt("ClassOfService", value); }

    public string? Electrification { get => GetText("Electrification"); set => SetText("Electrification", value); }

    public string? StartTraction { get => GetText("StartTraction"); set => SetText("StartTraction", value); }

    public string? OriginName { get => GetText("OriginName"); set => SetText("OriginName", value); }

    public string? DestinationName { get => GetText("DestinationName"); set => SetText("DestinationName", value); }

    public SimTime? OriginTime { get => GetTime("OriginTime"); set => SetTime("OriginTime", value); }

    public SimTime? DestinationTime { get => GetTime("DestinationTime"); set => SetTime("DestinationTime", value); }

    public string? OperatorCode { get => GetText("OperatorCode"); set => SetText("OperatorCode", value); }

    public string? Notes { get => GetText("Notes"); set => SetText("Notes", value); }

    /// <summary>A dwell time the train sets for itself over its category's (<c>StationForward</c>, <c>Join</c>…), in seconds.</summary>
    public int? GetDwell(string kind) => GetInt(kind);

    public void SetDwell(string kind, int? seconds) => SetInt(kind, seconds);

    /// <summary>The train's <c>Trips</c> element (its timing points), if it has one.</summary>
    public XElement? TripsElement => Element.Element("Trips");

    public IEnumerable<TripNode> Trips => TripsElement?.Elements("Trip").Select(e => new TripNode(Document, e)) ?? [];

    internal override WttNode Wrap(XElement element) => new TrainNode(Document, element);
}

/// <summary>A timing point (<c>Trip</c>).</summary>
public sealed class TripNode(WttDocument document, XElement element) : WttNode(document, element)
{
    /// <summary>The TIPLOC.</summary>
    public string? Location { get => GetText("Location"); set => SetText("Location", value); }

    public SimTime? ArrTime { get => GetTime("ArrTime"); set => SetTime("ArrTime", value); }

    /// <summary>The departure, or the pass time when <see cref="IsPassTime"/>.</summary>
    public SimTime? DepPassTime { get => GetTime("DepPassTime"); set => SetTime("DepPassTime", value); }

    public bool IsPassTime { get => GetBool("IsPassTime"); set => SetBool("IsPassTime", value); }

    public string? Platform { get => GetText("Platform"); set => SetText("Platform", value); }

    public string? Line { get => GetText("Line"); set => SetText("Line", value); }

    public string? Path { get => GetText("Path"); set => SetText("Path", value); }

    public bool AutoPlatform { get => GetBool("AutoPlatform"); set => SetBool("AutoPlatform", value); }

    public bool AutoLine { get => GetBool("AutoLine"); set => SetBool("AutoLine", value); }

    public bool AutoPath { get => GetBool("AutoPath"); set => SetBool("AutoPath", value); }

    public bool DownDirection { get => GetBool("DownDirection"); set => SetBool("DownDirection", value); }

    public bool PrevPathEndDown { get => GetBool("PrevPathEndDown"); set => SetBool("PrevPathEndDown", value); }

    public bool NextPathStartDown { get => GetBool("NextPathStartDown"); set => SetBool("NextPathStartDown", value); }

    /// <summary>Engineering allowance in seconds (the file holds half minutes).</summary>
    public int EngAllowanceSeconds { get => GetAllowance("EngAllowance"); set => SetAllowance("EngAllowance", value); }

    public int PathAllowanceSeconds { get => GetAllowance("PathAllowance"); set => SetAllowance("PathAllowance", value); }

    public int PerfAllowanceSeconds { get => GetAllowance("PerfAllowance"); set => SetAllowance("PerfAllowance", value); }

    public int? DwellTime { get => GetInt("DwellTime"); set => SetInt("DwellTime", value); }

    public int? RequestPercent { get => GetInt("RequestPercent"); set => SetInt("RequestPercent", value); }

    public bool SetDownOnly { get => GetBool("SetDownOnly"); set => SetBool("SetDownOnly", value); }

    public int? SetDownKind { get => GetInt("SetDownKind"); set => SetInt("SetDownKind", value); }

    public bool AllowStopsOnThroughLines { get => GetBool("AllowStopsOnThroughLines"); set => SetBool("AllowStopsOnThroughLines", value); }

    public bool WaitForBookedTime { get => GetBool("WaitForBookedTime"); set => SetBool("WaitForBookedTime", value); }

    public bool BerthsHere { get => GetBool("BerthsHere"); set => SetBool("BerthsHere", value); }

    public bool CrewChange { get => GetBool("CrewChange"); set => SetBool("CrewChange", value); }

    public int? StopLocation { get => GetInt("StopLocation"); set => SetInt("StopLocation", value); }

    public int? StopAdjustment { get => GetInt("StopAdjustment"); set => SetInt("StopAdjustment", value); }

    public string? NewCategory { get => GetText("NewCategory"); set => SetText("NewCategory", value); }

    public XElement? ActivitiesElement => Element.Element("Activities");

    public IEnumerable<ActivityNode> Activities => ActivitiesElement?.Elements("Activity").Select(e => new ActivityNode(Document, e)) ?? [];

    internal override WttNode Wrap(XElement element) => new TripNode(Document, element);
}

/// <summary>An activity at a timing point (next working, join, divide, crew change…).</summary>
public sealed class ActivityNode(WttDocument document, XElement element) : WttNode(document, element)
{
    /// <summary>The activity code (<c>Activity</c>; 0 is next working, <c>04-…md</c> §1).</summary>
    public int? Code { get => GetInt("Activity"); set => SetInt("Activity", value); }

    public string? AssociatedTrain { get => GetText("AssociatedTrain"); set => SetText("AssociatedTrain", value); }

    public string? AssociatedUid { get => GetText("AssociatedUID"); set => SetText("AssociatedUID", value); }

    public int? ActivityDuration { get => GetInt("ActivityDuration"); set => SetInt("ActivityDuration", value); }

    public string? Decision { get => GetText("Decision"); set => SetText("Decision", value); }

    public string? Choice { get => GetText("Choice"); set => SetText("Choice", value); }

    public SimTime? CrewChangeManualTime { get => GetTime("CrewChangeManualTime"); set => SetTime("CrewChangeManualTime", value); }

    internal override WttNode Wrap(XElement element) => new ActivityNode(Document, element);
}

/// <summary>A timetable rule (<c>TimetableRule</c>): train X is held for, or excluded by, train Y.</summary>
public sealed class RuleNode(WttDocument document, XElement element) : WttNode(document, element)
{
    /// <summary>The rule number (<c>Rule</c>; 0 is "X appears after Y appears", <c>03-…md</c> §6).</summary>
    public int? Code { get => GetInt("Rule"); set => SetInt("Rule", value); }

    public string? Train { get => GetText("Train"); set => SetText("Train", value); }

    public string? TrainUid { get => GetText("TrainUID"); set => SetText("TrainUID", value); }

    public string? Train2 { get => GetText("Train2"); set => SetText("Train2", value); }

    public string? Train2Uid { get => GetText("Train2UID"); set => SetText("Train2UID", value); }

    /// <summary>Seconds after Y's event (S32).</summary>
    public int? Time { get => GetInt("Time"); set => SetInt("Time", value); }

    public string? Location { get => GetText("Location"); set => SetText("Location", value); }

    internal override WttNode Wrap(XElement element) => new RuleNode(Document, element);
}

/// <summary>A seed group (<c>SeedGroup</c> under <c>SeedGroups</c>): another time the sim can start at.</summary>
public sealed class SeedGroupNode(WttDocument document, XElement element) : WttNode(document, element)
{
    public string? Id { get => GetText("ID"); set => SetText("ID", value); }

    public SimTime? StartTime { get => GetTime("StartTime"); set => SetTime("StartTime", value); }

    public string? Title { get => GetText("Title"); set => SetText("Title", value); }

    internal override WttNode Wrap(XElement element) => new SeedGroupNode(Document, element);
}
