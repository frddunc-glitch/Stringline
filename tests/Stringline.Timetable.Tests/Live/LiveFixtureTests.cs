using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Replay;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Live;
using Xunit.Abstractions;

namespace Stringline.Timetable.Tests.Live;

/// <summary>Fred's recordings replayed through <see cref="LiveState"/> against the WTTs they were made with.</summary>
public sealed class LiveFixtureTests(ITestOutputHelper output)
{
    private const string PenrithLog = "penrith-payware.sslog";

    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path(WttFixtureTests.Waterloo)).Timetable);

    private static IEnumerable<GatewayEvent> Events(string path)
    {
        var parser = new MessageParser();
        return SslogRecording.Load(path).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage()));
    }

    private static LiveState Play(WorkingTimetable tt, string path)
    {
        var state = new LiveState(tt);
        foreach (var e in Events(path))
        {
            state.Apply(e);
        }

        return state;
    }

    private void Report(LiveState state)
    {
        var s = state.Summarise();
        output.WriteLine($"{s.Trains} trains, {s.Workings} workings: {s.Matched} matched ({s.MatchedShare:P1}), {s.Pending} pending, {s.Unmatched} unmatched");
        foreach (var train in state.Trains)
        {
            foreach (var w in train.Workings.Where(w => !w.IsMatched))
            {
                output.WriteLine($"  uid {train.SimUid} {w.Headcode}: {w.Status} — {w.Reason}");
            }
        }
    }

    /// <summary>
    /// A matched working must be consistent with its train: the same headcode, and where it reported at points on the
    /// train's own path, close to the booked times there (well inside the 60 min window). SimSig also reports timing
    /// locations the train's schedule leaves out, so those are not checked.
    /// </summary>
    private void AssertConsistent(LiveState state)
    {
        var worst = 0;
        foreach (var train in state.Trains)
        {
            for (var i = 0; i < train.Workings.Count; i++)
            {
                var working = train.Workings[i];
                if (working.Train is not { } id)
                {
                    continue;
                }

                var wtt = state.Timetable.Get(id);
                wtt.Headcode.Should().Be(working.Headcode, "uid {0} working {1}", train.SimUid, i);
                var deviations = train.ActualsOf(i)
                    .SelectMany(a => wtt.Points.Where(p => p.Location == a.Location && p.IsTimed).Select(p => Math.Abs(a.Time - (a.Action == TrainLocationAction.Arrive ? p.Arrive ?? a.Time : p.Depart ?? p.Arrive!.Value))))
                    .ToList();
                if (deviations.Count > 0)
                {
                    worst = Math.Max(worst, (int)deviations.Average());
                    deviations.Average().Should().BeLessThan(20 * 60, "uid {0} {1} matched to {2}", train.SimUid, working.Headcode, id);
                }
            }
        }

        output.WriteLine($"Worst mean deviation of a matched working: {worst / 60} min {worst % 60} s");
    }

    [Theory]
    [InlineData(Fixtures.Waterloo1x, 31, 35)]
    [InlineData(Fixtures.Waterloo4x, 128, 205)]
    [InlineData(Fixtures.WaterlooFailure, 35, 39)]
    public void Waterloo_recordings_match_their_timetable(string log, int trains, int workings)
    {
        var state = Play(Waterloo.Value, Fixtures.Path(log));
        Report(state);

        var summary = state.Summarise();
        summary.Trains.Should().Be(trains);
        summary.Workings.Should().Be(workings);
        summary.MatchedShare.Should().BeGreaterThanOrEqualTo(0.95, "the S5 target");
        summary.Matched.Should().Be(workings, "every working matches today; a drop means a regression");
        AssertConsistent(state);
        state.Clock.AreaId.Should().Be("waterloo");
    }

    [Fact]
    public void Next_workings_are_followed_by_uid()
    {
        var state = Play(Waterloo.Value, Fixtures.Path(Fixtures.Waterloo1x));

        // uid 15 runs as 2F02 and then forms 2H01 (03-…md §6.4).
        var train = state.Find("15")!;
        train.Workings.Select(w => (w.Headcode.Value, w.Train?.Value, w.Method)).Should().Equal(
            ("2F02", "2F02", MatchMethod.HeadcodeAndTime),
            ("2H01", "2H01", MatchMethod.NextWorking));
    }

    [Fact]
    public void A_divided_portion_is_matched_as_formed()
    {
        var state = Play(Waterloo.Value, Fixtures.Path(Fixtures.Waterloo1x));

        // 1W51 (uid 2) divides at Waterloo; the front portion 2P05 gets uid 13 and starts with a departure.
        state.Find("2")!.MatchedTrain.Should().Be(new TrainId("1W51"));
        var portion = state.Find("13")!;
        portion.Actuals[0].Action.Should().Be(TrainLocationAction.Depart);
        portion.MatchedTrain.Should().Be(new TrainId("2P05"));
        portion.Current.Method.Should().Be(MatchMethod.Formed);
    }

    [Fact]
    public void Seeded_trains_sitting_at_their_origin_match()
    {
        var state = Play(Waterloo.Value, Fixtures.Path(Fixtures.Waterloo1x));

        // 2T13 is in the sim from 04:30 and reports "arrive WLOO 04:30:10", but is booked away at 06:42.
        var train = state.Trains.Single(t => t.Workings[0].Headcode == new Headcode("2T13"));
        train.PresentAtStart.Should().BeTrue();
        train.Actuals[0].Should().BeEquivalentTo(new { Location = new Tiploc("WLOO"), Action = TrainLocationAction.Arrive, Time = SimTime.Parse("04:30:10") });
        train.Workings[0].Train.Should().Be(new TrainId("2T13"));
    }

    [Fact]
    public void Suffixed_headcodes_match_their_own_working()
    {
        var state = Play(Waterloo.Value, Fixtures.Path(Fixtures.Waterloo4x));

        state.Find("62")!.Workings.Select(w => w.Train?.Value).Should().Equal("2O03-1", "2K07");
    }

    [Fact]
    public void The_clock_follows_the_recording()
    {
        var state = Play(Waterloo.Value, Fixtures.Path(Fixtures.Waterloo4x));

        state.SessionStart.Should().Be(SimTime.Parse("05:52:31"));
        state.Clock.LastReported.Should().Be(SimTime.Parse("08:05:11"));
        state.Clock.MeasuredSpeed.Should().NotBeNull();
    }

    [Fact]
    public void Td_steps_build_a_berth_map()
    {
        var state = Play(Waterloo.Value, Fixtures.Path(Fixtures.Waterloo1x));

        state.Berths.Should().NotBeEmpty();
        state.Berths.Values.Should().Contain("2C07", "uid 19 left Waterloo as 2C07 two minutes before the end");
    }

    [Fact]
    public async Task A_replay_source_feeds_live_state_end_to_end()
    {
        var source = ReplaySource.Open(Fixtures.Path(Fixtures.Waterloo4x), new ReplayOptions { Speed = double.PositiveInfinity });
        var state = new LiveState(Waterloo.Value);

        await foreach (var e in source.ReadAllAsync())
        {
            state.Apply(e);
        }

        state.Summarise().Should().BeEquivalentTo(Play(Waterloo.Value, Fixtures.Path(Fixtures.Waterloo4x)).Summarise());
    }

    [Fact]
    public void Penrith_picks_the_seeded_versions_for_trains_there_at_the_start()
    {
        var log = Fixtures.LocalPath(PenrithLog);
        var wtt = Fixtures.LocalPath(WttFixtureTests.Penrith);
        if (log is null || wtt is null)
        {
            output.WriteLine("Penrith fixtures not on this machine; skipped.");
            return;
        }

        var state = Play(new WttReader().Read(wtt).Timetable, log);
        Report(state);

        state.Summarise().Matched.Should().Be(9);
        AssertConsistent(state);
        string Matched(string headcode) => state.Trains.Single(t => t.Headcode == new Headcode(headcode)).MatchedTrain!.Value.Value;

        // Penrith has a seeded and an unseeded version of each train in the sim at 14:00, booked seconds apart.
        Matched("105J").Should().Be("105JS");
        Matched("186L").Should().Be("186LS");
        Matched("178H").Should().Be("178HS");
        Matched("AR85").Should().Be("AR85", "it entered later");
    }
}
