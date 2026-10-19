using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using Stringline.Timetable.Live;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>The delay table and its CSV (SES-2, S17).</summary>
public sealed class DelayReportTests
{
    private static readonly TrainId Fast = new("1A01");
    private static readonly TrainId Slow = new("2B01");

    private static SimTime S(string clock) => SimTime.Parse(clock);

    private static PlanSnapshot Snapshot(WorkingPlan plan, string now, params LiveTrain[] live)
    {
        var forecast = Run(plan, now, live: live);
        var clock = SimClockReading.None with { HasTime = true, LastReported = S(now) };
        return new PlanSnapshot(1, plan.Timetable, live, clock, new MatchSummary(live.Length, live.Length, live.Length, 0, 0, new Dictionary<MatchMethod, int>()),
            1, S("05:30"), plan, Forecast: forecast);
    }

    [Fact]
    public void Lists_trains_seen_by_default_and_every_train_when_asked()
    {
        var live = Running("901", "1A01", Dep("A", "06:02"));
        var snapshot = Snapshot(WorkingPlanTests.Plan(), "06:03", live);

        DelayReport.For(snapshot).Trains.Select(t => t.Train).Should().Equal(Fast);
        var all = DelayReport.For(snapshot, DelayReportFilter.All);
        all.Trains.Select(t => t.Train).Should().Equal(Fast, Slow);
        all.Now.Should().Be(S("06:03"));
        all.SessionStart.Should().Be(S("05:30"));
        all.UnmatchedLive.Should().Be(0);

        var slow = all.Trains[1];
        slow.State.Should().Be(DelayTrainState.NotStarted);
        slow.HasActuals.Should().BeFalse();
        (slow.From, slow.To, slow.Start).Should().Be((new Tiploc("A"), new Tiploc("D"), S("07:00")));
        slow.Final!.Location.Should().Be(new Tiploc("D"));
        (slow.FinalDelaySeconds, slow.Final.DelayBasis).Should().Be((0, DelayBasis.Forecast), "a train not started is forecast to run as planned");
    }

    [Fact]
    public void A_running_train_has_actual_delays_then_forecast_ones()
    {
        var live = Running("901", "1A01", Dep("A", "06:02"), Arr("B", "06:08"));

        var train = DelayReport.For(Snapshot(WorkingPlanTests.Plan(), "06:08:20", live)).Trains.Single();

        train.State.Should().Be(DelayTrainState.Running);
        train.LiveUid.Should().Be("901");
        var a = train.Points[0];
        (a.Depart, a.DepartBasis, a.WttDelaySeconds, a.PlanDelaySeconds, a.DelayBasis).Should().Be((S("06:02"), DelayBasis.Actual, 120, 120, DelayBasis.Actual));
        var b = train.Points[1];
        (b.Arrive, b.ArriveBasis, b.DepartBasis).Should().Be((S("06:08"), DelayBasis.Actual, DelayBasis.Forecast));
        (b.WttDelaySeconds, b.DelayBasis).Should().Be((180, DelayBasis.Actual), "a train standing at a stop is compared at its arrival until it leaves");
        var d = train.Points[3];
        (d.Arrive, d.ArriveBasis, d.Depart).Should().Be((S("06:17:30"), DelayBasis.Forecast, (SimTime?)null));
        (d.WttDelaySeconds, d.DelayBasis).Should().Be((150, DelayBasis.Forecast), "a destination is compared at its arrival");

        train.Latest.Should().Be(b);
        (train.Worst, train.WorstDelaySeconds).Should().Be((b, 180));
        (train.Final, train.FinalDelaySeconds).Should().Be((d, 150));
    }

    [Fact]
    public void A_replanned_train_has_delays_against_both_the_WTT_and_the_plan()
    {
        var (plan, _) = WorkingPlanTests.Plan().Apply(new ShiftTrain(Fast, 0, 300));
        var live = Running("901", "1A01", Dep("A", "06:06"), Arr("B", "06:11"), Dep("B", "06:12"));

        var train = DelayReport.For(Snapshot(plan, "06:12:10", live)).Trains.Single();

        train.IsModified.Should().BeTrue();
        var b = train.Points[1];
        (b.PlanArrive, b.PlanDepart, b.WttDepart).Should().Be((S("06:10"), S("06:11"), S("06:06")));
        (b.WttDelaySeconds, b.PlanDelaySeconds, b.DelaySeconds).Should().Be((360, 60, 360), "the report's delay is against the WTT");
    }

