using System.Xml.Linq;
using Stringline.Core;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Validation;

/// <summary>How bad an issue is (<c>08-…md</c> §5).</summary>
public enum IssueSeverity
{
    /// <summary>Worth knowing; often fine.</summary>
    Info,

    /// <summary>Likely wrong.</summary>
    Warning,

    /// <summary>SimSig will refuse it or the train will fail.</summary>
    Error,
}

/// <summary>A fix for an issue: an edit the editor applies and undoes like any other.</summary>
public sealed record QuickFix(string Title, TimetableEdit Edit);

/// <summary>
/// One problem found in a timetable (<c>08-…md</c> §5). <see cref="Message"/> uses SimSig's Analyser wording where the
/// Analyser has the check, so users recognise it; <see cref="Hint"/> says what to do.
/// </summary>
/// <param name="Code">The check, one of <see cref="IssueCodes"/>.</param>
/// <param name="Train">The train, by its ID in the document's import, if the issue is about one.</param>
/// <param name="PointIndex">The timing point (<c>TimingPoint.Index</c>), if the issue is about one.</param>
public sealed record ValidationIssue(
    IssueSeverity Severity,
    string Code,
    TrainId? Train,
    int? PointIndex,
    string Message,
    string Hint,
    QuickFix? QuickFix = null)
{
    /// <summary>The train's element (its identity in the editor, kept through renumbering), if the issue is about one.</summary>
    public XElement? TrainElement { get; init; }

    /// <summary>The element the issue is about most closely: a trip, rule, category or the root.</summary>
    public XElement? Element { get; init; }

    /// <summary>The train's headcode, for display.</summary>
    public string? Headcode { get; init; }

    public override string ToString()
    {
        var where = Headcode is null ? string.Empty : PointIndex is { } i ? $"{Headcode} [{i}]: " : Headcode + ": ";
        return $"{Severity} {Code}: {where}{Message}";
    }
}

/// <summary>The checks, by code. Each has a severity and a hint in <see cref="IssueCatalogue"/>.</summary>
public static class IssueCodes
{
    // The document
    public const string NoAreaId = "no-area-id";
    public const string DuplicateCategory = "duplicate-category";
    public const string UnknownCategory = "unknown-category";
    public const string UnknownSeedGroup = "unknown-seed-group";
    public const string NoHeadcode = "no-headcode";

    // Times (the Analyser's)
    public const string DepartureBeforeArrival = "departure-before-arrival";
    public const string ArrivalBeforePrevious = "arrival-before-previous";
    public const string DepartureBeforePrevious = "departure-before-previous";
    public const string ReversesWithPassTime = "reverses-with-pass-time";
    public const string OutOfOrder = "out-of-order";

    // Links (the Analyser's)
    public const string AssociatedNotDefined = "associated-not-defined";
    public const string AssociatedDifferentLocation = "associated-different-location";
    public const string AssociatedDifferentPlatform = "associated-different-platform";
    public const string NextAlreadyUsed = "next-already-used";
    public const string DividedPortionAlreadyUsed = "divided-portion-already-used";
    public const string NextNotFinal = "next-not-final";
    public const string UnknownActivity = "unknown-activity";
    public const string JoiningTrainLost = "joining-train-lost";
    public const string NeverUsed = "never-used";
    public const string NeverRuns = "never-runs";

    // Endings (the Analyser's)
    public const string TerminatesWithNoOnward = "terminates-no-onward";
    public const string NoFurtherActionAfterJoining = "no-action-after-joining";

    // Lengths and traction (the Analyser's)
    public const string TooShort = "too-short";
    public const string ShorterThanDividedPortion = "shorter-than-portion";
    public const string RemainderTooShort = "remainder-too-short";
    public const string LengthDiffersFromNext = "length-differs-from-next";
    public const string NoTractionPower = "no-traction-power";

    // Seeding and trips (the Analyser's)
    public const string SeedArrivalTime = "seed-arrival-time";
    public const string SeedSignalShared = "seed-signal-shared";
    public const string NoTrips = "no-trips";

