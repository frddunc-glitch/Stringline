using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Editing.Drafts;
using Stringline.Editing.Knowledge;
using Stringline.Planning;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Editing.Tests;

/// <summary>
/// The minimum run a drag on the draft board stops at (S44, Fred): the sim's least known run for the train's band,
/// link by link, else the direct link's, else the original timetable's least booked run.
/// </summary>
public sealed class DraftMinRunsTests
{
    private static readonly RunBand Band90 = new(SpeedBand.UpTo90, false);

    private static LinkKnowledge Link(string from, string to, int least) =>
        new(new Tiploc(from), new Tiploc(to), 1, RunTimes: new Dictionary<RunBand, TimeStats> { [Band90] = new(1, least, least) });

    private static SimKnowledge Knowledge(params LinkKnowledge[] links) => new() { AreaId = "test", Links = links };

    /// <summary>A 90 mph train A–B–C–D, booked 4 min a link; the original booked B–C in 3 min.</summary>
    private static (WorkingTimetable Draft, MinRunTable Original, TrainPlan Plan) Setup()
    {
        var draft = Of(Train("1A01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08"), Pass("D", "08:12")]) with { MaxSpeedMph = 85 });
        var original = Of(Train("1A01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:07"), Pass("D", "08:12")]));
        return (draft, MinRunTable.Build(original), WorkingPlan.FromTimetable(draft).Get(new TrainId("1A01")));
    }

    [Fact]
    public void The_sims_least_run_for_the_band_comes_first_link_by_link()
    {
        var (draft, original, plan) = Setup();
        var runs = DraftMinRuns.For(Knowledge(Link("A", "B", 150), Link("B", "C", 100)), original, draft);

        runs(plan, 0, 2).Should().Be(250);
        runs(plan, 0, 1).Should().Be(150);
    }

    [Fact]
    public void An_unknown_link_takes_the_direct_link_else_the_original_timetable()
    {
        var (draft, original, plan) = Setup();

        DraftMinRuns.For(Knowledge(Link("A", "B", 150), Link("A", "C", 200)), original, draft)(plan, 0, 2).Should().Be(200, "B–C is unknown, A–C is known");
        DraftMinRuns.For(Knowledge(Link("A", "B", 150)), original, draft)(plan, 1, 2).Should().Be(180, "the original booked B–C in 3 min");
        DraftMinRuns.For(null, original, draft)(plan, 1, 3).Should().Be(480, "without knowledge: B–C 3 min and C–D 5 min as the original booked");
        DraftMinRuns.For(null, original, draft)(plan, 2, 1).Should().BeNull();
    }
}