    [Fact]
    public void A_clone_is_measured_against_its_plan_and_a_cancelled_train_has_no_times()
    {
        var clone = new TrainId("1A51#c1");
        var (plan, _) = WorkingPlanTests.Plan().ApplyAll([new CloneTrain(Fast, new Headcode("1A51"), 3600, clone), new CancelTrain(Slow)]);

        var report = DelayReport.For(Snapshot(plan, "05:40"), DelayReportFilter.All);

        var copy = report.Trains.Single(t => t.Train == clone);
        copy.Origin.Should().Be(PlanOrigin.Clone);
        copy.Points.Should().OnlyContain(p => p.WttDelaySeconds == null && p.PlanDelaySeconds == 0);
        copy.FinalDelaySeconds.Should().Be(0);
        var cancelled = report.Trains.Single(t => t.Train == Slow);
        cancelled.State.Should().Be(DelayTrainState.Cancelled);
        cancelled.Points.Should().OnlyContain(p => p.Arrive == null && p.Depart == null && p.DelaySeconds == null);
        cancelled.Final.Should().BeNull();
    }

    [Fact]
    public void Points_after_a_short_termination_have_no_delay()
    {
        var (plan, _) = WorkingPlanTests.Plan().Apply(new TerminateAt(Fast, 2));

        var train = DelayReport.For(Snapshot(plan, "05:40"), DelayReportFilter.All).Trains.Single(t => t.Train == Fast);

        train.To.Should().Be(new Tiploc("C"));
        train.Points[3].Runs.Should().BeFalse();
        train.Points[3].DelaySeconds.Should().BeNull();
        train.Final!.Location.Should().Be(new Tiploc("C"));
    }

    [Fact]
    public void The_points_CSV_has_a_row_per_train_and_point()
    {
        var live = Running("901", "1A01", Dep("A", "06:02"), Arr("B", "06:08"));
        var report = DelayReport.For(Snapshot(WorkingPlanTests.Plan(), "06:08:20", live));
        var profile = new StringlineProfile { AreaId = "test", LocationNames = new Dictionary<Tiploc, string> { [new Tiploc("B")] = "Bee, \"Junction\"" } };

        var csv = Write(w => DelayReportCsv.WritePoints(w, report, profile));

        csv.Should().EndWith("\r\n");
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().Equal(
            "headcode,train_id,sim_uid,category,seq,tiploc,location,activity,runs,wtt_arr,wtt_dep,plan_arr,plan_dep,arr,arr_basis,dep,dep_basis,wtt_platform,plan_platform,delay_vs_wtt_s,delay_vs_plan_s,delay_basis",
            "1A01,1A01,901,,1,A,A,start,yes,,06:00:00,,06:00:00,,,06:02:00,actual,,,120,120,actual",
            "1A01,1A01,901,,2,B,\"Bee, \"\"Junction\"\"\",stop,yes,06:05:00,06:06:00,06:05:00,06:06:00,06:08:00,actual,06:08:30,forecast,,,180,180,actual",
            "1A01,1A01,901,,3,C,C,pass,yes,,06:10:00,,06:10:00,,,06:12:30,forecast,,,150,150,forecast",
            "1A01,1A01,901,,4,D,D,terminate,yes,06:15:00,,06:15:00,,06:17:30,forecast,,,,,150,150,forecast");
    }