    // Sim knowledge: never seen in the sim's other timetables and play
    public const string UnknownLocation = "unknown-location";
    public const string InvalidPlatform = "invalid-platform";
    public const string InvalidLine = "invalid-line";
    public const string InvalidPath = "invalid-path";
    public const string NoKnownPath = "no-known-path";
    public const string AmbiguousLine = "ambiguous-line";
    public const string RunTooShort = "run-too-short";
    public const string StopsAtNonStation = "stops-at-non-station";
    public const string UnknownEntryPoint = "unknown-entry-point";
    public const string UnknownSeedPoint = "unknown-seed-point";
    public const string NotSafeForActivities = "not-safe-for-activities";
    public const string WrongTractionPower = "wrong-traction-power";
    public const string NotSensibleEnding = "not-sensible-ending";
    public const string ExitsAndReenters = "exits-and-reenters";

    // Rules
    public const string RuleUnresolved = "rule-unresolved";
    public const string RuleUnknown = "rule-unknown";
    public const string RuleCycle = "rule-cycle";
    public const string RuleLocationNotCalled = "rule-location-not-called";

    // A test run in SimSig (S48): from a recording of the timetable played
    public const string TestNeverAppeared = "test-never-appeared";
    public const string TestUnmatched = "test-unmatched";
    public const string TestSlowSection = "test-slow-section";
    public const string TestPlatformClash = "test-platform-clash";
}

