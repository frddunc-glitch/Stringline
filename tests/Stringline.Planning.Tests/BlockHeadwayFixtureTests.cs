using Stringline.Core.Events;
using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Network;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using Stringline.Timetable.Live;
using Xunit.Abstractions;

namespace Stringline.Planning.Tests;

/// <summary>
/// Block headways and conflicts at signals (S39) on Waterloo's recordings, with the places learned from the 4 h one:
/// the headway conflicts before (the WTT's headways, S27) and after (the blocks'), and the catching-up found at signals.
/// </summary>
[Collection("Forecast fixtures")] // heavy: not alongside the forecast speed test
public sealed class BlockHeadwayFixtureTests(ITestOutputHelper output)
{
    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable);

    private static readonly Lazy<StringlineProfile> Learned = new(() =>
    {
        var tt = Waterloo.Value;
        var learner = new SignalLearner();
        foreach (var e in Events(Fixtures.Waterloo4h))
        {
            learner.Observe(e);
        }

        return SignalLearning.Apply(StringlineProfile.Create(tt), learner.Learn(tt), "4h").Profile;
    });

    private static List<GatewayEvent> Events(string fixture)
    {
        var parser = new MessageParser();
        return SslogRecording.Load(Fixtures.Path(fixture)).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
    }

    /// <summary>The conflicts at the end of a recording with this network.</summary>
    private static ConflictSet AtEnd(string recording, ForecastNetwork network)
    {
        var timetable = Waterloo.Value;
        var plan = WorkingPlan.FromTimetable(timetable);
        var state = new LiveState(timetable);
        foreach (var e in Events(recording))
        {
            state.Apply(e);
        }

        var forecast = Forecaster.Run(new ForecastRequest(plan)
        {
            LiveTrains = state.Trains.ToArray(),
            Now = state.Clock.Reading.HasTime ? state.Clock.Reading.LastReported : null,
            SessionStart = state.SessionStart,
            EntryDelays = new Dictionary<Core.Headcode, int>(state.EntryDelays),
            Network = network,
            LineSide = state.LineSide,
        });
        return ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = network });
    }

    /// <summary>
    /// Waterloo's sections take their headways from the blocks where signals are placed, four-aspect throughout (the
    /// corridor shows double yellows, Fred), and they are near the WTT's spacing (S27: 1:30–2:54).
    /// </summary>
    [Fact]
    public void Waterloo_sections_take_block_headways()
    {
        var sections = Learned.Value.Corridors.SelectMany(c => c.Sections).ToList();
        foreach (var s in sections)
        {
            output.WriteLine($"{s.From}–{s.To}: {s.DefaultHeadwaySeconds} s {s.HeadwaySource} (WTT {s.BookedHeadwaySeconds}, blocks {s.BlockHeadwaySeconds} from {s.BlockSignals} signals, {s.BlockAspects}-aspect)");
        }

        var blocks = sections.Where(s => s.HeadwaySource == HeadwaySource.Blocks).ToList();
        blocks.Should().HaveCountGreaterThan(10);
        blocks.Should().OnlyContain(s => s.BlockAspects == 4 && s.DefaultHeadwaySeconds >= 60 && s.DefaultHeadwaySeconds <= 180);
        sections.First(s => s.From.Code == "WLOO" && s.To.Code == "INTJN" && s.HeadwaySource == HeadwaySource.Blocks).DefaultHeadwaySeconds.Should().BeInRange(100, 160);
        sections.Where(s => s.BlockHeadwaySeconds is null).Should().OnlyContain(s => s.HeadwaySource != HeadwaySource.Blocks);
    }

    /// <summary>
    /// S39: with the block headways, headway conflicts at the end of each recording against the WTT's (S27, with the
    /// same learned places in the forecast). False ones fall and none appears. On the failure recording 2O05/2U07 stays;
    /// 5S06/5O05 (108 s apart at QTRDBAT–WESTLON, where the WTT gives 2:30 and the blocks need 1:25) goes, as Fred
    /// accepted: the learned signalling clears them.
    /// </summary>
    [Theory]
    [InlineData(Fixtures.Waterloo4x, 12, 8, new[] { "6L49/6G15", "2C85/2S91", "1B92/2L90" })]
    [InlineData(Fixtures.WaterlooFailure, 2, 1, new[] { "2O05/2U07" })]
    [InlineData(Fixtures.Waterloo1x, 0, 0, new string[] { })]
    public void Block_headways_drop_false_conflicts_and_add_none(string recording, int before, int after, string[] kept)
    {
        var tt = Waterloo.Value;
        var learned = Learned.Value;
        var wtt = ForecastNetwork.Build(StringlineProfile.Create(tt).Corridors, tt).WithPlaces(UsablePlaces.Of(learned.Places));
        var blocks = ForecastNetwork.For(learned, tt);
        var old = AtEnd(recording, wtt).All.Where(c => c.Kind is ConflictKind.Headway or ConflictKind.Overtake).ToList();
        var now = AtEnd(recording, blocks).All.Where(c => c.Kind is ConflictKind.Headway or ConflictKind.Overtake).ToList();
        output.WriteLine($"{recording}: {old.Count} with the WTT's headways, {now.Count} with the blocks'");
        foreach (var c in now)
        {
            output.WriteLine($"  {c} (headway {c.HeadwaySeconds} s, {c.HeadwaySource}) {c.Summary}");
        }

        old.Should().HaveCount(before);
        now.Should().HaveCount(after);
        static string Pair(Conflict c) => $"{c.HeadcodeA}/{c.HeadcodeB}";
        now.Select(Pair).Should().BeSubsetOf(old.Select(Pair), "no new conflict appears");
        if (after > 0)
        {
            now.Select(Pair).Should().Contain(kept);
            now.Should().Contain(c => c.HeadwaySource == HeadwaySource.Blocks);
        }
    }

    /// <summary>
    /// Llangollen's single line is as before with signals placed in every section (each way, a place running through
    /// and one starting): opposing trains are still judged at the ends, and none of its sections gets a block headway
    /// from one signal with none placed ahead.
    /// </summary>
    [Fact]
    public void Llangollen_single_line_is_unchanged()
    {
        var tt = new WttReader().Read(Fixtures.Path("llangollen 2012 B.WTT")).Timetable;
        var created = StringlineProfile.Create(tt);
        var profile = created with { Corridors = [.. created.Corridors.Select(c => c with { Sections = [.. c.Sections.Select(s => s with { Tracks = Section.SingleLine })] })] };
        var places = SignallingPlaces.Empty;
        foreach (var s in profile.Corridors.SelectMany(c => c.Sections))
        {
            foreach (var (from, to) in new[] { (s.From, s.To), (s.To, s.From) })
            {
                places = places.SetByHand(SignallingKind.Signal, $"S{places.Signals.Count + 1}", from, to, 0.5)
                    .SetByHand(SignallingKind.Signal, $"S{places.Signals.Count + 1}", from, to, 0.6, RunKind.Starting);
            }
        }

        var plain = ForecastNetwork.For(profile, tt);
        var signals = ForecastNetwork.For(profile.WithPlaces(places), tt);
        signals.Sections.Should().Contain(s => s.SingleLine && s.DownSignals.Count > 0);
        signals.Sections.Select(s => (s.HeadwaySeconds, s.HeadwaySource)).Should().Equal(plain.Sections.Select(s => (s.HeadwaySeconds, s.HeadwaySource)));

        var plan = WorkingPlan.FromTimetable(tt);
        ConflictSet Day(ForecastNetwork network) => ConflictDetector.Detect(new ConflictRequest(plan, Forecaster.Run(new ForecastRequest(plan) { Network = network })) { Network = network });
        Day(signals).All.Select(c => c.ToString()).Should().Equal(Day(plain).All.Select(c => c.ToString()));
        Forecaster.Run(new ForecastRequest(plan) { Network = signals }).Trains.Select(t => t.KnockOnSeconds)
            .Should().Equal(Forecaster.Run(new ForecastRequest(plan) { Network = plain }).Trains.Select(t => t.KnockOnSeconds));
    }
}
