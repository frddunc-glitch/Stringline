using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using Stringline.Timetable.Live;
using Xunit.Abstractions;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>S36's baseline (#17): how long trains go between reports on the corridors (<see cref="ReportGaps"/>).</summary>
public sealed class ReportGapsTests(ITestOutputHelper output)
{
    [Fact]
    public void Gaps_are_between_places_and_unbooked_reports_are_counted()
    {
        var tt = Of(Train("1A01", points: [Call("A", null, "06:00"), Call("C", "06:10", "06:11"), Call("D", "06:15", null)]));
        var live = Running("10", "1A01", Dep("A", "06:00"), Passed("B", "06:04"), Arr("C", "06:10"), Dep("C", "06:11"), Arr("D", "06:15"), Passed("X", "06:16"));
        live = live with { Actuals = live.Actuals.Insert(1, new ActualEvent(new Tiploc("S12"), Core.Events.TrainLocationAction.Pass, SimTime.Parse("06:02"), "", false, 0)) };

        var gaps = ReportGaps.Measure(tt, [live], [Line("main", 180, false, "A", "B", "C", "D")]);

        gaps.Workings.Should().Be(1);
        gaps.TimingReports.Should().Be(6);
        gaps.UnbookedReports.Should().Be(2, "B and X");
        gaps.UnbookedOnCorridor.Should().Be(1, "X is not on the corridor");
        gaps.Booked.Should().Be(new GapStats(2, 240, 600, 600), "A to C, then C (from its departure) to D; the dwell at C is not a gap");
        gaps.Timing.Should().Be(new GapStats(3, 240, 360, 360));
        gaps.Signals.Should().Be(new GapStats(4, 120, 360, 360), "the signal at 06:02 splits A–B, and X after D is off the corridor");
    }

    /// <summary>
    /// The baseline on Waterloo's 1× recording, so S38 can show its gain: about 30 % of reports are at timing
    /// locations the train's WTT leaves out, and counting them nearly halves the median gap.
    /// </summary>
    [Fact]
    public void Waterloo_baseline()
    {
        var tt = new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable;
        var parser = new MessageParser();
        var state = new LiveState(tt);
        foreach (var e in SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo1x)).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())))
        {
            state.Apply(e);
        }

        var gaps = ReportGaps.Measure(tt, state.Trains, StringlineProfile.Create(tt).Corridors);
        output.WriteLine($"{gaps.Workings} workings, {gaps.TimingReports} reports, {gaps.UnbookedReports} unbooked; booked {gaps.Booked}, timing {gaps.Timing}, signals {gaps.Signals}");

        gaps.Workings.Should().Be(35);
        gaps.UnbookedShare.Should().BeInRange(0.25, 0.35);
        gaps.Booked.Median.Should().Be(122);
        gaps.Booked.P95.Should().Be(347);
        gaps.Timing.Median.Should().Be(83);
        gaps.Timing.P95.Should().Be(137);
        gaps.Signals.Median.Should().Be(23);
        gaps.Signals.P95.Should().Be(54);
    }
}
