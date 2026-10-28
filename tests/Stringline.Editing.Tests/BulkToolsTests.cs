using System.Xml.Linq;
using Stringline.Core;
using Stringline.Editing.Bulk;
using Stringline.Editing.Knowledge;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;
using static Stringline.Editing.Tests.Draft;

namespace Stringline.Editing.Tests;

/// <summary>S46: the bulk tools, each one edit with a list of changes, undone to the same tree.</summary>
public sealed class BulkToolsTests
{
    private static TrainNode Of(WttDocument d, string headcode) => d.Trains.Single(t => t.Headcode == headcode);

    /// <summary>Applies the result's edit, checks it, undoes it to the same tree, and applies it again.</summary>
    private static void ApplyUndo(WttDocument document, BulkResult result, Action applied)
    {
        result.Edit.Should().NotBeNull(string.Join("; ", result.Problems));
        var before = new XElement(document.Root).ToString(SaveOptions.DisableFormatting);
        var undo = document.Apply(result.Edit!);
        applied();
        document.Apply(undo);
        document.Root.ToString(SaveOptions.DisableFormatting).Should().Be(before);
        document.Apply(result.Edit!);
    }

    /// <summary>2A01 A→B→C and 2A02 C→B→A, both with a crew change at B.</summary>
    private static WttDocument Line() => Doc(
        Category("C1", new XElement("MaxSpeed", 75)),
        Category("C2", new XElement("MaxSpeed", 100)),
        Train("2A01", Field("Category", "C1"), Field("UID", "U101"), Trips(
            Stop("A", null, "10:00", new XElement("Platform", "1")),
            Pass("B", "10:05", Activities(Activity(10, null, new XElement("CrewChangeManualTime", T("10:05"))))),
            Stop("C", "10:10", null, new XElement("Platform", "2")))),
        Train("2A02", Field("Category", "C1"), Trips(
            Stop("C", null, "10:30", new XElement("Platform", "2")),
            Pass("B", "10:35"),
            Stop("A", "10:40", null, new XElement("Platform", "1")))));

    [Fact]
    public void Shift_moves_all_or_from_a_location_on_entry_and_crew_change_too_and_refuses_odd_seconds()
    {
        var d = Line();
        var a = Of(d, "2A01");
        var entry = a.DepartTime!.Value;
        ApplyUndo(d, BulkTools.Shift([a], 150), () =>
        {
            a.DepartTime.Should().Be(entry + 150);
            a.Trips.Select(t => t.DepPassTime ?? t.ArrTime).Should().Equal(new SimTime(T("10:02:30")), new SimTime(T("10:07:30")), new SimTime(T("10:12:30")));
            a.Trips.ElementAt(1).Activities.Single().CrewChangeManualTime.Should().Be(new SimTime(T("10:07:30")));
        });

        var d2 = Line();
        var b = Of(d2, "2A01");
        ApplyUndo(d2, BulkTools.Shift([b], -60, from: "B"), () =>
        {
            b.DepartTime.Should().Be(entry, "it does not move the whole train");
            b.Trips.Select(t => t.DepPassTime ?? t.ArrTime).Should().Equal(new SimTime(T("10:00")), new SimTime(T("10:04")), new SimTime(T("10:09")));
        });

        BulkTools.Shift([b], 60, "ZZZ").Problems.Should().ContainSingle().Which.Should().Contain("ZZZ");
        FluentActions.Invoking(() => BulkTools.Shift([b], 45)).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("2A01", 2, 1, "2A03")]
    [InlineData("2A01", 2, 3, "2A07")]
    [InlineData("1Z95", 1, 4, "1Z99")]
    [InlineData("U101", 1, 1, "U102")]
    public void Headcodes_count_on_keeping_their_width(string from, int step, int steps, string expected) =>
        BulkTools.NextHeadcode(from, step, steps).Should().Be(expected);

    [Fact]
    public void Headcodes_that_run_out_of_numbers_are_refused() =>
        FluentActions.Invoking(() => BulkTools.NextHeadcode("1A99", 1)).Should().Throw<InvalidOperationException>().WithMessage("*runs out*");

