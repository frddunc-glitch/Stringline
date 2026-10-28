using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Editing.Testing;
using Stringline.Editing.Validation;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;
using static Stringline.Editing.Tests.Draft;

namespace Stringline.Editing.Tests;

/// <summary>S48: what a test run of a timetable in SimSig showed, from a recording of it.</summary>
public sealed class TimetableTestReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private static ClockEvent Clock(string time) => new(T0, "test", SimTime.Parse(time), 500, false);

    private static TrainLocationEvent At(string uid, string headcode, string location, string time, TrainLocationAction action, string platform = "") =>
        new(T0, new Headcode(headcode), uid, action, new Tiploc(location), platform, SimTime.Parse(time));

    [Theory]
    [InlineData(Fixtures.Waterloo1x)]
    [InlineData(Fixtures.Waterloo4x)]
    public void Waterloo_played_against_its_own_timetable_has_no_train_that_never_appeared(string recording)
    {
        var import = WttDocument.Load(Fixtures.Path("waterloo 2010.WTT")).Import();
        var parser = new MessageParser();
        var events = SslogRecording.Load(Fixtures.Path(recording)).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage()));

        var issues = TimetableTestReport.Build(import, events);

        issues.Where(i => i.Code == IssueCodes.TestNeverAppeared).Select(i => $"{i.Headcode}: {i.Message}").Should().BeEmpty("every train of the recording's span ran");
        issues.Where(i => i.Code == IssueCodes.TestUnmatched).Should().BeEmpty("the recordings match 100 %");
    }

    [Fact]
    public void A_run_booked_too_short_a_train_that_never_came_an_unmatched_working_and_a_shared_platform_are_reported()
    {
        // A→B is booked 5 min; both trains take 8. 2X09 never comes. 9Z99 is not in the timetable. 1A01 and 1A03 share P1 at B.
        var import = Doc(
            Train("1A01", Trips(Stop("A", null, "10:00"), Stop("B", "10:05", "10:10", new System.Xml.Linq.XElement("Platform", "1")), Stop("C", "10:20", null))),
            Train("1A03", Trips(Stop("A", null, "10:06"), Stop("B", "10:11", "10:16", new System.Xml.Linq.XElement("Platform", "2")), Stop("C", "10:26", null))),
            Train("2X09", Trips(Stop("A", null, "10:15"), Stop("C", "10:30", null)))).Import();
        GatewayEvent[] events =
        [
            Clock("09:59"),
            At("1", "1A01", "A", "10:00", TrainLocationAction.Depart),
            At("1", "1A01", "B", "10:08", TrainLocationAction.Arrive, "1"),
            At("2", "1A03", "A", "10:06", TrainLocationAction.Depart),
            At("1", "1A01", "B", "10:15", TrainLocationAction.Depart, "1"),
            At("2", "1A03", "B", "10:14", TrainLocationAction.Arrive, "1"),
            At("2", "1A03", "B", "10:18", TrainLocationAction.Depart, "1"),
            At("3", "9Z99", "A", "10:20", TrainLocationAction.Pass),
            At("1", "1A01", "C", "10:22", TrainLocationAction.Arrive),
            At("2", "1A03", "C", "10:28", TrainLocationAction.Arrive),
            Clock("10:45"),
        ];

        var issues = TimetableTestReport.Build(import, events);

        issues.Where(i => i.Code == IssueCodes.TestNeverAppeared).Should().ContainSingle().Which.Headcode.Should().Be("2X09");
        issues.Where(i => i.Code == IssueCodes.TestUnmatched).Should().ContainSingle().Which.Message.Should().StartWith("9Z99");
        var slow = issues.Where(i => i.Code == IssueCodes.TestSlowSection).ToList();
        slow.Select(i => i.Headcode).Should().BeEquivalentTo(["1A01", "1A03"]);
        slow[0].Message.Should().StartWith("A–B").And.Contain("add about +3:00");
        issues.Where(i => i.Code == IssueCodes.TestPlatformClash).Should().ContainSingle().Which.Message.Should().Contain("Platform 1 at B: 1A01 and 1A03");

        // The quick fix moves the train from B on by 3 min.
        var fix = slow.Single(i => i.Headcode == "1A01").QuickFix!;
        import.Document.Apply(fix.Edit);
        import.Document.Trains.First().Trips.Select(t => t.ArrTime ?? t.DepPassTime).Should().Equal(new SimTime(T("10:00")), new SimTime(T("10:08")), new SimTime(T("10:23")));
    }
}
