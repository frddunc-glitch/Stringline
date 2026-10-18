using Stringline.Core;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using Stringline.Timetable.Live;

namespace Stringline.Graph.Tests;

/// <summary>Real timetables and recordings drawn end to end: the first "looks like a train graph" check.</summary>
public class GraphFixtureTests
{
    private static readonly Lazy<WttImportResult> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")));
    private static readonly Lazy<WttImportResult> Llangollen = new(() => new WttReader().Read(Fixtures.Path("llangollen 2012 B.WTT")));

    /// <summary>The 4× recording (05:52–08:05 sim) played to 07:00 against the Waterloo WTT.</summary>
    private static LiveState PlayWaterloo4xTo(SimTime until)
    {
        var state = new LiveState(Waterloo.Value.Timetable);
        var parser = new MessageParser();
        foreach (var message in SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo4x)).Messages)
        {
            if (message.Sim > until)
            {
                break;
            }

            foreach (var e in parser.Parse(message.ToGatewayMessage()))
            {
                state.Apply(e);
            }
        }

        return state;
    }

    [Fact]
    public void Waterloo_main_at_seven_has_wtt_and_matched_actuals()
    {
        var tt = Waterloo.Value.Timetable;
        var profile = StringlineProfile.Create(tt);
        var state = PlayWaterloo4xTo(SimTime.FromClock(7, 0));

        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(profile, profile.FindCorridor("main")!) { Timetable = tt, LiveTrains = state.Trains, Now = SimTime.FromClock(7, 0) });

        var wtt = scene.InLayer(GraphLayer.Wtt).ToList();
        var actual = scene.InLayer(GraphLayer.Actual).ToList();
        wtt.Should().HaveCountGreaterThan(1500, "most of the 1 626 trains on the corridor draw");
        actual.Should().HaveCountGreaterThan(50);
        actual.Should().OnlyContain(p => p.Vertices.Count >= 2 && p.TimeMax <= SimTime.FromClock(7, 1).Seconds);
        actual.Count(p => !p.IsUnmatched).Should().BeGreaterThan(actual.Count * 9 / 10);
        scene.Paths.Should().OnlyContain(p => p.Vertices.Zip(p.Vertices.Skip(1)).All(v => v.Second.Time >= v.First.Time), "vertices are in time order");
        scene.Paths.Select(p => p.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public Task Waterloo_main_snapshot()
    {
        var tt = Waterloo.Value.Timetable;
        var profile = StringlineProfile.Create(tt);
        var now = SimTime.FromClock(7, 0);
        var state = PlayWaterloo4xTo(now);
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(profile, profile.FindCorridor("main")!) { Timetable = tt, LiveTrains = state.Trains, Now = now });

        return SkiaGraphRendererTests.VerifyPng(scene, GraphViewport.Around(now, 2 * 3600, 1400, 800));
    }

    [Fact]
    public Task Llangollen_single_line_snapshot()
    {
        var tt = Llangollen.Value.Timetable;
        var profile = StringlineProfile.Create(tt);
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(profile, profile.Corridors[0]) { Timetable = tt });

        return SkiaGraphRendererTests.VerifyPng(scene, GraphViewport.Create(SimTime.FromClock(10, 0).Seconds, SimTime.FromClock(16, 0).Seconds, 1200, 600));
    }
}