    [Fact]
    public void Renumber_category_codes_allowances_and_find_and_replace_keep_links()
    {
        var d = Line();
        d.Apply(TimetableParts.AddActivity(Of(d, "2A01").Trips.Last(), TimetableParts.NewActivity(d, Core.Timetables.ActivityKind.Next, Of(d, "2A02"))));
        var both = d.Trains.ToList();

        ApplyUndo(d, BulkTools.Renumber(d, both, "2B10", 2), () =>
        {
            d.Trains.Select(t => t.Headcode).Should().Equal("2B10", "2B12");
            Of(d, "2B10").Trips.Last().Activities.Single().AssociatedTrain.Should().Be("2B12", "the link follows the renumbered train");
        });

        ApplyUndo(d, BulkTools.SetCategory(d, both, "C2"), () => d.Trains.Should().OnlyContain(t => t.Category == "C2"));
        FluentActions.Invoking(() => BulkTools.SetCategory(d, both, "NONE")).Should().Throw<InvalidOperationException>();

        ApplyUndo(d, BulkTools.SetAtLocation(both, "A", TripCode.Platform, "3"), () =>
            d.Trains.Select(t => t.Trips.Single(p => p.Location == "A").Platform).Should().Equal("3", "3"));
        ApplyUndo(d, BulkTools.SetAllowance(both, AllowanceKind.Performance, 90, "B"), () =>
            d.Trains.Select(t => t.Trips.Single(p => p.Location == "B").PerfAllowanceSeconds).Should().Equal(90, 90));
        ApplyUndo(d, BulkTools.SetAllowance(both, AllowanceKind.Performance, 0), () =>
            d.Trains.SelectMany(t => t.Trips).Should().OnlyContain(p => p.PerfAllowanceSeconds == 0));

        var replaced = BulkTools.FindReplace(d, both, "b", "BB", ReplaceIn.Codes);
        replaced.Changes.Should().HaveCount(2);
        ApplyUndo(d, replaced, () => d.Trains.Should().OnlyContain(t => t.Trips.ElementAt(1).Location == "BB"));
        ApplyUndo(d, BulkTools.FindReplace(d, both, "2B12", "2C01", ReplaceIn.Headcodes), () =>
            d.Trains.First().Trips.Last().Activities.Single().AssociatedTrain.Should().Be("2C01"));
    }

    [Fact]
    public void Repeat_as_pattern_copies_every_n_minutes_counting_headcodes_and_UIDs_and_leaves_links_off()
    {
        var d = Line();
        var a = Of(d, "2A01");
        d.Apply(TimetableParts.AddActivity(a.Trips.Last(), TimetableParts.NewActivity(d, Core.Timetables.ActivityKind.Next, Of(d, "2A02"))));
        var result = BulkTools.RepeatPattern(d, a, 30, new SimTime(T("11:00")), 2);

        result.Changes.Should().HaveCount(2, "10:30 and 11:00 (the copies start by 11:00)");
        ApplyUndo(d, result, () =>
        {
            d.Trains.Select(t => (t.Headcode, t.Uid)).Should().Equal(("2A01", "U101"), ("2A03", "U103"), ("2A05", "U105"), ("2A02", null));
            var copy = Of(d, "2A05");
            copy.Trips.First().DepPassTime.Should().Be(new SimTime(T("11:00")));
            copy.Trips.Last().ActivitiesElement.Should().BeNull("the next working is not copied");
            copy.Trips.ElementAt(1).Activities.Single().CrewChangeManualTime.Should().Be(new SimTime(T("11:05")), "crew changes go with it");
        });

        BulkTools.RepeatPattern(d, a, 30, new SimTime(T("10:10")), 2).Edit.Should().BeNull();
    }

    [Fact]
    public void Next_workings_are_paired_by_the_least_turnaround_that_meets_the_dwell()
    {
        var d = Doc(
            Train("1U01", Trips(Stop("A", null, "09:00"), Stop("T", "09:30", null))),
            Train("1U03", Trips(Stop("A", null, "09:20"), Stop("T", "09:50", null))),
            Train("1D02", Trips(Stop("T", null, "09:40"), Stop("A", "10:10", null))),
            Train("1D04", Trips(Stop("T", null, "09:52"), Stop("A", "10:22", null))),
            Train("1D06", Trips(Stop("T", null, "10:05"), Stop("A", "10:35", null))));
        var trains = d.Trains.ToList();

        var paired = BulkTools.PairNextWorkings(d, trains, trains, "T", 600);

        paired.Changes.Should().HaveCount(2);
        ApplyUndo(d, paired, () =>
        {
            Of(d, "1U01").Trips.Last().Activities.Single().AssociatedTrain.Should().Be("1D02", "09:30 + 10 min: the 09:40 is the first that meets it");
            Of(d, "1U03").Trips.Last().Activities.Single().AssociatedTrain.Should().Be("1D06", "09:50 + 10 min: the 09:52 is too soon");
        });
    }