    [Fact]
    public void The_trains_CSV_has_a_row_per_train()
    {
        var live = Running("901", "1A01", Dep("A", "06:02"), Arr("B", "06:08"));
        var report = DelayReport.For(Snapshot(WorkingPlanTests.Plan(), "06:08:20", live), DelayReportFilter.All);

        var lines = Write(w => DelayReportCsv.WriteTrains(w, report)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines.Should().Equal(
            "headcode,train_id,sim_uid,category,origin,state,replanned,from,to,start,latest_tiploc,latest_time,latest_delay_s,worst_tiploc,worst_delay_s,final_tiploc,final_delay_s,final_basis",
            "1A01,1A01,901,,wtt,running,no,A,D,06:00:00,B,06:08:00,180,B,180,D,150,forecast",
            "2B01,2B01,,,wtt,not started,no,A,D,07:00:00,,,,,,D,0,forecast");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    public void Fields_are_quoted_as_RFC_4180_has_it(string? field, string expected) => DelayReportCsv.Quote(field).Should().Be(expected);

    [Fact]
    public void A_stop_booked_with_only_a_departure_is_a_stop_not_a_start()
    {
        var plan = WorkingPlan.FromTimetable(Of(Train("1A01", points: [Call("A", null, "06:00"), Call("B", null, "06:05"), Call("C", "06:10", null)])));

        var csv = Write(w => DelayReportCsv.WritePoints(w, DelayReport.For(Snapshot(plan, "05:40"), DelayReportFilter.All)));

        csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')[7]).Should().Equal("start", "stop", "terminate");
    }

    [Fact]
    public void Times_after_midnight_carry_a_day_marker()
    {
        var tt = Of(Train("1A01", points: [Call("A", null, "23:58"), Call("B", "24:03", null)]));
        var plan = WorkingPlan.FromTimetable(tt);

        var csv = Write(w => DelayReportCsv.WritePoints(w, DelayReport.For(Snapshot(plan, "23:00"), DelayReportFilter.All)));

        csv.Should().Contain(",00:03:00+1,");
    }

    [Fact]
    public void Waterloo_at_the_end_of_the_4x_recording()
    {
        var tt = new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable;
        var state = new LiveState(tt);
        var parser = new MessageParser();
        foreach (var message in SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo4x)).Messages)
        {
            foreach (var e in parser.Parse(message.ToGatewayMessage()))
            {
                state.Apply(e);
            }
        }

        var plan = WorkingPlan.FromTimetable(tt);
        var live = state.Trains.ToList();
        var now = state.Clock.LastReported;
        var forecast = Forecaster.Run(new ForecastRequest(plan) { LiveTrains = live, Now = now, SessionStart = state.SessionStart, EntryDelays = state.EntryDelays });
        var snapshot = new PlanSnapshot(1, tt, live, SimClockReading.None with { HasTime = true, LastReported = now }, state.Summarise(), 1, state.SessionStart, plan, Forecast: forecast);

        var report = DelayReport.For(snapshot);

        var matched = new HashSet<TrainId>();
        foreach (var t in live)
        {
            for (var w = 0; w < t.Workings.Count; w++)
            {
                if (t.Workings[w].Train is { } id && t.ActualsOf(w).Any(a => a.AtTimingPoint && plan.Get(id).Points.Any(p => p.Location == a.Location)))
                {
                    matched.Add(id);
                }
            }
        }

        report.Trains.Select(t => t.Train).Should().BeEquivalentTo(matched, "every working that reported at a point of its plan is listed");
        report.Trains.Should().OnlyContain(t => t.Latest != null && t.Worst != null);
        report.Trains.Should().OnlyContain(t => Math.Abs(t.WorstDelaySeconds!.Value) < 30 * 60, "delays at Waterloo 4× are minutes, not hours");
        report.Trains.Should().Contain(t => t.State == DelayTrainState.Finished).And.Contain(t => t.State == DelayTrainState.Running);

        var lines = Write(w => DelayReportCsv.WritePoints(w, report)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(1 + report.Trains.Sum(t => t.Points.Count));
        lines.Should().OnlyContain(l => l.Count(c => c == ',') == DelayReportCsv.PointColumns.Length - 1, "no Waterloo field needs quoting");
    }

    private static string Write(Action<StringWriter> write)
    {
        using var writer = new StringWriter();
        write(writer);
        return writer.ToString();
    }
}
