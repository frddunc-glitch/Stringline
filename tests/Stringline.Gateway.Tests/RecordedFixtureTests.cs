using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway.Recording;

namespace Stringline.Gateway.Tests;

/// <summary>
/// The parser against Fred's real SimSig recordings (Waterloo, free sim). Every message type SimSig sent is
/// parsed without loss, and the facts recorded in <c>03-simsig-gateway-integration.md</c> §6 are checked here.
/// </summary>
public class RecordedFixtureTests
{
    private static readonly Dictionary<string, SslogRecording> Loaded = [];

    private static SslogRecording Load(string name)
    {
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(name, out var recording))
            {
                recording = SslogRecording.Load(Fixtures.Path(name));
                Loaded[name] = recording;
            }

            return recording;
        }
    }

    private static List<GatewayEvent> Events(string name)
    {
        var parser = new MessageParser();
        return [.. Load(name).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage()))];
    }

    private static Dictionary<string, int> CountByKind(IEnumerable<GatewayEvent> events) =>
        events.GroupBy(e => e switch
        {
            SignallingStateEvent s => "SG_MSG:" + s.ObjectTypeText,
            TdStepEvent t => t.Kind.ToString(),
            _ => e.GetType().Name,
        }).ToDictionary(g => g.Key, g => g.Count());

    [Theory]
    [InlineData(Fixtures.Waterloo1x, 8437)]
    [InlineData(Fixtures.Waterloo4x, 5384)]
    [InlineData(Fixtures.WaterlooFailure, 8573)]
    public void Recordings_load_completely_and_parse_without_unknown_messages(string name, int frames)
    {
        var recording = Load(name);

        recording.SkippedLines.Should().BeEmpty();
        recording.Messages.Should().HaveCount(frames);
        recording.Messages.Should().OnlyContain(m => m.Body.Length > 0);
        recording.Connections.Select(c => c.State).Should().Equal(
            GatewayConnectionState.Connecting, GatewayConnectionState.Connected, GatewayConnectionState.Disconnected);
        recording.Connections[1].Headers.Should().BeEquivalentTo(new KeyValuePair<string, string>[] { new("version", "1.1"), new("server", "SimSig") });

        Events(name).OfType<UnknownEvent>().Should().BeEmpty();
    }

    [Fact]
    public void Waterloo_1x_has_every_message_type()
    {
        CountByKind(Events(Fixtures.Waterloo1x)).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["SG_MSG:flag"] = 35,
            ["SG_MSG:point"] = 740,
            ["SG_MSG:route"] = 985,
            ["SG_MSG:signal"] = 2602,
            ["SG_MSG:track"] = 2451,
            ["SG_MSG:ulc"] = 3206,
            ["TrainLocationEvent"] = 614,
            ["Step"] = 465,
            ["Cancel"] = 112,
            ["Interpose"] = 45,
            ["ClockEvent"] = 100,
            ["TrainDelayEvent"] = 54,
            ["TimetableSummaryEvent"] = 1,
        });
    }

    [Fact]
    public void Waterloo_4x_has_movements_and_clock_only()
    {
        CountByKind(Events(Fixtures.Waterloo4x)).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["TrainLocationEvent"] = 4779,
            ["TrainDelayEvent"] = 459,
            ["ClockEvent"] = 146,
        });
    }

    [Fact]
    public void Waterloo_failure_counts()
    {
        var counts = CountByKind(Events(Fixtures.WaterlooFailure));

        counts["TrainLocationEvent"].Should().Be(644);
        counts["SG_MSG:track"].Should().Be(2479);
        counts.Values.Sum().Should().Be(10099 + 644 + 489 + 107 + 52 + 99 + 56);
    }

    [Fact]
    public void Train_location_at_a_timing_point_a_signal_and_a_departure()
    {
        var moves = Events(Fixtures.Waterloo1x).OfType<TrainLocationEvent>().ToList();

        // Trains already in the sim at the start "arrive" at their first location; no platform on arrival.
        moves[0].Should().BeEquivalentTo(new { Headcode = new Headcode("1W51"), Uid = "2", Action = TrainLocationAction.Arrive, Location = new Tiploc("WLOO"), Platform = "", Time = new SimTime(16210), AspectPassed = (int?)null });

        // Signal passes carry the aspects; the platform is then empty.
        moves.Where(m => m.AspectPassed is not null).Should().OnlyContain(m => m.Action == TrainLocationAction.Pass && m.Location.Code.StartsWith('S') && m.Platform.Length == 0);

        // Departures report the platform (or line); 2P05 is a divided portion and gets its own uid.
        moves.Should().ContainEquivalentOf(new { Headcode = new Headcode("2P05"), Uid = "13", Action = TrainLocationAction.Depart, Location = new Tiploc("WLOO"), Platform = "11", Time = new SimTime(18001) });
    }

    [Fact]
    public void Uid_persists_when_a_train_forms_its_next_working()
    {
        var headcodes = Events(Fixtures.Waterloo1x).OfType<TrainLocationEvent>()
            .Where(m => m.Uid == "15")
            .Select(m => m.Headcode.Value)
            .Distinct();

        headcodes.Should().Equal("2F02", "2H01");
    }

    [Fact]
    public void Headcodes_with_a_suffix_are_kept_whole()
    {
        Events(Fixtures.Waterloo4x).OfType<TrainLocationEvent>()
            .Should().Contain(m => m.Headcode == new Headcode("2O03-1") && m.Uid == "62");
    }

    [Fact]
    public void Train_delay_has_no_uid_at_waterloo()
    {
        var delays = Events(Fixtures.Waterloo4x).OfType<TrainDelayEvent>().ToList();

        delays.Should().OnlyContain(d => d.Uid.Length == 0);
        delays[0].Should().BeEquivalentTo(new { Headcode = new Headcode("5C09"), DelaySeconds = -76 });
    }

    [Fact]
    public void Clock_messages_cover_placeholder_normal_fast_and_flat_out()
    {
        var clocks = Events(Fixtures.Waterloo1x).OfType<ClockEvent>().ToList();
        var fast = Events(Fixtures.Waterloo4x).OfType<ClockEvent>().ToList();
        var flatOut = Events(Fixtures.WaterlooFailure).OfType<ClockEvent>().ToList();

        // Before the sim is started SimSig answers idrequest with clock 0 at a 1000 ms interval.
        clocks[0].Should().BeEquivalentTo(new { AreaId = "waterloo", Clock = new SimTime(0), IntervalMs = 1000, Paused = false });
        clocks[1].Should().BeEquivalentTo(new { Clock = new SimTime(16200), IntervalMs = 500, Paused = true, SpeedRatio = 1.0 });
        fast.Select(c => c.IntervalMs).Distinct().Should().Equal(500, 124, 31);
        flatOut.Last().Should().BeEquivalentTo(new { IntervalMs = 1, IsMaximumSpeed = true, Paused = true });

        // Running clocks mostly tick on whole sim minutes; pause, resume and speed changes land on any second.
        var running = clocks.Where(c => !c.Paused && c.Clock.Seconds > 16200).ToList();
        running.Count(c => c.Clock.Second == 0).Should().BeGreaterThan(running.Count * 9 / 10);
    }

    [Fact]
    public void Td_berth_steps()
    {
        var steps = Events(Fixtures.Waterloo1x).OfType<TdStepEvent>().ToList();

        steps.First(s => s.Kind == TdStepKind.Step).Should().BeEquivalentTo(new { AreaId = "waterloo", From = "0021", To = "R021", Description = "1W51", Time = (SimTime?)new SimTime(16200) });
        steps.First(s => s.Kind == TdStepKind.Cancel).Should().BeEquivalentTo(new { From = "P198", To = "", Description = "03", Time = (SimTime?)new SimTime(16814) });
        steps.First(s => s.Kind == TdStepKind.Interpose).Should().BeEquivalentTo(new { From = "", To = "4304", Description = "2F02", Time = (SimTime?)new SimTime(16621) });
    }

    [Fact]
    public void Signalling_objects_have_the_recorded_fields_as_strings()
    {
        var sg = Events(Fixtures.Waterloo1x).OfType<SignallingStateEvent>().ToList();

        Fields(sg, SignallingObjectType.Flag).Should().BeEquivalentTo("state");
        Fields(sg, SignallingObjectType.Point).Should().BeEquivalentTo("dn", "dr", "cn", "cr", "kn", "kr", "locked");
        Fields(sg, SignallingObjectType.Route).Should().BeEquivalentTo("is_set");
        Fields(sg, SignallingObjectType.Signal).Should().BeEquivalentTo("aspect", "bpull", "rset", "appr_lock", "lp", "auto", "trts", "stackN", "stackX");
        Fields(sg, SignallingObjectType.Track).Should().BeEquivalentTo("clear");
        Fields(sg, SignallingObjectType.Subroute).Should().BeEquivalentTo("locked", "overlap");

        var signal = sg.First(s => s.ObjectId == "S1");
        signal.GetInt32("aspect").Should().Be(0);
        signal.GetBoolean("lp").Should().BeTrue();
        sg.First(s => s.ObjectId == "P5000A").GetBoolean("dn").Should().BeTrue();
        sg.First(s => s.ObjectId == "U1000-BA").ObjectType.Should().Be(SignallingObjectType.Subroute);
    }

    [Fact]
    public void Snapshot_is_one_body_with_thousands_of_duplicate_keys()
    {
        var parser = new MessageParser();
        var snapshot = Load(Fixtures.Waterloo1x).Messages.Single(m => m.Body.Length > 100_000 && m.Topic == GatewayTopics.TrainDescriber);

        var events = parser.Parse(snapshot.ToGatewayMessage());

        events.Should().HaveCount(2974).And.AllBeOfType<SignallingStateEvent>();
        events.Cast<SignallingStateEvent>().Select(e => e.ObjectId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Ttsummary_reply_lists_every_timetabled_train()
    {
        var summary = Events(Fixtures.Waterloo1x).OfType<TimetableSummaryEvent>().Single();

        summary.Topic.Should().Be(GatewayTopics.TrainMovementSummary);
        summary.Trains.Should().HaveCount(1779);
        summary.Trains.Count(t => t.Status == TrainSummaryStatus.Ok).Should().Be(1757);
        summary.Trains.Where(t => t.Status == TrainSummaryStatus.Active).Should().HaveCount(11).And.OnlyContain(t => t.Location != null);
        summary.Trains.Count(t => t.Status == TrainSummaryStatus.Done).Should().Be(11);
        summary.Trains.Should().OnlyContain(t => t.Uid.Length == 0 && t.DelaySeconds == 0);
        summary.Trains[0].Should().BeEquivalentTo(new { Headcode = new Headcode("1W51"), Location = (Tiploc?)new Tiploc("WLOO"), LocationName = "Waterloo", Platform = "" });
    }

    [Fact]
    public void Track_circuit_failure_shows_only_as_a_track_occupied_with_no_train()
    {
        var t1620 = Events(Fixtures.WaterlooFailure).OfType<SignallingStateEvent>()
            .Where(e => e.ObjectId == "T1620" && e.Time is not null)
            .Select(e => (e.Time!.Value.Seconds, Clear: e.GetBoolean("clear")))
            .ToList();

        // SimSig sends no failure message: the track just shows occupied for half an hour.
        t1620.Should().ContainInOrder((19331, false), (21275, true));
        t1620.SkipWhile(e => e.Seconds < 19331).Skip(1).First().Should().Be((21275, (bool?)true));
    }

    [Fact]
    public void Payware_recording_parses_if_present()
    {
        if (Fixtures.LocalPath("penrith-payware.sslog") is not { } path)
        {
            return;
        }

        var parser = new MessageParser();
        var events = SslogRecording.Load(path).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();

        events.OfType<UnknownEvent>().Should().BeEmpty();
        events.OfType<TrainLocationEvent>().Should().NotBeEmpty();
    }

    private static IEnumerable<string> Fields(IEnumerable<SignallingStateEvent> events, SignallingObjectType type) =>
        events.Where(e => e.ObjectType == type).SelectMany(e => e.Fields.Keys).Distinct();
}