    [Fact]
    public void Auto_fill_shares_a_run_by_the_sims_least_runs_times_on_from_the_last_and_reports_a_shortfall()
    {
        var builder = new SimKnowledgeBuilder("test");
        builder.AddTimetable(Doc(
            Category("C1", new XElement("MaxSpeed", 75)),
            Train("1K01", Field("Category", "C1"), Trips(Stop("A", null, "08:00"), Pass("B", "08:02"), Pass("C", "08:08"), Stop("D", "08:10", "08:11", new XElement("Platform", "1")), Stop("E", "08:15", null))))
            .ToWorkingTimetable());
        var knowledge = builder.Build();

        var d = Doc(
            Category("C1", new XElement("MaxSpeed", 75)),
            Train("2K01", Field("Category", "C1"), Trips(Stop("A", null, "09:00"), Stop("B", null, null), Stop("C", null, null), Stop("D", "09:10", "09:11", new XElement("Platform", "1")),
                new XElement("Trip", new XElement("Location", "E")))));
        var result = BulkTools.AutoFill(d, knowledge, Of(d, "2K01"));

        result.Problems.Should().BeEmpty();
        ApplyUndo(d, result, () =>
        {
            var trips = Of(d, "2K01").Trips.ToList();
            (trips[1].DepPassTime, trips[1].IsPassTime).Should().Be((new SimTime(T("09:02")), true), "2 of the 10 min, as the sim runs A–B in 2 of A–D's 10");
            trips[2].DepPassTime.Should().Be(new SimTime(T("09:08")));
            trips[4].ArrTime.Should().Be(new SimTime(T("09:15")), "D to E is 4 min on from 09:11");
        });

        var tight = Doc(
            Category("C1", new XElement("MaxSpeed", 75)),
            Train("2K03", Field("Category", "C1"), Trips(Stop("A", null, "09:00"), Stop("B", null, null), Stop("C", "09:05", null))));
        BulkTools.AutoFill(tight, knowledge, Of(tight, "2K03")).Problems.Should().ContainSingle().Which.Should().Contain("3 min short");
    }

    /// <summary>1K01 books every location A–F; 1K02 runs A, D, F only, so A–D and D–F are skip chains.</summary>
    private static SimKnowledge AToF(params XElement[] more)
    {
        var builder = new SimKnowledgeBuilder("test");
        builder.AddTimetable(Doc(
            Category("C1", new XElement("MaxSpeed", 75)),
            Train("1K01", Field("Category", "C1"), Trips(Stop("A", null, "08:00"), Pass("B", "08:02"), Pass("C", "08:08"), Pass("D", "08:10"), Pass("E", "08:13"), Stop("F", "08:15", null))),
            Train("1K02", Field("Category", "C1"), Trips(Stop("A", null, "09:00"), Pass("D", "09:10"), Stop("F", "09:15", null))),
            more).ToWorkingTimetable());
        return builder.Build();
    }

    [Fact]
    public void Filling_in_timing_points_inserts_every_location_other_timetables_book_in_order_timed_by_the_least_runs()
    {
        var knowledge = AToF();
        var d = Doc(
            Category("C1", new XElement("MaxSpeed", 75)),
            Train("2K01", Field("Category", "C1"), Trips(Stop("A", null, "10:00"), Pass("D", "10:20"), Stop("F", "10:30", null))));

        var result = BulkTools.FillTimingPoints(d, knowledge, Of(d, "2K01"));

        result.Problems.Should().BeEmpty();
        result.Changes.Should().HaveCount(3);
        ApplyUndo(d, result, () =>
        {
            var trips = Of(d, "2K01").Trips.ToList();
            trips.Select(t => t.Location).Should().Equal("A", "B", "C", "D", "E", "F");
            trips.Where(t => t.Location is "B" or "C" or "E").Should().AllSatisfy(t => t.IsPassTime.Should().BeTrue());

            // A–D: least runs 2, 6 and 2 min leave 10 min over, shared 2:6:2. D–F: 3 and 2 leave 5, shared 3:2.
            trips[1].DepPassTime.Should().Be(new SimTime(T("10:04")));
            trips[2].DepPassTime.Should().Be(new SimTime(T("10:16")));
            trips[4].DepPassTime.Should().Be(new SimTime(T("10:26")));
        });

        BulkTools.FillTimingPoints(d, knowledge, Of(d, "2K01")).Edit.Should().BeNull("every location is booked now");
    }

