using Stringline.Core;
using Stringline.Planning;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>
/// #12: what is under the mouse, in order (a conflict's ring, a note, a train line, a conflict's highlight, a block or
/// restriction, a label), and the tooltip for each, the same in both orientations.
/// </summary>
public sealed class GraphTargetTests
{
    private const double Third = 1.0 / 3, TwoThirds = 2.0 / 3;

    /// <summary>2B02 too close behind 1A01 entering at C: ring at C 07:12, highlight over B–C 07:08–07:16.</summary>
    private static readonly Conflict Headway = new("H:2B02:1A01:C", ConflictKind.Headway, new TrainId("2B02"), new TrainId("1A01"), new Tiploc("C"),
        SimTime.Parse("07:12"), 150)
    {
        HeadcodeA = new Headcode("2B02"),
        HeadcodeB = new Headcode("1A01"),
        To = new Tiploc("B"),
        Start = SimTime.Parse("07:08"),
        End = SimTime.Parse("07:16"),
    };

    /// <summary>A block over A–C, 07:00–07:20.</summary>
    private static readonly Disruption Block = new("B1", DisruptionKind.Block, new Tiploc("C"), new Tiploc("A"), null, SimTime.Parse("07:00"),
        SimTime.Parse("07:20"), Note: "Engineering");

    private static readonly PlanNote Note = new("N1", new Tiploc("B"), SimTime.Parse("07:10"), "Relief driver");

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    private static WorkingPlan Plan()
    {
        var (plan, results) = WorkingPlan.FromTimetable(TwoTrains()).ApplyAll([new AddDisruption(Block), new AddNote(Note)]);
        results.Should().OnlyContain(r => r.Applied);
        return plan;
    }

    private static GraphScene Scene() => GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor)
    {
        Timetable = TwoTrains(),
        Plan = Plan(),
        Now = SimTime.Parse("07:00"),
        Conflicts = new ConflictSet([Headway]),
        ShowConflicts = true,
        ShowDisruptions = true,
    });

    private static GraphViewport View(GraphOrientation orientation) =>
        GraphViewport.Create(T("06:45"), T("07:30"), 800, 400, orientation: orientation);

    private static GraphTarget? At(GraphOrientation orientation, string time, double axis, bool indexed = false)
    {
        var scene = Scene();
        var view = View(orientation);
        return GraphHitTester.TargetAt(scene, view, view.ToScreen(T(time), axis), indexed ? GraphHitIndex.Build(scene, view) : null);
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal, false)]
    [InlineData(GraphOrientation.Vertical, false)]
    [InlineData(GraphOrientation.Horizontal, true)]
    [InlineData(GraphOrientation.Vertical, true)]
    public void Each_thing_is_found_in_order(GraphOrientation orientation, bool indexed)
    {
        // 2B02 stands at C 07:10–07:12, under the ring: the ring comes first.
        At(orientation, "07:12", TwoThirds, indexed)!.Kind.Should().Be(GraphTargetKind.ConflictRing);
        At(orientation, "07:10", Third, indexed)!.Note!.Id.Should().Be("N1");
        // 1A01 at 07:06 is halfway, inside the block: the line comes before the block.
        At(orientation, "07:06", 0.5, indexed)!.Hit!.Path.Headcode.Value.Should().Be("1A01");
        // Inside both the headway's highlight and the block, away from any line: the conflict.
        var highlight = At(orientation, "07:15", 0.55, indexed)!;
        (highlight.Kind, highlight.Conflict!.Id).Should().Be((GraphTargetKind.Conflict, "H:2B02:1A01:C"));
        At(orientation, "07:02", 0.5, indexed)!.Disruption!.Id.Should().Be("B1");
        At(orientation, "07:25", 0.5, indexed).Should().BeNull("nothing is there");
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void A_line_inside_a_highlight_and_a_block_names_them(GraphOrientation orientation)
    {
        // 2B02 runs C–B 07:12–07:16, through the headway's highlight and the block: halfway at 07:14.
        var target = At(orientation, "07:14", 0.5)!;

        (target.Kind, target.Hit!.Path.Headcode.Value, target.Conflict!.Id, target.Disruption!.Id).Should().Be((GraphTargetKind.Path, "2B02", Headway.Id, "B1"));
        var info = GraphHover.Describe(target, Scene(), PlanSnapshot.Empty);
        info.Heading.Should().Be("2B02");
        info.ValueOf(GraphHover.Conflict).Should().Be("Headway: 2B02 too close behind 1A01");
        info.ValueOf(GraphHover.Disruption).Should().Be("Block B1, 07:00:00 – 07:20:00");

        var outside = GraphHover.Describe(At(orientation, "07:18:30", 1.0 / 6)!, Scene(), PlanSnapshot.Empty);
        (outside.Heading, outside.ValueOf(GraphHover.Conflict), outside.ValueOf(GraphHover.Disruption)).Should().Be(("2B02", null, "Block B1, 07:00:00 – 07:20:00"));
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void A_label_in_the_margin_is_found_last(GraphOrientation orientation)
    {
        var scene = Scene();
        var view = View(orientation);
        var along = view.AxisToPixel(TwoThirds);
        var at = orientation == GraphOrientation.Vertical ? new ScreenPoint(along, view.PlotTop - 20) : new ScreenPoint(view.PlotLeft - 20, along);

        GraphHitTester.TargetAt(scene, view, at)!.Location!.Tiploc.Code.Should().Be("C");
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void The_tooltip_is_the_same_in_both_orientations(GraphOrientation orientation)
    {
        var scene = Scene();
        string? Suggest(Conflict c) => c.Id == Headway.Id ? "Hold 2B02 at Charlie until 07:15 (+3 min)" : null;
        string Describe(string time, double axis) =>
            GraphHover.Describe(At(orientation, time, axis)!, scene, PlanSnapshot.Empty, Suggest).ToString();

        Describe("07:12", TwoThirds).Should().Be(
            "Headway: 2B02 too close behind 1A01" + Environment.NewLine +
            "Trains: 2B02 gives way to 1A01" + Environment.NewLine +
            "Where: Charlie – B" + Environment.NewLine +
            "When: 07:12:00 (clash 07:08–07:16)" + Environment.NewLine +
            "Resolve: delay 2B02 2 min 30 s" + Environment.NewLine +
            "Suggest: Hold 2B02 at Charlie until 07:15 (+3 min)");
        Describe("07:15", 0.55).Should().Be(Describe("07:12", TwoThirds), "the highlight says what the ring says");
        Describe("07:02", 0.5).Should().StartWith("Block B1" + Environment.NewLine + "Where: Charlie – Alpha").And.EndWith("Tip: Right-click to edit or remove it");
        Describe("07:10", Third).Should().StartWith("Note N1");
        Describe("07:06", 0.5).Should().StartWith("1A01");
    }
}