/// <summary>Each check's severity, title and hint (the Analyser's advice, from the SimSig wiki's Timetable Tools 1 page, where it has the check).</summary>
public static class IssueCatalogue
{
    public sealed record Entry(IssueSeverity Severity, string Title, string Hint, bool FromAnalyser = false, bool FromKnowledge = false, bool BlocksSave = false);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal)
    {
        [IssueCodes.NoAreaId] = new(IssueSeverity.Error, "No sim ID", "The root element needs the sim's ID (e.g. waterloo); SimSig cannot tie the timetable to a sim without it.", BlocksSave: true),
        [IssueCodes.DuplicateCategory] = new(IssueSeverity.Warning, "Category defined twice", "Give each train category its own ID; SimSig uses the first."),
        [IssueCodes.UnknownCategory] = new(IssueSeverity.Warning, "Unknown category", "Choose one of the timetable's categories, or add this one."),
        [IssueCodes.UnknownSeedGroup] = new(IssueSeverity.Warning, "Unknown seed group", "Choose one of the timetable's seed groups, or clear it to seed at the timetable's start."),
        [IssueCodes.NoHeadcode] = new(IssueSeverity.Error, "No headcode", "Give the train a headcode (ID)."),

        [IssueCodes.DepartureBeforeArrival] = new(IssueSeverity.Error, "Departure earlier than arrival", "Correct the times. Times after midnight go on past 23:59 (24:01).", FromAnalyser: true),
        [IssueCodes.ArrivalBeforePrevious] = new(IssueSeverity.Error, "Arrival earlier than previous", "Correct the times. Times after midnight go on past 23:59 (24:01).", FromAnalyser: true),
        [IssueCodes.DepartureBeforePrevious] = new(IssueSeverity.Error, "Dep/pass earlier than previous", "Correct the times. Times after midnight go on past 23:59 (24:01).", FromAnalyser: true),
        [IssueCodes.ReversesWithPassTime] = new(IssueSeverity.Error, "Reverses on a pass", "A train cannot reverse without stopping: untick \"passing time\".", FromAnalyser: true),
        [IssueCodes.OutOfOrder] = new(IssueSeverity.Info, "Location out of order", "Other timetables book this location between the two before it; put the locations in the order the train passes them.", FromAnalyser: true, FromKnowledge: true),

        [IssueCodes.AssociatedNotDefined] = new(IssueSeverity.Error, "Associated train not defined", "Add the associated train, or name one that is in the timetable.", FromAnalyser: true),
        [IssueCodes.AssociatedDifferentLocation] = new(IssueSeverity.Error, "Associated train elsewhere", "Make the associated train start (or call) where the activity is.", FromAnalyser: true),
        [IssueCodes.AssociatedDifferentPlatform] = new(IssueSeverity.Warning, "Associated train on another platform", "Use the same platform as the associated train; the train can still run, but the player is penalised.", FromAnalyser: true),
        [IssueCodes.NextAlreadyUsed] = new(IssueSeverity.Error, "Next train already used", "Another train already names this one as its next working; give each its own.", FromAnalyser: true),
        [IssueCodes.DividedPortionAlreadyUsed] = new(IssueSeverity.Error, "Divided portion already used", "Another train already forms this portion; remove the duplicate.", FromAnalyser: true),
        [IssueCodes.NextNotFinal] = new(IssueSeverity.Error, "Next not the final activity", "Make the next working the train's last activity at its last location.", FromAnalyser: true),
        [IssueCodes.UnknownActivity] = new(IssueSeverity.Warning, "Unknown activity", "Choose one of SimSig's activities.", FromAnalyser: true),
        [IssueCodes.JoiningTrainLost] = new(IssueSeverity.Error, "Joining train got lost", "The train it joins never runs; fix that train's timetable.", FromAnalyser: true),
        [IssueCodes.NeverUsed] = new(IssueSeverity.Warning, "Never used", "Give the train an entry point or seed point, or have another train form it.", FromAnalyser: true),
        [IssueCodes.NeverRuns] = new(IssueSeverity.Warning, "Never runs", "The train that forms it never runs; fix that train.", FromAnalyser: true),

        [IssueCodes.TerminatesWithNoOnward] = new(IssueSeverity.Info, "Terminates with no onward working", "Add a next working or a join, or tick \"berths here\" if it stays.", FromAnalyser: true),
        [IssueCodes.NoFurtherActionAfterJoining] = new(IssueSeverity.Warning, "No further action after joining", "Add the joined train's onward movement or next working.", FromAnalyser: true),

        [IssueCodes.TooShort] = new(IssueSeverity.Error, "Shorter than the minimum length", "Trains must be at least 8 m long.", FromAnalyser: true),
        [IssueCodes.ShorterThanDividedPortion] = new(IssueSeverity.Warning, "Shorter than the divided portion", "The train must be longer than the portion it divides into.", FromAnalyser: true),
        [IssueCodes.RemainderTooShort] = new(IssueSeverity.Warning, "Remainder too short after dividing", "What stays after dividing must be at least 8 m long.", FromAnalyser: true),
        [IssueCodes.LengthDiffersFromNext] = new(IssueSeverity.Info, "Length differs from the next working", "Give the train and its next working the same length.", FromAnalyser: true),
        [IssueCodes.NoTractionPower] = new(IssueSeverity.Warning, "No traction power", "Tick at least one power type (diesel, overhead, third rail…) for the train or its category.", FromAnalyser: true),

        [IssueCodes.SeedArrivalTime] = new(IssueSeverity.Info, "Arrival time at the seed location", "A seeded train starts at its first location: leave its arrival time empty.", FromAnalyser: true),
        [IssueCodes.SeedSignalShared] = new(IssueSeverity.Error, "Two trains seeded at one signal", "Seed each train in a seed group at its own signal.", FromAnalyser: true),
        [IssueCodes.NoTrips] = new(IssueSeverity.Error, "No trips defined", "A train needs at least two locations, or an entry point and one location.", FromAnalyser: true),

        [IssueCodes.UnknownLocation] = new(IssueSeverity.Warning, "Location never seen", "Check the TIPLOC: no timetable or recording of this sim has it.", FromKnowledge: true),
        [IssueCodes.InvalidPlatform] = new(IssueSeverity.Warning, "Platform never seen", "Check the platform: no timetable or recording of this sim uses it here (SimSig: \"Invalid platform\").", FromAnalyser: true, FromKnowledge: true),
        [IssueCodes.InvalidLine] = new(IssueSeverity.Warning, "Line never seen", "Check the line code: no timetable of this sim uses it out of here (SimSig: \"Invalid line code\").", FromAnalyser: true, FromKnowledge: true),
        [IssueCodes.InvalidPath] = new(IssueSeverity.Warning, "Path never seen", "Check the path code: no timetable of this sim uses it into here (SimSig: \"Invalid path code\").", FromAnalyser: true, FromKnowledge: true),
        [IssueCodes.NoKnownPath] = new(IssueSeverity.Warning, "No known path", "No timetable of this sim books these two straight after each other; SimSig may say \"no path between timing points\". Insert the timing points other timetables book between them.", FromAnalyser: true, FromKnowledge: true),
        [IssueCodes.AmbiguousLine] = new(IssueSeverity.Info, "Ambiguous line", "Other timetables use several lines from here to the next location; set the one this train should take.", FromKnowledge: true),
        [IssueCodes.RunTooShort] = new(IssueSeverity.Warning, "Run shorter than any seen", "Other timetables never book trains of this speed this quickly; allow more time.", FromKnowledge: true),
        [IssueCodes.StopsAtNonStation] = new(IssueSeverity.Warning, "Stops at a non-station", "No timetable of this sim stops here; make it a pass, or stop at a station.", FromAnalyser: true, FromKnowledge: true),
        [IssueCodes.UnknownEntryPoint] = new(IssueSeverity.Warning, "Entry point never seen", "No timetable of this sim enters trains here; check the entry point.", FromKnowledge: true),
        [IssueCodes.UnknownSeedPoint] = new(IssueSeverity.Warning, "Seed point not a known signal", "Neither the sim's timetables nor its recordings and profile know this signal; check the seed point.", FromKnowledge: true),
        [IssueCodes.NotSafeForActivities] = new(IssueSeverity.Warning, "Not safe to have activities", "No timetable of this sim has activities here, so it may not be a key location; move the activity.", FromAnalyser: true, FromKnowledge: true),
        [IssueCodes.WrongTractionPower] = new(IssueSeverity.Info, "Traction power never seen here", "No train of this power type enters here in the sim's timetables; check the entry point or the power types.", FromAnalyser: true, FromKnowledge: true),
        [IssueCodes.NotSensibleEnding] = new(IssueSeverity.Info, "Not a sensible ending point", "No timetable of this sim ends a train here; end it where trains form a new working or are stabled.", FromAnalyser: true, FromKnowledge: true),
        [IssueCodes.ExitsAndReenters] = new(IssueSeverity.Info, "Exits and re-enters", "Trains leave the area here; split the train, with a rule so the second part enters after the first has gone.", FromAnalyser: true, FromKnowledge: true),

        [IssueCodes.RuleUnresolved] = new(IssueSeverity.Info, "Rule names no single train", "Name a train that is in the timetable once (by UID where the file uses them)."),
        [IssueCodes.RuleUnknown] = new(IssueSeverity.Warning, "Unknown rule", "Choose one of SimSig's rules."),
        [IssueCodes.RuleCycle] = new(IssueSeverity.Warning, "Rules wait for each other", "These rules wait for each other in a loop, so the trains may never go; remove one."),
        [IssueCodes.TestNeverAppeared] = new(IssueSeverity.Warning, "Never appeared in the test", "SimSig did not bring the train on in the test run: check its entry point, seeding, rules and as-required setting, and that the sim was started early enough."),
        [IssueCodes.TestUnmatched] = new(IssueSeverity.Warning, "Ran but did not match", "A train ran in the test that Stringline could not match to the timetable: often a path, link or headcode SimSig would not take as booked."),
        [IssueCodes.TestSlowSection] = new(IssueSeverity.Warning, "Always slower than booked", "Every train timed over this section in the test ran it slower than booked; the quick fix moves the train's later times by the time it took."),
        [IssueCodes.TestPlatformClash] = new(IssueSeverity.Warning, "Platform shared in the test", "Two trains stood in one platform at once in the test run; move one to another platform or time."),
        [IssueCodes.RuleLocationNotCalled] = new(IssueSeverity.Warning, "Rule location not called at", "The rule's train does not call at the rule's location; change the location or the train."),
    };

    public static IReadOnlyCollection<string> Codes => Entries.Keys;

    public static Entry Of(string code) =>
        Entries.TryGetValue(code, out var entry) ? entry : throw new ArgumentException($"No check '{code}'.", nameof(code));
}