    [Fact]
    public void Filling_in_timing_points_keeps_to_links_booked_the_trains_way_and_says_where_the_booking_is_too_quick()
    {
        // The chain C–B–A is learned from trains going the other way only: a train A to C is left as it is.
        var builder = new SimKnowledgeBuilder("test");
        builder.AddTimetable(Doc(
            Train("1U01", Trips(Stop("C", null, "08:00"), Pass("B", "08:05"), Stop("A", "08:10", null))),
            Train("1U02", Trips(Stop("C", null, "09:00"), Stop("A", "09:10", null))),
            Train("1D01", Trips(Stop("A", null, "09:30"), Stop("C", "09:40", null)))).ToWorkingTimetable());
        var reverse = Doc(Train("2D01", Trips(Stop("A", null, "10:00"), Stop("C", "10:10", null))));

        BulkTools.FillTimingPoints(reverse, builder.Build(), Of(reverse, "2D01")).Edit.Should().BeNull();

        // Booked A–D in 6 min where trains take 10 through B and C: still filled, in proportion, and said.
        var d = Doc(
            Category("C1", new XElement("MaxSpeed", 75)),
            Train("2K02", Field("Category", "C1"), Trips(Stop("A", null, "10:00"), Stop("D", "10:06", null))));
        var result = BulkTools.FillTimingPoints(d, AToF(), Of(d, "2K02"));

        result.Problems.Should().ContainSingle().Which.Should().Contain("A to D is booked 4 min quicker");
        ApplyUndo(d, result, () => Of(d, "2K02").Trips.Select(t => t.Location).Should().Equal("A", "B", "C", "D"));
    }

    [Fact]
    public void Auto_fill_on_Waterloo_lies_within_the_timetables_own_run_times()
    {
        var waterloo = WttDocument.Load(Fixtures.Path("waterloo 2010.WTT"));
        var builder = new SimKnowledgeBuilder("waterloo");
        builder.AddTimetable(waterloo.ToWorkingTimetable());
        var knowledge = builder.Build();

        // A train with at least three passes between its first and last times.
        var train = waterloo.Trains.First(t => t.Trips.Count() >= 6 && t.Trips.Skip(1).SkipLast(1).All(p => p.IsPassTime && p.DepPassTime is not null));
        var trips = train.Trips.ToList();
        var booked = trips.Select(t => t.DepPassTime ?? t.ArrTime).ToList();
        foreach (var trip in trips.Skip(1).SkipLast(1))
        {
            waterloo.Apply(TimetableEdit.Change(trip, t => (t.DepPassTime, t.IsPassTime) = (null, false)));
        }

        var result = BulkTools.AutoFill(waterloo, knowledge, train);

        result.Problems.Should().BeEmpty();
        waterloo.Apply(result.Edit!);
        var filled = train.Trips.Select(t => t.DepPassTime ?? t.ArrTime).ToList();
        for (var i = 1; i < trips.Count - 1; i++)
        {
            Math.Abs(filled[i]!.Value - booked[i]!.Value).Should().BeLessThanOrEqualTo(120, $"{train.Headcode} at {trips[i].Location}: filled {filled[i]}, booked {booked[i]}");
        }
    }

    [Fact]
    public void Set_line_takes_the_line_other_timetables_use_and_leaves_a_tie()
    {
        var builder = new SimKnowledgeBuilder("test");
        builder.AddTimetable(Doc(
            Train("1K01", Trips(Stop("A", null, "08:00", new XElement("Line", "F")), Stop("B", "08:10", null))),
            Train("1K02", Trips(Stop("A", null, "08:20", new XElement("Line", "F")), Stop("B", "08:30", null))),
            Train("1K03", Trips(Stop("A", null, "08:40", new XElement("Line", "S")), Stop("B", "08:50", "08:51", new XElement("Line", "X")), Stop("C", "09:00", null))),
            Train("1K04", Trips(Stop("B", null, "09:30", new XElement("Line", "Y")), Stop("C", "09:40", null))))
            .ToWorkingTimetable());
        var knowledge = builder.Build();
        var d = Doc(Train("2K01", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:11"), Stop("C", "10:20", null))));

        var result = BulkTools.SetLine(knowledge, [Of(d, "2K01")]);

        result.Problems.Should().ContainSingle().Which.Should().Contain("B");
        ApplyUndo(d, result, () => Of(d, "2K01").Trips.First().Line.Should().Be("F"));
    }
}
